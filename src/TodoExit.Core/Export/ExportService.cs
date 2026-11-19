using TodoExit.Core.Archive;
using TodoExit.Core.Model;

namespace TodoExit.Core.Export;

public sealed record ExportOptions(ExportFormats Formats = ExportFormats.All, bool IncludeCompleted = false);

public sealed record ExportResult(IReadOnlyList<ExportFile> Files, LossReport Report);

/// <summary>Runs the requested exporters and builds the loss report. Shared by the CLI and the web app.</summary>
public static class ExportService
{
    public const string ArchiveFileName = "archive.json";

    public static ExportResult Export(TodoArchiveDocument document, TodoArchive archive, ExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(options);

        var files = new List<ExportFile>();
        var losses = new List<Loss>();

        if (options.Formats.HasFlag(ExportFormats.Json))
        {
            files.Add(new ExportFile(ArchiveFileName, document.ToUtf8Bytes(indented: true)));
        }

        if (options.Formats.HasFlag(ExportFormats.Markdown))
        {
            files.AddRange(MarkdownExporter.Export(archive, losses));
        }

        if (options.Formats.HasFlag(ExportFormats.Todoist))
        {
            files.AddRange(TodoistCsvExporter.Export(archive, options.IncludeCompleted, losses));
        }

        if (options.Formats.HasFlag(ExportFormats.Csv))
        {
            files.Add(GenericCsvExporter.Export(archive, losses));
        }

        var ordered = LossCatalog.Aggregate(losses)
            .Select((loss, index) => (loss, index))
            .OrderBy(x => FormatRank(x.loss.Format))
            .ThenBy(x => x.index)
            .Select(x => x.loss)
            .ToList();

        var report = new LossReport(Inventory.From(archive), ordered, options.Formats) { ReadNotes = archive.ReadNotes };
        files.AddRange(report.ToFiles());
        EnsureDistinctPaths(files);
        return new ExportResult(files, report);
    }

    /// <summary>
    /// The last check before anything is written or zipped: two files whose paths differ only in case or Unicode
    /// normalization would overwrite each other on Windows and macOS, so the export fails instead.
    /// </summary>
    /// <exception cref="ExportPathConflictException">Two files would be the same file.</exception>
    public static void EnsureDistinctPaths(IEnumerable<ExportFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var groups = FileNames.SameFileGroups(files.Select(f => f.Path));
        if (groups.Count > 0)
        {
            throw new ExportPathConflictException(
                "The export stopped before writing anything: these files would overwrite each other: " +
                string.Join("; ", groups.Select(g => string.Join(" and ", g))) + ". Please report this as a bug.");
        }
    }

    private static int FormatRank(string format) => format switch
    {
        ExportFormatNames.Todoist => 0,
        ExportFormatNames.Markdown => 1,
        ExportFormatNames.Csv => 2,
        _ => 3,
    };
}

/// <summary>Two files of one export would have the same path on Windows or macOS. Nothing was written.</summary>
public sealed class ExportPathConflictException(string message) : InvalidOperationException(message);
