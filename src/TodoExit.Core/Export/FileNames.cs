using System.Text;

namespace TodoExit.Core.Export;

/// <summary>Safe file names for list names.</summary>
public static class FileNames
{
    private const string Invalid = "<>:\"/\\|?*";

    /// <summary>
    /// Replaces characters that are invalid on Windows and control characters with '_',
    /// trims trailing dots and spaces, and falls back to "list" when nothing is left.
    /// </summary>
    public static string Sanitize(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "list";
        }

        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(char.IsControl(c) || Invalid.Contains(c) ? '_' : c);
        }

        var result = sb.ToString().TrimEnd('.', ' ');
        return result.Length == 0 ? "list" : result;
    }

    /// <summary>
    /// Sanitizes each name and de-duplicates case-insensitively by appending " (2)", " (3)", ...
    /// The result has the same length and order as the input.
    /// </summary>
    public static IReadOnlyList<string> SanitizeUnique(IEnumerable<string?> names)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var name in names)
        {
            var baseName = Sanitize(name);
            var candidate = baseName;
            for (var i = 2; !used.Add(candidate); i++)
            {
                candidate = $"{baseName} ({i})";
            }

            result.Add(candidate);
        }

        return result;
    }
}
