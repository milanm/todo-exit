using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using TodoExit.Core.Archive;

namespace TodoExit.Core.Todoist;

/// <summary>A per-command error Todoist returned, or a command that was skipped because its parent failed.</summary>
public sealed record TransferError(string LossKind, string CommandType, string Key, string List, string? Task, string? Tag, string Message);

/// <summary>
/// What one transfer has done so far. Serializable, holds no token. The CLI saves it as JSON after every request;
/// a resumed run rebuilds the same plan from the same archive and these settings and skips what is done.
/// </summary>
/// <remarks>
/// Command uuids and temp_ids are derived from <see cref="TransferId"/> and the plan keys
/// (see <see cref="DeriveUuid"/>), so sending a request again never creates a second object.
/// </remarks>
public sealed class TodoistTransferState
{
    public const string FormatName = "todo-exit-todoist-transfer";
    public const int CurrentVersion = 1;

    public string TransferId { get; init; } = Guid.NewGuid().ToString("D");

    /// <summary>SHA-256 of the archive; a resume refuses a different archive.</summary>
    public string ArchiveFingerprint { get; init; } = string.Empty;

    public DateTimeOffset StartedAt { get; init; }

    /// <summary>The settings the plan was built with. A resume uses these, not the command line.</summary>
    public TodoistPlanSettings Settings { get; init; } = new();

    /// <summary>Projects that existed in the account before this transfer started. Never touched.</summary>
    public HashSet<string> PreexistingProjectIds { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Plan key (project or task) to the real Todoist id.</summary>
    public Dictionary<string, string> Ids { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Uuids of commands that have a final result (ok, error or skipped).</summary>
    public HashSet<string> Done { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Uuids of every command ever sent. A project whose command was sent may exist without a known id.</summary>
    public HashSet<string> Sent { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Uuids of the request in flight. Non-empty after a crash or a lost response: sent again first.</summary>
    public List<string> Pending { get; init; } = new();

    /// <summary>Keys acknowledged as created whose id the response did not include. Found by reading the account.</summary>
    public HashSet<string> Unresolved { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Keys that could not be created, with the reason. Their children are skipped.</summary>
    public Dictionary<string, string> Failed { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Task keys completed in Todoist by this transfer.</summary>
    public HashSet<string> Completed { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Task keys that got a reminder.</summary>
    public HashSet<string> Reminders { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Set after the first PREMIUM_ONLY answer to a reminder; later reminders are not sent.</summary>
    public bool RemindersRefused { get; set; }

    public List<TransferError> Errors { get; init; } = new();

    public bool Finished { get; set; }

    public override string ToString() => $"TodoistTransferState({TransferId})";

    public static string Fingerprint(TodoArchiveDocument document) =>
        Convert.ToHexStringLower(SHA256.HashData(document.ToUtf8Bytes(indented: false)));

    /// <summary>A name-based UUID (version 5 layout, SHA-256) of transfer id, purpose and plan key.</summary>
    public static string DeriveUuid(string transferId, string purpose, string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(transferId + "\n" + purpose + "\n" + key));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        var hex = Convert.ToHexStringLower(hash, 0, 16);
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..32]}";
    }

    public string Uuid(string commandType, string key) => DeriveUuid(TransferId, commandType, key);

    public string TempId(string key) => DeriveUuid(TransferId, "temp_id", key);

    public byte[] ToUtf8Bytes()
    {
        var root = new JsonObject
        {
            ["format"] = FormatName,
            ["version"] = CurrentVersion,
            ["transferId"] = TransferId,
            ["archiveFingerprint"] = ArchiveFingerprint,
            ["startedAt"] = StartedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["settings"] = Settings.ToJson(),
            ["finished"] = Finished,
            ["remindersRefused"] = RemindersRefused,
            ["preexistingProjectIds"] = Array(PreexistingProjectIds.Order(StringComparer.Ordinal)),
            ["ids"] = new JsonObject(Ids.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value)))),
            ["pending"] = Array(Pending),
            ["unresolved"] = Array(Unresolved.Order(StringComparer.Ordinal)),
            ["failed"] = new JsonObject(Failed.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value)))),
            ["completed"] = Array(Completed.Order(StringComparer.Ordinal)),
            ["reminders"] = Array(Reminders.Order(StringComparer.Ordinal)),
            ["done"] = Array(Done.Order(StringComparer.Ordinal)),
            ["sent"] = Array(Sent.Order(StringComparer.Ordinal)),
        };

