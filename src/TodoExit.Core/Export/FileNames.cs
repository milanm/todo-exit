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
    /// Compares names the way Windows and macOS file systems (and zip extraction there) do: two names that differ
    /// only in letter case or in Unicode normalization form are the same file.
    /// </summary>
    public static StringComparer PathComparer { get; } = new SameFileComparer();

    /// <summary>
    /// Sanitizes each name and de-duplicates by appending " (2)", " (3)", ... Names that differ only in case or
    /// Unicode normalization count as duplicates. The result has the same length and order as the input.
    /// </summary>
    public static IReadOnlyList<string> SanitizeUnique(IEnumerable<string?> names)
    {
        var sanitized = names.Select(Sanitize).ToList();
        return UniqueStems(sanitized, sanitized.Select(_ => 1).ToList(), (stem, _, _) => stem);
    }

    /// <summary>
    /// Picks a stem for each entry so that every final name is unique (see <see cref="PathComparer"/>).
    /// Entry i produces <paramref name="counts"/>[i] names, <paramref name="nameOf"/>(stem, part, count) for part 1..count.
    /// A stem that is taken, or whose names are taken, gets " (2)", " (3)", ... Every stem is reserved, even for an entry
    /// with no names, so the numbering is the same as when only the stems are compared. Without collisions every stem
    /// is the base name unchanged.
    /// </summary>
    public static IReadOnlyList<string> UniqueStems(
        IReadOnlyList<string> baseNames, IReadOnlyList<int> counts, Func<string, int, int, string> nameOf)
    {
        ArgumentNullException.ThrowIfNull(baseNames);
        ArgumentNullException.ThrowIfNull(counts);
        ArgumentNullException.ThrowIfNull(nameOf);
        if (baseNames.Count != counts.Count)
        {
            throw new ArgumentException("Each base name needs a count.", nameof(counts));
        }

        var stems = new HashSet<string>(PathComparer);
        var taken = new HashSet<string>(PathComparer);
        var result = new List<string>(baseNames.Count);
        for (var i = 0; i < baseNames.Count; i++)
        {
            var baseName = baseNames[i];
            var candidate = baseName;
            for (var n = 2; ; n++)
            {
                if (!stems.Contains(candidate))
                {
                    var count = counts[i];
                    var names = Enumerable.Range(1, count).Select(p => nameOf(candidate, p, count)).ToList();
                    var free = new HashSet<string>(names, PathComparer).Count == names.Count && !names.Any(taken.Contains);
                    if (free)
                    {
                        stems.Add(candidate);
                        taken.UnionWith(names);
                        break;
                    }
                }

                candidate = $"{baseName} ({n})";
            }

            result.Add(candidate);
        }

        return result;
    }

    /// <summary>Names that are the same file for <see cref="PathComparer"/>, in groups. Empty when there are none.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> SameFileGroups(IEnumerable<string> names) =>
        names.GroupBy(n => n, PathComparer).Where(g => g.Count() > 1).Select(g => (IReadOnlyList<string>)g.ToList()).ToList();

    private sealed class SameFileComparer : StringComparer
    {
        public override int Compare(string? x, string? y) =>
            StringComparer.OrdinalIgnoreCase.Compare(Normalize(x), Normalize(y));

        public override bool Equals(string? x, string? y) =>
            StringComparer.OrdinalIgnoreCase.Equals(Normalize(x), Normalize(y));

        public override int GetHashCode(string obj) =>
            StringComparer.OrdinalIgnoreCase.GetHashCode(Normalize(obj)!);

        private static string? Normalize(string? value)
        {
            if (value is null)
            {
                return null;
            }

            try
            {
                return value.Normalize(NormalizationForm.FormC);
            }
            catch (ArgumentException)
            {
                return value; // not valid UTF-16: compare as it is
            }
        }
    }
}
