using System.Text;

namespace TodoExit.Core.Export;

/// <summary>RFC 4180 CSV writing. Rows end with CRLF.</summary>
public static class Csv
{
    public static string Escape(string? field)
    {
        if (string.IsNullOrEmpty(field))
        {
            return string.Empty;
        }

        if (field.AsSpan().IndexOfAny(",\"\r\n") < 0)
        {
            return field;
        }

        return "\"" + field.Replace("\"", "\"\"") + "\"";
    }

    public static void AppendRow(StringBuilder sb, IEnumerable<string?> fields)
    {
        var first = true;
        foreach (var field in fields)
        {
            if (!first)
            {
                sb.Append(',');
            }

            sb.Append(Escape(field));
            first = false;
        }

        sb.Append("\r\n");
    }
}
