using System.Runtime.InteropServices;

namespace BoxPilot.App.Services;

internal static class MacOSDockService
{
    private const string ObjectiveCLibrary = "/usr/lib/libobjc.A.dylib";
    private const nint RegularActivationPolicy = 0;
    private const nint AccessoryActivationPolicy = 1;

    public static void SetDockVisible(bool isVisible)
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var applicationClass = GetClass("NSApplication");
        if (applicationClass == 0)
            return;

        var application = Send(applicationClass, RegisterSelector("sharedApplication"));
        if (application == 0)
            return;

        // Accessory mode removes the Dock entry but keeps the menu-bar tray item alive.
        var policy = isVisible ? RegularActivationPolicy : AccessoryActivationPolicy;
        _ = SendIntegerArgument(
            application,
            RegisterSelector("setActivationPolicy:"),
            policy);
        if (isVisible)
        {
            SendBooleanArgument(
                application,
                RegisterSelector("activateIgnoringOtherApps:"),
                1);
        }
    }

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_getClass")]
    private static extern nint GetClass(string name);

    [DllImport(ObjectiveCLibrary, EntryPoint = "sel_registerName")]
    private static extern nint RegisterSelector(string name);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern byte SendIntegerArgument(nint receiver, nint selector, nint argument);

    [DllImport(ObjectiveCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendBooleanArgument(nint receiver, nint selector, byte argument);
}
