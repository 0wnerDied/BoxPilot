using System.Text;

namespace BoxPilot.Core.Infrastructure;

internal static class Utf8Text
{
    public static readonly UTF8Encoding Strict = new(false, true);

    public static async Task<string> ReadAllTextAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ReadToEndAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<string> ReadToEndAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(
            stream,
            Strict,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: true);
        var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }
}
