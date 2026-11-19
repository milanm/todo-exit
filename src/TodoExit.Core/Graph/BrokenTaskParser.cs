using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TodoExit.Core.Graph;

/// <summary>
/// Recovers what it can from a broken single-task page.
/// </summary>
/// <remarks>
/// Observed on a live account: for a corrupt task Graph answers 200, streams every scalar property, starts the
/// linkedResources array, fails, appends an error object and stops. The body is invalid JSON, but almost the
/// whole task is in it. This parser cuts off the appended error object, walks the rest with a forward-only
/// reader that tolerates the incomplete end, and keeps every property of the first "value" element whose
/// value was read completely, as raw JSON.
/// </remarks>
internal static class BrokenTaskParser
{
    private static readonly byte[] ErrorStart = "{\"error\":"u8.ToArray();

    internal sealed record Result(JsonObject? Task, string? FailedProperty, string? ErrorCode);

    public static Result Parse(string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var (cut, errorCode) = CutAppendedError(bytes);
        var (task, failed) = ReadFirstValue(bytes.AsSpan(0, cut));
        return new Result(task, failed, errorCode);
    }

    /// <summary>
    /// Finds the last {"error":{...}} that runs to the end of the body (followed only by closing braces,
    /// brackets or whitespace). Returns the length of the body before it, and the error code.
    /// </summary>
    internal static (int Length, string? Code) CutAppendedError(byte[] bytes)
    {
        var span = bytes.AsSpan();
        var searchEnd = span.Length;
        while (searchEnd > 0)
        {
            var index = span[..searchEnd].LastIndexOf(ErrorStart);
            if (index < 0)
            {
                break;
            }

            if (TryReadErrorObject(span[index..], out var consumed, out var code)
                && OnlyClosers(span[(index + consumed)..]))
            {
                return (index, code);
            }

            searchEnd = index;
        }

        return (bytes.Length, null);
    }

    private static bool TryReadErrorObject(ReadOnlySpan<byte> data, out int consumed, out string? code)
    {
        consumed = 0;
        code = null;
        try
        {
            var reader = new Utf8JsonReader(data, isFinalBlock: false, state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject || !reader.TrySkip())
            {
                return false;
            }

            consumed = (int)reader.BytesConsumed;
            using var doc = JsonDocument.Parse(data[..consumed].ToArray());
            if (doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out var c)
                && c.ValueKind == JsonValueKind.String)
            {
                code = c.GetString();
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool OnlyClosers(ReadOnlySpan<byte> rest)
    {
        foreach (var b in rest)
        {
            if (b is not ((byte)'}' or (byte)']' or (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reads the first element of the top-level "value" array, keeping completely read properties.
    /// Returns the name of the property whose value was still open when the data ended.
    /// </summary>
    internal static (JsonObject? Task, string? FailedProperty) ReadFirstValue(ReadOnlySpan<byte> data)
    {
        var reader = new Utf8JsonReader(data, isFinalBlock: false, state: default);
        var properties = new List<(string Name, JsonNode? Value)>();
        string? failed = null;
        var found = false;

        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return (null, null);
            }

            // Find "value": [ { at the top level, skipping everything else.
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    return (null, null);
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                var isValue = reader.ValueTextEquals("value"u8);
                if (!reader.Read())
                {
                    return (null, null);
                }

                if (!isValue)
                {
                    if (!reader.TrySkip())
                    {
                        return (null, null);
                    }

                    continue;
                }

                if (reader.TokenType != JsonTokenType.StartArray || !reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                {
                    return (null, null);
                }

                found = true;
                break;
            }

            if (!found)
            {
                return (null, null);
            }

            // Inside the task object.
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    break;
                }

                var name = reader.GetString()!;
                if (!reader.Read())
                {
                    failed = name;
                    break;
                }

                var start = (int)reader.TokenStartIndex;
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    if (!reader.TrySkip())
                    {
                        failed = name;
                        break;
                    }
                }

                var end = (int)reader.BytesConsumed;
                properties.Add((name, JsonNode.Parse(data[start..end].ToArray())));
            }
        }
        catch (JsonException)
        {
            // Invalid bytes before the end: keep what was read so far.
        }

        var task = new JsonObject();
        foreach (var (name, value) in properties)
        {
            if (failed is not null && name.StartsWith(failed + "@", StringComparison.Ordinal))
            {
                continue; // annotation of the property Graph failed on
            }

            task[name] = value;
        }

        return (task, failed);
    }
}
