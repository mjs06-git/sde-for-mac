using System.Runtime.InteropServices;

namespace Sde.App.Services;

/// <summary>
/// The few things the app asks macOS directly, through the Objective-C runtime: the Accessibility display settings
/// Reduce Motion and Increase Contrast (System Settings, Accessibility, Display). Everywhere else, and if the Mac
/// can't answer, each is simply off.
/// </summary>
public static class MacNative
{
	private const string ObjC = "/usr/lib/libobjc.A.dylib";

	[DllImport(ObjC, EntryPoint = "objc_getClass")]
	private static extern IntPtr GetClass(string name);

	[DllImport(ObjC, EntryPoint = "sel_registerName")]
	private static extern IntPtr Selector(string name);

	[DllImport(ObjC, EntryPoint = "objc_msgSend")]
	private static extern IntPtr SendForObject(IntPtr receiver, IntPtr selector);

	[DllImport(ObjC, EntryPoint = "objc_msgSend")]
	[return: MarshalAs(UnmanagedType.I1)]
	private static extern bool SendForBool(IntPtr receiver, IntPtr selector);

	/// <summary>For tests and other platforms: set to answer instead of asking the Mac.</summary>
	public static bool? ReduceMotionOverride { get; set; }

	/// <summary>For tests and other platforms: set to answer instead of asking the Mac.</summary>
	public static bool? IncreaseContrastOverride { get; set; }

	/// <summary>Reduce Motion is on: the app shows sheets and changes without animating them.</summary>
	public static bool ReduceMotion => ReduceMotionOverride ?? WorkspaceFlag("accessibilityDisplayShouldReduceMotion");

	/// <summary>Increase Contrast is on: borders and dividers are drawn stronger.</summary>
	public static bool IncreaseContrast => IncreaseContrastOverride ?? WorkspaceFlag("accessibilityDisplayShouldIncreaseContrast");

	private static bool WorkspaceFlag(string property)
	{
		if (!OperatingSystem.IsMacOS())
		{
			return false;
		}
		try
		{
			IntPtr workspaceClass = GetClass("NSWorkspace");
			if (workspaceClass == IntPtr.Zero)
			{
				return false;
			}
			IntPtr workspace = SendForObject(workspaceClass, Selector("sharedWorkspace"));
			return workspace != IntPtr.Zero && SendForBool(workspace, Selector(property));
		}
		catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
		{
			return false;
		}
	}
}
