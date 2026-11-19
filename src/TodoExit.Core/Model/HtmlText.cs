using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace TodoExit.Core.Model;

/// <summary>Converts the HTML bodies To Do produces into plain text. Not a general HTML parser.</summary>
public static partial class HtmlText
{
    [GeneratedRegex(@"<(script|style|head)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex InvisibleBlocks();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comments();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreaks();

    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItemOpen();

    [GeneratedRegex(@"</(p|ul|ol|h[1-6])\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex ParagraphClose();

    [GeneratedRegex(@"</?(p|div|h[1-6]|ul|ol|li|tr|table|thead|tbody|blockquote|pre|section|article|header|footer|hr)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTags();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AnyTag();

    // A run of markers and whitespace between two pieces of text.
    [GeneratedRegex("[\u0001\u0002\u0003\\s]*[\u0001\u0002\u0003][\u0001\u0002\u0003\\s]*")]
    private static partial Regex MarkerRuns();

    [GeneratedRegex(@"[ \t\f\v]+")]
    private static partial Regex HorizontalSpace();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLineRuns();

    private const char BlockMark = '\u0001';
    private const char ParagraphMark = '\u0002';
    private const char BreakMark = '\u0003';
    private const char BulletMark = '\u0004';

    public static string? ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        // Source line breaks carry no meaning in HTML.
        var s = html.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        s = InvisibleBlocks().Replace(s, string.Empty);
        s = Comments().Replace(s, string.Empty);

        // Replace structure with markers first, then decide how many newlines each run of markers is worth.
        // This keeps "<div>a</div><div>b</div>" as two lines and "<p>a</p><p>b</p>" as two paragraphs.
        s = LineBreaks().Replace(s, BreakMark.ToString());
        s = ListItemOpen().Replace(s, BlockMark.ToString() + BulletMark);
        s = ParagraphClose().Replace(s, ParagraphMark.ToString());
        s = BlockTags().Replace(s, BlockMark.ToString());
        s = AnyTag().Replace(s, string.Empty);
        s = MarkerRuns().Replace(s, m => new string('\n', NewlinesFor(m.Value)));
        s = s.Replace(BulletMark.ToString(), "- ");
        s = WebUtility.HtmlDecode(s).Replace('\u00A0', ' ');

        return Normalize(s);
    }

    private static int NewlinesFor(string run)
    {
        var breaks = run.Count(c => c == BreakMark);
        var hasParagraph = run.Contains(ParagraphMark);
        var hasBlock = hasParagraph || run.Contains(BlockMark);
        if (!hasBlock)
        {
            return breaks;
        }

        return Math.Min(2, (hasParagraph ? 2 : 1) + breaks);
    }

    /// <summary>Trims each line, collapses runs of blank lines to one, trims the whole text. Empty becomes null.</summary>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var s = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var sb = new StringBuilder(s.Length);
        foreach (var line in s.Split('\n'))
        {
            sb.Append(HorizontalSpace().Replace(line, " ").Trim()).Append('\n');
        }

        s = BlankLineRuns().Replace(sb.ToString(), "\n\n").Trim();
        return s.Length == 0 ? null : s;
    }
}
