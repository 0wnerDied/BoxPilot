using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace BoxPilot.Core.Infrastructure;

internal static class MacOSAuthorization
{
    private const string SecurityFramework =
        "/System/Library/Frameworks/Security.framework/Versions/A/Security";
    private const string ExecuteRight = "system.privilege.admin";
    private const string PromptEnvironment = "prompt";
    private const string Prompt = "BoxPilot TUN";
    private const int AuthorizationSuccess = 0;
    private const int AuthorizationDenied = -60005;
    private const int AuthorizationCanceled = -60006;
    private const uint InteractionAllowed = 1 << 0;
    private const uint ExtendRights = 1 << 1;
    private const uint DestroyRights = 1 << 3;
    private const uint PreAuthorize = 1 << 4;
    private const int MaximumOutputLength = 64 * 1024;

    [SupportedOSPlatform("macos")]
    public static Task<int> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        cancellationToken.ThrowIfCancellationRequested();

        // Authorization Services is synchronous. Keep its system dialog off the UI thread;
        // cancellation is checked before entry because aborting a native authorization
        // session after it has launched the installer would orphan privileged work.
        return Task.Run(() => Run(executable, arguments));
    }

    internal static int ParseInstallerResult(string output)
    {
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith(CoreServiceInstaller.ResultPrefix, StringComparison.Ordinal))
                continue;
            var value = line.AsSpan(CoreServiceInstaller.ResultPrefix.Length);
            if (int.TryParse(
                    value,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var exitCode)
                && exitCode is >= 0 and <= byte.MaxValue)
            {
                return exitCode;
            }
        }
        return 70;
    }

    private static int Run(string executable, IReadOnlyList<string> arguments)
    {
        var authorization = IntPtr.Zero;
        var pipe = IntPtr.Zero;
        using var right = new NativeAuthorizationItem(ExecuteRight, executable);
        using var prompt = new NativeAuthorizationItem(PromptEnvironment, Prompt);
        using var nativeArguments = new NativeArguments(arguments);
        try
        {
            var status = AuthorizationCreate(IntPtr.Zero, IntPtr.Zero, 0, out authorization);
            if (status != AuthorizationSuccess)
                return MapAuthorizationStatus(status);

            var rights = new AuthorizationItemSet
            {
                Count = 1,
                Items = right.Pointer,
            };
            var environment = new AuthorizationItemSet
            {
                Count = 1,
                Items = prompt.Pointer,
            };
            status = AuthorizationCopyRights(
                authorization,
                ref rights,
                ref environment,
                InteractionAllowed | ExtendRights | PreAuthorize,
                IntPtr.Zero);
            if (status != AuthorizationSuccess)
                return MapAuthorizationStatus(status);

            status = AuthorizationExecuteWithPrivileges(
                authorization,
                right.ValuePointer,
                0,
                nativeArguments.Pointer,
                out pipe);
            if (status != AuthorizationSuccess)
                return MapAuthorizationStatus(status);

            return ParseInstallerResult(ReadOutput(pipe));
        }
        finally
        {
            if (pipe != IntPtr.Zero)
                _ = CloseFile(pipe);
            if (authorization != IntPtr.Zero)
                _ = AuthorizationFree(authorization, DestroyRights);
        }
    }

    private static int MapAuthorizationStatus(int status)
    {
        return status is AuthorizationDenied or AuthorizationCanceled ? 77 : 70;
    }

    private static string ReadOutput(IntPtr pipe)
    {
        var descriptor = FileNumber(pipe);
        if (descriptor < 0)
            return string.Empty;

        var buffer = new byte[4096];
        using var output = new MemoryStream();
        while (true)
        {
            var count = Read(descriptor, buffer, (nuint)buffer.Length);
            if (count == 0)
                break;
            if (count < 0)
            {
                if (Marshal.GetLastPInvokeError() == 4)
                    continue;
                return string.Empty;
            }
            if (output.Length + count > MaximumOutputLength)
                return string.Empty;
            output.Write(buffer, 0, checked((int)count));
        }
        return Utf8Text.Strict.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AuthorizationItem
    {
        public IntPtr Name;
        public nuint ValueLength;
        public IntPtr Value;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AuthorizationItemSet
    {
        public uint Count;
        public IntPtr Items;
    }

    private sealed class NativeAuthorizationItem : IDisposable
    {
        private IntPtr namePointer;
        private IntPtr valuePointer;

        public NativeAuthorizationItem(string name, string value)
        {
            try
            {
                namePointer = Marshal.StringToCoTaskMemUTF8(name);
                valuePointer = Marshal.StringToCoTaskMemUTF8(value);
                Pointer = Marshal.AllocHGlobal(Marshal.SizeOf<AuthorizationItem>());
                Marshal.StructureToPtr(
                    new AuthorizationItem
                    {
                        Name = namePointer,
                        ValueLength = (nuint)Utf8Text.Strict.GetByteCount(value),
                        Value = valuePointer,
                    },
                    Pointer,
                    fDeleteOld: false);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public IntPtr Pointer { get; private set; }

        public IntPtr ValuePointer => valuePointer;

        public void Dispose()
        {
            if (Pointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Pointer);
                Pointer = IntPtr.Zero;
            }
            if (valuePointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(valuePointer);
                valuePointer = IntPtr.Zero;
            }
            if (namePointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(namePointer);
                namePointer = IntPtr.Zero;
            }
        }
    }

    private sealed class NativeArguments : IDisposable
    {
        private readonly IntPtr[] values;

        public NativeArguments(IReadOnlyList<string> arguments)
        {
            values = new IntPtr[arguments.Count];
            try
            {
                Pointer = Marshal.AllocHGlobal(IntPtr.Size * (arguments.Count + 1));
                for (var index = 0; index < arguments.Count; index++)
                {
                    values[index] = Marshal.StringToCoTaskMemUTF8(arguments[index]);
                    Marshal.WriteIntPtr(Pointer, index * IntPtr.Size, values[index]);
                }
                Marshal.WriteIntPtr(Pointer, arguments.Count * IntPtr.Size, IntPtr.Zero);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public IntPtr Pointer { get; private set; }

        public void Dispose()
        {
            foreach (var value in values)
            {
                if (value != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(value);
            }
            if (Pointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Pointer);
                Pointer = IntPtr.Zero;
            }
        }
    }

    [DllImport(SecurityFramework)]
    private static extern int AuthorizationCreate(
        IntPtr rights,
        IntPtr environment,
        uint flags,
        out IntPtr authorization);

    [DllImport(SecurityFramework)]
    private static extern int AuthorizationCopyRights(
        IntPtr authorization,
        ref AuthorizationItemSet rights,
        ref AuthorizationItemSet environment,
        uint flags,
        IntPtr authorizedRights);

    [DllImport(SecurityFramework)]
    private static extern int AuthorizationExecuteWithPrivileges(
        IntPtr authorization,
        IntPtr pathToTool,
        uint options,
        IntPtr arguments,
        out IntPtr communicationsPipe);

    [DllImport(SecurityFramework)]
    private static extern int AuthorizationFree(IntPtr authorization, uint flags);

    [DllImport("libc", EntryPoint = "fileno")]
    private static extern int FileNumber(IntPtr stream);

    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern nint Read(int fileDescriptor, byte[] buffer, nuint count);

    [DllImport("libc", EntryPoint = "fclose")]
    private static extern int CloseFile(IntPtr stream);
}
