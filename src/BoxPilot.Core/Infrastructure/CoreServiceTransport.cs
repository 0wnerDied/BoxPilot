using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace BoxPilot.Core.Infrastructure;

internal interface ICoreServiceListener : IAsyncDisposable
{
    ValueTask<Stream> AcceptAsync(CancellationToken cancellationToken);
}

internal static class CoreServiceTransport
{
    public static ICoreServiceListener CreateListener(CoreServiceConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (OperatingSystem.IsWindows())
            return new WindowsCoreServiceListener(configuration.Endpoint, configuration.OwnerSid);
        if (OperatingSystem.IsMacOS())
            return new UnixCoreServiceListener(configuration.Endpoint, configuration.OwnerUid);
        throw new PlatformNotSupportedException();
    }

    public static async Task<Stream> ConnectAsync(
        CoreServiceLayout layout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.Platform == CoreServicePlatform.Windows)
        {
            var pipe = new NamedPipeClientStream(
                ".",
                layout.Endpoint,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
                return pipe;
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(
                    new UnixDomainSocketEndPoint(layout.Endpoint),
                    cancellationToken)
                .ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsCoreServiceListener : ICoreServiceListener
{
    private readonly string pipeName;
    private readonly PipeSecurity security;

    public WindowsCoreServiceListener(string pipeName, string? ownerSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerSid);
        this.pipeName = pipeName;
        security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddAccess(new SecurityIdentifier(ownerSid));
        AddAccess(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        AddAccess(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
    }

    public async ValueTask<Stream> AcceptAsync(CancellationToken cancellationToken)
    {
        var pipe = NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            64 * 1024,
            64 * 1024,
            security,
            HandleInheritability.None,
            PipeAccessRights.ReadWrite);
        try
        {
            await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    private void AddAccess(SecurityIdentifier identity)
    {
        security.AddAccessRule(new PipeAccessRule(
            identity,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
    }
}

[SupportedOSPlatform("macos")]
internal sealed class UnixCoreServiceListener : ICoreServiceListener
{
    private readonly string path;
    private readonly Socket listener;

    public UnixCoreServiceListener(string path, uint ownerUid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(this.path)!;
        Directory.CreateDirectory(directory);
        File.SetUnixFileMode(
            directory,
            UnixFileMode.UserRead
            | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead
            | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead
            | UnixFileMode.OtherExecute);
        File.Delete(this.path);

        listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(this.path));
            if (Chown(this.path, ownerUid, uint.MaxValue) != 0)
                throw new IOException("Could not assign the core service socket owner.");
            if (Chmod(this.path, Convert.ToUInt32("600", 8)) != 0)
                throw new IOException("Could not protect the core service socket.");
            listener.Listen(1);
        }
        catch
        {
            listener.Dispose();
            File.Delete(this.path);
            throw;
        }
    }

    public async ValueTask<Stream> AcceptAsync(CancellationToken cancellationToken)
    {
        var socket = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
        return new NetworkStream(socket, ownsSocket: true);
    }

    public ValueTask DisposeAsync()
    {
        listener.Dispose();
        File.Delete(path);
        return ValueTask.CompletedTask;
    }

    [DllImport("libc", EntryPoint = "chown", SetLastError = true)]
    private static extern int Chown(string path, uint owner, uint group);

    [DllImport("libc", EntryPoint = "chmod", SetLastError = true)]
    private static extern int Chmod(string path, uint mode);
}
