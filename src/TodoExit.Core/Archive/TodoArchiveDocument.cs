using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using TodoExit.Core.Model;

namespace TodoExit.Core.Archive;

/// <summary>
/// The lossless archive: raw Microsoft Graph JSON wrapped in a small envelope.
/// The typed model is always derived from this document, never the other way round,
/// so properties the parser does not know about survive a read/write round trip.
/// </summary>
public sealed class TodoArchiveDocument
{
    public const string FormatName = "todo-exit-archive";
    public const int CurrentVersion = 1;

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private TodoArchiveDocument(JsonObject root)
    {
        Root = root;
    }

    /// <summary>The raw archive object. Shape: { format, version, exportedAt, lists: [ { list, tasks } ] }.</summary>
    public JsonObject Root { get; }

    public JsonArray Lists => Root["lists"] as JsonArray ?? new JsonArray();

    /// <summary>What the read could not get from Graph. Empty when the archive has no "readNotes" property.</summary>
    public ReadNotes ReadNotes => ReadNotes.FromJson(Root[ReadNotes.PropertyName]);

    /// <summary>Creates a new archive from raw Graph objects.</summary>
    /// <param name="notes">Written as "readNotes" only when it has something to note.</param>
    public static TodoArchiveDocument Create(DateTimeOffset exportedAt, IEnumerable<ArchivedList> lists, ReadNotes? notes = null)
    {
        var listArray = new JsonArray();
        foreach (var entry in lists)
        {
            var tasks = new JsonArray();
            foreach (var task in entry.Tasks)
            {
                tasks.Add((JsonNode)task);
            }

            listArray.Add((JsonNode)new JsonObject
            {
                ["list"] = entry.List,
                ["tasks"] = tasks,
            });
        }

        var root = new JsonObject
        {
            ["format"] = FormatName,
            ["version"] = CurrentVersion,
            ["exportedAt"] = exportedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        };

        if (notes is { IsEmpty: false })
        {
            root[ReadNotes.PropertyName] = notes.ToJson();
        }

        root["lists"] = listArray;
        return new TodoArchiveDocument(root);
    }

    /// <summary>Parses an archive from JSON text. Throws <see cref="InvalidDataException"/> when the envelope is wrong.</summary>
    public static TodoArchiveDocument Parse(string json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json, documentOptions: ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The archive is not valid JSON: {ex.Message}", ex);
        }

        return FromNode(node);
    }

    public static TodoArchiveDocument Parse(byte[] utf8Json)
    {
        return Parse(DecodeUtf8(utf8Json));
    }

    public static async Task<TodoArchiveDocument> LoadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return Parse(buffer.ToArray());
    }

    private static string DecodeUtf8(byte[] bytes)
    {
        // Strip a UTF-8 BOM if present.
        var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
    }

    private static TodoArchiveDocument FromNode(JsonNode? node)
    {
        if (node is not JsonObject root)
        {
            throw new InvalidDataException("The archive must be a JSON object.");
        }

        var format = root["format"] is JsonValue fv && fv.TryGetValue<string>(out var f) ? f : null;
        if (format != FormatName)
        {
            throw new InvalidDataException($"Unexpected archive format '{format ?? "(missing)"}'. Expected '{FormatName}'.");
        }

        if (root["version"] is JsonValue vv && vv.TryGetValue<int>(out var version) && version > CurrentVersion)
        {
            throw new InvalidDataException($"Archive version {version} is newer than this tool supports ({CurrentVersion}).");
        }

        if (root["lists"] is not null and not JsonArray)
        {
            throw new InvalidDataException("The archive property 'lists' must be an array.");
        }

        return new TodoArchiveDocument(root);
    }

    /// <summary>Serializes the archive. Non-ASCII text is written as is (not \u-escaped).</summary>
    public byte[] ToUtf8Bytes(bool indented = true)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = indented,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            Root.WriteTo(writer);
        }

        return stream.ToArray();
    }

    public string ToJsonString(bool indented = true) => Encoding.UTF8.GetString(ToUtf8Bytes(indented));
}

/// <summary>One list with its tasks, as raw Graph objects.</summary>
public sealed record ArchivedList(JsonObject List, IReadOnlyList<JsonObject> Tasks);