        var errors = new JsonArray();
        foreach (var e in Errors)
        {
            errors.Add((JsonNode)new JsonObject
            {
                ["lossKind"] = e.LossKind,
                ["commandType"] = e.CommandType,
                ["key"] = e.Key,
                ["list"] = e.List,
                ["task"] = e.Task,
                ["tag"] = e.Tag,
                ["message"] = e.Message,
            });
        }

        root["errors"] = errors;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            NewLine = "\n",
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            root.WriteTo(writer);
        }

        return stream.ToArray();
    }

    /// <summary>Throws <see cref="InvalidDataException"/> when the file is not a transfer state.</summary>
    public static TodoistTransferState Parse(byte[] utf8)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(utf8) as JsonObject ?? throw new InvalidDataException("The transfer state must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The transfer state is not valid JSON: {ex.Message}", ex);
        }

        if (Str(root, "format") != FormatName)
        {
            throw new InvalidDataException($"This is not a todo-exit Todoist transfer state (expected format '{FormatName}').");
        }

        if (root["version"] is JsonValue vv && vv.TryGetValue<int>(out var version) && version > CurrentVersion)
        {
            throw new InvalidDataException($"Transfer state version {version} is newer than this tool supports ({CurrentVersion}).");
        }

        if (Str(root, "transferId") is not { Length: > 0 } transferId)
        {
            throw new InvalidDataException("The transfer state has no transferId.");
        }

        var state = new TodoistTransferState
        {
            TransferId = transferId,
            ArchiveFingerprint = Str(root, "archiveFingerprint") ?? string.Empty,
            StartedAt = DateTimeOffset.TryParse(Str(root, "startedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var s)
                ? s
                : DateTimeOffset.MinValue,
            Settings = TodoistPlanSettings.FromJson(root["settings"] as JsonObject),
            Finished = root["finished"] is JsonValue f && f.TryGetValue<bool>(out var fin) && fin,
            RemindersRefused = root["remindersRefused"] is JsonValue r && r.TryGetValue<bool>(out var rr) && rr,
            PreexistingProjectIds = Set(root["preexistingProjectIds"]),
            Ids = Map(root["ids"]),
            Pending = Set(root["pending"]).ToList(),
            Unresolved = Set(root["unresolved"]),
            Failed = Map(root["failed"]),
            Completed = Set(root["completed"]),
            Reminders = Set(root["reminders"]),
            Done = Set(root["done"]),
            Sent = Set(root["sent"]),
        };

        // Keep the pending order as written.
        state.Pending.Clear();
        if (root["pending"] is JsonArray pending)
        {
            state.Pending.AddRange(pending.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var x) ? x : null).OfType<string>());
        }

        if (root["errors"] is JsonArray errors)
        {
            foreach (var e in errors.OfType<JsonObject>())
            {
                state.Errors.Add(new TransferError(
                    Str(e, "lossKind") ?? "todoist-error", Str(e, "commandType") ?? string.Empty, Str(e, "key") ?? string.Empty,
                    Str(e, "list") ?? string.Empty, Str(e, "task"), Str(e, "tag"), Str(e, "message") ?? string.Empty));
            }
        }

        return state;
    }

    private static JsonArray Array(IEnumerable<string> values) => new(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());

    private static HashSet<string> Set(JsonNode? node) =>
        node is JsonArray a
            ? a.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

    private static Dictionary<string, string> Map(JsonNode? node)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (node is JsonObject o)
        {
            foreach (var (k, v) in o)
            {
                if (v is JsonValue value && value.TryGetValue<string>(out var s))
                {
                    map[k] = s;
                }
            }
        }

        return map;
    }

    internal static string? Str(JsonObject? o, string name) =>
        o?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
