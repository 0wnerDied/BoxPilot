using System.Runtime.InteropServices;

namespace BoxPilot.Core.Infrastructure;

internal static class ProcessPrivileges
{
    public static bool IsElevated()
    {
        if (OperatingSystem.IsWindows())
            return IsUserAnAdmin();
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            return GetEffectiveUserId() == 0;
        return false;
    }

    public static uint GetUserId()
    {
        return OperatingSystem.IsWindows() ? 0 : GetRealUserId();
    }

    [DllImport("shell32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsUserAnAdmin();

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    [DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetRealUserId();
}
