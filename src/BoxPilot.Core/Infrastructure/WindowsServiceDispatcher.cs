using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace BoxPilot.Core.Infrastructure;

[SupportedOSPlatform("windows")]
internal static class WindowsServiceDispatcher
{
    // Keep SCM integration local instead of pulling a second hosting stack into the single-file build.
    private const int ServiceWin32OwnProcess = 0x00000010;
    private const int ServiceStartPending = 0x00000002;
    private const int ServiceStopPending = 0x00000003;
    private const int ServiceRunning = 0x00000004;
    private const int ServiceStopped = 0x00000001;
    private const int ServiceAcceptStop = 0x00000001;
    private const int ServiceAcceptShutdown = 0x00000004;
    private const int ServiceControlStop = 0x00000001;
    private const int ServiceControlShutdown = 0x00000005;

    private static readonly ServiceMainCallback ServiceMainDelegate = ServiceMain;
    private static readonly HandlerCallback HandlerDelegate = Handler;
    private static readonly object Sync = new();
    private static Func<CancellationToken, Task<int>>? runService;
    private static CancellationTokenSource? cancellation;
    private static string? currentServiceName;
    private static IntPtr statusHandle;
    private static int exitCode;

    public static int Run(
        string serviceName,
        Func<CancellationToken, Task<int>> service)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentNullException.ThrowIfNull(service);
        lock (Sync)
        {
            runService = service;
            currentServiceName = serviceName;
            exitCode = 1;
            var table = new[]
            {
                new ServiceTableEntry { Name = serviceName, Callback = ServiceMainDelegate },
                new ServiceTableEntry(),
            };
            if (!StartServiceCtrlDispatcher(table))
                return Marshal.GetLastWin32Error();
            return exitCode;
        }
    }

    private static void ServiceMain(int argumentCount, IntPtr arguments)
    {
        var service = runService;
        if (service is null)
            return;

        statusHandle = RegisterServiceCtrlHandlerEx(currentServiceName, HandlerDelegate, IntPtr.Zero);
        if (statusHandle == IntPtr.Zero)
            return;

        cancellation = new CancellationTokenSource();
        SetStatus(ServiceStartPending, 0, 10_000);
        SetStatus(ServiceRunning, ServiceAcceptStop | ServiceAcceptShutdown, 0);
        try
        {
            exitCode = service(cancellation.Token).GetAwaiter().GetResult();
        }
        catch
        {
            exitCode = 1;
        }
        finally
        {
            cancellation.Dispose();
            cancellation = null;
            SetStatus(ServiceStopped, 0, 0);
        }
    }

    private static int Handler(int control, int eventType, IntPtr eventData, IntPtr context)
    {
        if (control is ServiceControlStop or ServiceControlShutdown)
        {
            SetStatus(ServiceStopPending, 0, 10_000);
            cancellation?.Cancel();
        }
        return 0;
    }

    private static void SetStatus(int state, int acceptedControls, int waitHint)
    {
        if (statusHandle == IntPtr.Zero)
            return;
        var status = new ServiceStatus
        {
            ServiceType = ServiceWin32OwnProcess,
            CurrentState = state,
            ControlsAccepted = acceptedControls,
            Win32ExitCode = 0,
            ServiceSpecificExitCode = 0,
            CheckPoint = state is ServiceStartPending or ServiceStopPending ? 1 : 0,
            WaitHint = waitHint,
        };
        _ = SetServiceStatus(statusHandle, ref status);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceTableEntry
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Name;

        public ServiceMainCallback? Callback;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public int ServiceType;
        public int CurrentState;
        public int ControlsAccepted;
        public int Win32ExitCode;
        public int ServiceSpecificExitCode;
        public int CheckPoint;
        public int WaitHint;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ServiceMainCallback(int argumentCount, IntPtr arguments);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int HandlerCallback(int control, int eventType, IntPtr eventData, IntPtr context);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceCtrlDispatcher(
        [In] ServiceTableEntry[] serviceTable);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr RegisterServiceCtrlHandlerEx(
        string? serviceName,
        HandlerCallback callback,
        IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceStatus(IntPtr serviceStatus, ref ServiceStatus status);
}
