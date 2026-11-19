using System.Text.Json.Nodes;
using TodoExit.Core.Archive;
using TodoExit.Core.Model;

namespace TodoExit.Core.Tests;

public class ArchiveTests
{
    private const string Sample = """
        {
          "format": "todo-exit-archive",
          "version": 1,
          "exportedAt": "2026-11-19T13:30:00Z",
          "someFutureField": { "nested": [1, 2.50, "x"] },
          "lists": [
            {
              "list": { "id": "L1", "displayName": "Tasks", "@odata.etag": "W/\"abc\"", "unknownListProp": true },
              "tasks": [
                {
                  "id": "T1",
                  "title": "Kupi hleb ćevapi",
                  "status": "notStarted",
                  "newGraphProperty": { "deep": { "value": 12345678901234567890 } },
                  "checklistItems": [ { "displayName": "s", "isChecked": false, "extra": "keep" } ],
                  "attachments": [ { "id": "a1", "name": "f.png", "size": 10, "contentType": "image/png", "lastModifiedDateTime": "2026-11-01T00:00:00Z" } ]
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void Round_trip_preserves_unknown_properties()
    {
        var doc = TodoArchiveDocument.Parse(Sample);
        var written = doc.ToJsonString();
        var reread = TodoArchiveDocument.Parse(written);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Sample), reread.Root));
        Assert.Contains("12345678901234567890", written);
        Assert.Contains("2.50", written);
        Assert.Contains("Kupi hleb ćevapi", written);
    }

    [Fact]
    public void Parsing_the_model_does_not_modify_the_archive()
    {
        var doc = TodoArchiveDocument.Parse(Sample);
        var before = doc.ToJsonString();
        _ = ArchiveParser.Parse(doc, TestData.Belgrade);
        Assert.Equal(before, doc.ToJsonString());
    }

    [Fact]
    public void Bom_is_accepted()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(System.Text.Encoding.UTF8.GetBytes(Sample)).ToArray();
        var doc = TodoArchiveDocument.Parse(bytes);
        Assert.Single(doc.Lists);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{ "format": "other", "version": 1, "lists": [] }""")]
    [InlineData("""{ "format": "todo-exit-archive", "version": 99, "lists": [] }""")]
    [InlineData("not json")]
    public void Rejects_wrong_envelope(string json)
    {
        Assert.Throws<InvalidDataException>(() => TodoArchiveDocument.Parse(json));
    }

    [Fact]
    public void Create_writes_the_envelope()
    {
        var doc = TestData.Document((TestData.List("A"), [TestData.Task("t")]));
        var root = JsonNode.Parse(doc.ToJsonString())!.AsObject();

        Assert.Equal("todo-exit-archive", (string)root["format"]!);
        Assert.Equal(1, (int)root["version"]!);
        Assert.Equal("2026-11-19T13:30:00Z", (string)root["exportedAt"]!);
        Assert.Equal("A", (string)root["lists"]![0]!["list"]!["displayName"]!);
        Assert.Equal("t", (string)root["lists"]![0]!["tasks"]![0]!["title"]!);
    }
}
