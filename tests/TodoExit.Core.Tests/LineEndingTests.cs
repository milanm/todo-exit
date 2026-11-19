using TodoExit.Core.Archive;
using TodoExit.Core.Export;

namespace TodoExit.Core.Tests;

// Exported JSON must be byte-identical on every OS: LF only, never CR.
public class LineEndingTests
{
    private const byte Cr = (byte)'\r';
    private const byte Lf = (byte)'\n';

    // Task text with CR LF inside: JSON escapes it, so no raw CR byte may appear.
    private static TodoArchiveDocument Doc() => TestData.Document(
        (TestData.List("Work"), [
            TestData.Task("Open", """{ "body": { "content": "line one\r\nline two", "contentType": "text" } }"""),
            TestData.Task("Done", """{ "status": "completed" }"""),
        ]));

    private static void AssertLfOnly(byte[] bytes)
    {
        Assert.DoesNotContain(Cr, bytes);
        Assert.Contains(Lf, bytes);
    }

    [Fact]
    public void Archive_bytes_use_lf_and_contain_no_cr()
    {
        AssertLfOnly(Doc().ToUtf8Bytes());
    }

    [Theory]
    [InlineData(ExportService.ArchiveFileName)]
    [InlineData(LossReport.JsonFileName)]
    public void Exported_json_file_uses_lf_and_contains_no_cr(string path)
    {
        var doc = Doc();
        var archive = Model.ArchiveParser.Parse(doc, TestData.Belgrade);
        var result = ExportService.Export(doc, archive, new ExportOptions());

        AssertLfOnly(result.Files.Single(f => f.Path == path).Content);
    }
}
