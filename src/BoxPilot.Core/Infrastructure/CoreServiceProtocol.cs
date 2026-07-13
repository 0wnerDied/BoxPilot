using System.Buffers.Binary;
using System.Text.Json;
using BoxPilot.Core.Models;

namespace BoxPilot.Core.Infrastructure;

internal sealed record CoreServiceMessage
{
    public required string Type { get; init; }

    public int ProtocolVersion { get; init; }

    public string? Token { get; init; }

    public long RequestId { get; init; }

    public string? Command { get; init; }

    public string? Configuration { get; init; }

    public string? WorkingDirectory { get; init; }

    public bool Success { get; init; }

    public string? Error { get; init; }

    public CoreState State { get; init; }

    public int? ProcessId { get; init; }

    public DateTimeOffset Timestamp { get; init; }

    public CoreLogStream Stream { get; init; }

    public string? Message { get; init; }

    public string? ApplicationFingerprint { get; init; }

    public string? CoreFingerprint { get; init; }
}

internal sealed record CoreServiceInstallRequest
{
    public int ProtocolVersion { get; init; }

    public required string Identity { get; init; }

    public required string DataRoot { get; init; }

    public required string SourceApplicationPath { get; init; }

    public required string SourceCorePath { get; init; }

    public required string ApplicationFingerprint { get; init; }

    public required string CoreFingerprint { get; init; }

    public required string TokenHash { get; init; }

    public string? OwnerSid { get; init; }

    public uint OwnerUid { get; init; }
}

internal sealed record CoreServiceConfiguration
{
    public int ProtocolVersion { get; init; }

    public required string Identity { get; init; }

    public required string DataRoot { get; init; }

    public required string TokenHash { get; init; }

    public required string ApplicationFingerprint { get; init; }

    public required string CoreFingerprint { get; init; }

    public required string ServiceName { get; init; }

    public required string Endpoint { get; init; }

    public string? OwnerSid { get; init; }

    public uint OwnerUid { get; init; }
}

internal sealed record CoreServiceUninstallRequest
{
    public int ProtocolVersion { get; init; }

    public required string Identity { get; init; }

    public required string DataRoot { get; init; }
}

internal static class CoreServiceProtocol
{
    public const int Version = 2;

    private const int MaximumMessageBytes = 16 * 1024 * 1024;

    public static async Task WriteAsync(
        Stream stream,
        CoreServiceMessage message,
        SemaphoreSlim writeGate,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            message,
            JsonDefaults.Context.CoreServiceMessage);
        if (payload.Length > MaximumMessageBytes)
            throw new InvalidDataException("The core service message is too large.");

        var prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(prefix, payload.Length);
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }
    }

    public static async Task<CoreServiceMessage?> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var prefix = new byte[sizeof(int)];
        if (!await ReadExactlyAsync(stream, prefix, cancellationToken).ConfigureAwait(false))
            return null;

        var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length is <= 0 or > MaximumMessageBytes)
            throw new InvalidDataException("The core service message length is invalid.");

        var payload = new byte[length];
        if (!await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false))
            throw new EndOfStreamException("The core service message ended early.");
        return JsonSerializer.Deserialize(payload, JsonDefaults.Context.CoreServiceMessage)
               ?? throw new InvalidDataException("The core service message is invalid.");
    }

    private static async Task<bool> ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                if (total == 0)
                    return false;
                throw new EndOfStreamException("The core service message ended early.");
            }
            total += read;
        }

        return true;
    }
}
