using System.Text;

namespace TodoExit.Core.Export;

/// <summary>An in-memory output file. Path is relative and uses forward slashes.</summary>
public sealed class ExportFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public ExportFile(string path, byte[] content)
    {
        Path = path;
        Content = content;
    }

    public string Path { get; }

    public byte[] Content { get; }

    public static ExportFile FromText(string path, string text, bool withBom = false)
    {
        var body = Utf8NoBom.GetBytes(text);
        if (!withBom)
        {
            return new ExportFile(path, body);
        }

        var bytes = new byte[body.Length + 3];
        bytes[0] = 0xEF;
        bytes[1] = 0xBB;
        bytes[2] = 0xBF;
        body.CopyTo(bytes, 3);
        return new ExportFile(path, bytes);
    }

    /// <summary>Content decoded as UTF-8, without a BOM.</summary>
    public string GetText()
    {
        var offset = Content.Length >= 3 && Content[0] == 0xEF && Content[1] == 0xBB && Content[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(Content, offset, Content.Length - offset);
    }

    public override string ToString() => Path;
}
