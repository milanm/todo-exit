using System.Text;
using System.Text.Json.Nodes;
using TodoExit.Cli;
using TodoExit.Core.Export;
using TodoExit.Core.Model;
using TodoExit.Core.Todoist;

namespace TodoExit.Core.Tests;

/// <summary>Exported files and planned Todoist projects never share a name, ignoring case and Unicode normalization.</summary>
public class FileNameCollisionTests
{
    private static JsonObject[] Tasks(string prefix, int count) =>
        Enumerable.Range(0, count).Select(i => TestData.Task($"{prefix} {i}", $$"""{ "id": "{{prefix}}-{{i}}" }""")).ToArray();

    private static readonly string CafeComposed = "Café";
    private static readonly string CafeDecomposed = "Café";

    [Fact]
    public async Task List_named_like_a_part_of_a_split_list_does_not_overwrite_it_through_the_cli()
    {
        // The reviewer's scenario: "Work" with 301 tasks is split into two files; a list "Work - part 1" with one task
        // used to overwrite the first part.
        var dir = Path.Combine(AppContext.BaseDirectory, "collision-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var archivePath = Path.Combine(dir, "archive.json");
        await File.WriteAllBytesAsync(archivePath, TestData.Document(
            (TestData.List("Work"), Tasks("Work", 301)),
            (TestData.List("Work - part 1", id: "list-other"), Tasks("Other", 1))).ToUtf8Bytes());
        var outDir = Path.Combine(dir, "out");
        var stdout = new StringWriter();

        var code = await Program.RunAsync(["export", "--from-archive", archivePath, "--out", outDir, "--formats", "todoist,markdown"],
            stdout, new StringWriter(), null, CancellationToken.None);

        Assert.Equal(Program.ExitOk, code);
        var csv = Directory.GetFiles(Path.Combine(outDir, "todoist")).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["Work - part 1 (2).csv", "Work - part 1.csv", "Work - part 2.csv"], csv);
        var rows = Directory.GetFiles(Path.Combine(outDir, "todoist"))
            .Sum(f => File.ReadAllLines(f).Count(l => l.StartsWith("task,", StringComparison.Ordinal)));
        Assert.Equal(302, rows);
        Assert.Contains("Other 0", await File.ReadAllTextAsync(Path.Combine(outDir, "todoist", "Work - part 1 (2).csv")));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(outDir, "markdown")).Length);
    }

    [Fact]
    public void Part_names_collide_ignoring_case()
    {
        var archive = TestData.Archive(
            (TestData.List("Work"), Tasks("Work", 301)),
            (TestData.List("work - PART 2", id: "list-b"), Tasks("B", 1)));

        var paths = TodoistCsvExporter.Export(archive, includeCompleted: false).Select(f => f.Path).ToList();

        Assert.Equal(["todoist/Work - part 1.csv", "todoist/Work - part 2.csv", "todoist/work - PART 2 (2).csv"], paths);
    }

    [Fact]
    public void Case_only_duplicate_list_names_get_distinct_files_in_every_exporter()
    {
        var archive = TestData.Archive(
            (TestData.List("Work"), Tasks("A", 2)),
            (TestData.List("work", id: "list-b"), Tasks("B", 2)));

        Assert.Equal(["todoist/Work.csv", "todoist/work (2).csv"], TodoistCsvExporter.Export(archive, false).Select(f => f.Path));
        Assert.Equal(["markdown/Work.md", "markdown/work (2).md"], MarkdownExporter.Export(archive).Select(f => f.Path));
    }

    [Fact]
    public void Names_that_differ_only_in_unicode_normalization_get_distinct_files()
    {
        // macOS treats the composed and the decomposed form of "Cafe" with an accent as one file name.
        var archive = TestData.Archive(
            (TestData.List(CafeComposed), Tasks("A", 1)),
            (TestData.List(CafeDecomposed, id: "list-b"), Tasks("B", 1)));

        var markdown = MarkdownExporter.Export(archive).Select(f => f.Path).ToList();
        Assert.Equal([$"markdown/{CafeComposed}.md", $"markdown/{CafeDecomposed} (2).md"], markdown);
        var csv = TodoistCsvExporter.Export(archive, false).Select(f => f.Path).ToList();
        Assert.Equal([$"todoist/{CafeComposed}.csv", $"todoist/{CafeDecomposed} (2).csv"], csv);

        var all = ExportService.Export(TestData.Document(
            (TestData.List(CafeComposed), Tasks("A", 1)),
            (TestData.List(CafeDecomposed, id: "list-b"), Tasks("B", 1))), archive, new ExportOptions()).Files;
        Assert.Empty(FileNames.SameFileGroups(all.Select(f => f.Path)));
    }

    [Fact]
    public void Export_fails_loudly_when_two_files_would_be_the_same_file()
    {
        var ex = Assert.Throws<ExportPathConflictException>(() => ExportService.EnsureDistinctPaths(
        [
            ExportFile.FromText("todoist/Work.csv", "a"),
            ExportFile.FromText("todoist/Other.csv", "b"),
            ExportFile.FromText("todoist/WORK.csv", "c"),
        ]));
        Assert.Contains("todoist/Work.csv and todoist/WORK.csv", ex.Message);

        Assert.Throws<ExportPathConflictException>(() => ExportService.EnsureDistinctPaths(
            [ExportFile.FromText($"markdown/{CafeComposed}.md", "a"), ExportFile.FromText($"markdown/{CafeDecomposed}.md", "b")]));

        ExportService.EnsureDistinctPaths([ExportFile.FromText("a.csv", "a"), ExportFile.FromText("markdown/a.csv", "b")]);
    }

    [Fact]
    public void Unique_stems_reserve_names_of_lists_without_files_like_before()
    {
        // A list with nothing to write still keeps its name, so the numbering of later lists does not change.
        var stems = FileNames.UniqueStems(["Work", "work", "Work - part 1"], [0, 2, 1],
            (stem, part, count) => count == 1 ? stem : $"{stem} - part {part}");
        Assert.Equal(["Work", "work (2)", "Work - part 1"], stems);
    }

    [Fact]
    public void Transfer_plan_never_gives_two_projects_the_same_name()
    {
        var archive = TestData.Archive(
            (TestData.List("Work"), Tasks("Work", 7)),
            (TestData.List("Work - part 1", id: "list-b"), Tasks("B", 1)),
            (TestData.List("WORK - PART 2", id: "list-c"), Tasks("C", 1)));
        var settings = new TodoistPlanSettings { TimeZoneId = "UTC", MaxTasksPerProject = 5, PlannedAt = TodoistTransferTests.Now };

        var plan = TodoistPlanner.Build(archive, settings, account: null);

        Assert.Equal(["Work - part 1", "Work - part 2", "Work - part 1 (2)", "WORK - PART 2 (2)"], plan.Projects.Select(p => p.Name));
        Assert.Empty(FileNames.SameFileGroups(plan.Projects.Select(p => p.Name)));
    }

    [Fact]
    public async Task Transfer_plan_with_a_split_list_and_a_part_named_list_creates_distinct_projects()
    {
        var fake = new FakeTodoist { MaxTasks = 5 };
        using var http = new HttpClient(fake, disposeHandler: false);
        var client = new TodoistClient(http, new TodoistToken(FakeTodoist.Token));
        var account = await client.ReadAccountAsync(CancellationToken.None);
        var archive = TestData.Archive(
            (TestData.List("Work"), Tasks("Work", 7)),
            (TestData.List("work - part 2", id: "list-b"), Tasks("B", 1)));
        var plan = TodoistPlanner.Build(archive, new TodoistPlanSettings
        {
            TimeZoneId = "UTC",
            MaxTasksPerProject = 5,
            PlannedAt = TodoistTransferTests.Now,
        }, account);
        Assert.True(plan.CanStart, string.Join(" ", plan.Problems));

        var state = TodoistTransfer.NewState(plan, "fingerprint", TodoistTransferTests.Now);
        var result = await TodoistTransfer.RunAsync(plan, state, client, (_, _) => Task.CompletedTask, null, CancellationToken.None);

        Assert.Equal(TransferStatus.Completed, result.Status);
        Assert.Equal(["Inbox", "Work - part 1", "Work - part 2", "work - part 2 (2)"], fake.Projects.Select(p => p.Name));
        Assert.Single(fake.ItemsIn("work - part 2 (2)"));
        Assert.Equal(8, fake.Items.Count);
    }

    [Fact]
    public void Planned_name_is_still_compared_with_existing_projects_ignoring_case()
    {
        var archive = TestData.Archive((TestData.List("Work"), Tasks("Work", 1)));
        var account = new TodoistAccount("a@example.com", false, "Beginner", "UTC", 5, 300, 500, false,
            [new TodoistProject("p1", "WORK", false, false)]);

        var plan = TodoistPlanner.Build(archive, new TodoistPlanSettings { TimeZoneId = "UTC", PlannedAt = TodoistTransferTests.Now }, account);

        Assert.False(plan.CanStart);
        Assert.Equal(["Work"], plan.NameClashes);
    }
}
