using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Sde.App.Controls;

/// <summary>
/// Embeds a native macOS <c>WKWebView</c> inside an Avalonia visual tree via <see cref="NativeControlHost"/>.
/// Uses direct Objective-C runtime P/Invoke (matching <see cref="Services.MacNative"/>) with zero extra NuGet
/// dependencies. On non-macOS platforms or in Avalonia's headless test platform, it safely renders nothing.
/// </summary>
public sealed class MacWebView : NativeControlHost
{
	private const string ObjC = "/usr/lib/libobjc.A.dylib";
	private const string WebKitPath = "/System/Library/Frameworks/WebKit.framework/WebKit";
	private const string HandlerName = "sde";

	private static readonly object InitLock = new();
	private static bool _runtimeReady;
	private static IntPtr _handlerClass;
	private static readonly ConcurrentDictionary<IntPtr, WeakReference<MacWebView>> Handlers = new();

	private IntPtr _webView;
	private IntPtr _userContentController;
	private IntPtr _handlerInstance;
	private string _pendingUrl;
	private bool _nativeHidden;

	/// <summary>Raised on the UI thread when JS calls <c>window.webkit.messageHandlers.sde.postMessage(json)</c>.</summary>
	public event Action<string> MessageReceived;

	/// <summary>Raised on the UI thread once the native WKWebView has been created and attached.</summary>
	public event Action NativeReady;

	/// <summary>True when running on macOS with a real AppKit window (false in headless tests or other OSes).</summary>
	public static bool IsSupportedFor(TopLevel topLevel) =>
		OperatingSystem.IsMacOS()
		&& topLevel?.TryGetPlatformHandle() is { Handle: var h, HandleDescriptor: "NSView" or "NSWindow" }
		&& h != IntPtr.Zero;

	/// <summary>True once the underlying <c>WKWebView</c> exists.</summary>
	public bool IsNativeCreated => _webView != IntPtr.Zero;

	/// <summary>Navigates the <c>WKWebView</c> to <paramref name="url"/> (queued if the native view isn't created yet).</summary>
	public void Navigate(string url)
	{
		_pendingUrl = url;
		if (_webView == IntPtr.Zero || string.IsNullOrEmpty(url))
		{
			return;
		}
		LoadUrlCore(_webView, url);
	}

	/// <summary>Runs JavaScript in the <c>WKWebView</c> (fire-and-forget; replies come via <see cref="MessageReceived"/>).</summary>
	public void ExecuteScript(string script)
	{
		if (_webView == IntPtr.Zero || string.IsNullOrEmpty(script))
		{
			return;
		}
		IntPtr pool = SendPtr(SendPtr(Cls("NSAutoreleasePool"), Sel("alloc")), Sel("init"));
		try
		{
			IntPtr nsScript = CreateNSString(script);
			if (nsScript != IntPtr.Zero)
			{
				SendVoid(_webView, Sel("evaluateJavaScript:completionHandler:"), nsScript, IntPtr.Zero);
			}
		}
		finally
		{
			if (pool != IntPtr.Zero)
			{
				SendVoid(pool, Sel("drain"));
			}
		}
	}

	/// <summary>
	/// Hides or shows the native <c>NSView</c> directly so Avalonia modal sheets, the command palette and toolbar
	/// flyouts are never clipped behind the <c>WKWebView</c> (macOS airspace).
	/// </summary>
	public void SetNativeHidden(bool hidden)
	{
		_nativeHidden = hidden;
		if (_webView != IntPtr.Zero)
		{
			SendVoidBool(_webView, Sel("setHidden:"), hidden);
		}
	}

	protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
	{
		if (!OperatingSystem.IsMacOS() || parent?.HandleDescriptor is not ("NSView" or "NSWindow"))
		{
			return base.CreateNativeControlCore(parent);
		}

		EnsureRuntime();
		if (_handlerClass == IntPtr.Zero)
		{
			return base.CreateNativeControlCore(parent);
		}

		IntPtr pool = SendPtr(SendPtr(Cls("NSAutoreleasePool"), Sel("alloc")), Sel("init"));
		try
		{
			// WKWebViewConfiguration *config = [[WKWebViewConfiguration alloc] init];
			IntPtr config = SendPtr(SendPtr(Cls("WKWebViewConfiguration"), Sel("alloc")), Sel("init"));
			IntPtr ucc = SendPtr(config, Sel("userContentController"));

			// Register window.webkit.messageHandlers.sde
			IntPtr handler = SendPtr(SendPtr(_handlerClass, Sel("alloc")), Sel("init"));
			Handlers[handler] = new WeakReference<MacWebView>(this);
			IntPtr nsName = CreateNSString(HandlerName);
			SendVoid(ucc, Sel("addScriptMessageHandler:name:"), handler, nsName);

#if DEBUG
			// Allow Safari Web Inspector in Debug builds.
			IntPtr prefs = SendPtr(config, Sel("preferences"));
			if (prefs != IntPtr.Zero)
			{
				IntPtr yesNum = SendPtrBool(Cls("NSNumber"), Sel("numberWithBool:"), true);
				SendVoid(prefs, Sel("setValue:forKey:"), yesNum, CreateNSString("developerExtrasEnabled"));
			}
#endif

			// WKWebView *webView = [[WKWebView alloc] initWithFrame:CGRectZero configuration:config];
			IntPtr webView = SendInitWithFrameConfig(
				SendPtr(Cls("WKWebView"), Sel("alloc")),
				Sel("initWithFrame:configuration:"),
				default,
				config);

			// Transparent background so light/dark theme transitions don't flash white.
			IntPtr noNum = SendPtrBool(Cls("NSNumber"), Sel("numberWithBool:"), false);
			SendVoid(webView, Sel("setValue:forKey:"), noNum, CreateNSString("drawsBackground"));

#if DEBUG
			IntPtr setInspectable = Sel("setInspectable:");
			if (SendBoolPtr(webView, Sel("respondsToSelector:"), setInspectable))
			{
				SendVoidBool(webView, setInspectable, true);
			}
#endif

			// Retain userContentController so teardown can safely remove our handler.
			SendPtr(ucc, Sel("retain"));
			// Release config (WKWebView copied it).
			SendVoid(config, Sel("release"));

			_webView = webView;
			_userContentController = ucc;
			_handlerInstance = handler;

			if (_nativeHidden)
			{
				SendVoidBool(_webView, Sel("setHidden:"), true);
			}

			if (!string.IsNullOrEmpty(_pendingUrl))
			{
				LoadUrlCore(_webView, _pendingUrl);
			}

			Dispatcher.UIThread.Post(() => NativeReady?.Invoke(), DispatcherPriority.Loaded);
			return new PlatformHandle(webView, "NSView");
		}
		finally
		{
			if (pool != IntPtr.Zero)
			{
				SendVoid(pool, Sel("drain"));
			}
		}
	}

	protected override void DestroyNativeControlCore(IPlatformHandle control)
	{
		IntPtr webView = Interlocked.Exchange(ref _webView, IntPtr.Zero);
		IntPtr ucc = Interlocked.Exchange(ref _userContentController, IntPtr.Zero);
		IntPtr handler = Interlocked.Exchange(ref _handlerInstance, IntPtr.Zero);

		if (handler != IntPtr.Zero)
		{
			Handlers.TryRemove(handler, out _);
		}

		if (OperatingSystem.IsMacOS())
		{
			IntPtr pool = SendPtr(SendPtr(Cls("NSAutoreleasePool"), Sel("alloc")), Sel("init"));
			try
			{
				if (ucc != IntPtr.Zero)
				{
					SendVoid(ucc, Sel("removeScriptMessageHandlerForName:"), CreateNSString(HandlerName));
					SendVoid(ucc, Sel("release"));
				}
				if (webView != IntPtr.Zero)
				{
					SendVoid(webView, Sel("stopLoading"));
					SendVoid(webView, Sel("removeFromSuperview"));
					SendVoid(webView, Sel("release"));
				}
				if (handler != IntPtr.Zero)
				{
					SendVoid(handler, Sel("release"));
				}
			}
			finally
			{
				if (pool != IntPtr.Zero)
				{
					SendVoid(pool, Sel("drain"));
				}
			}
		}

		base.DestroyNativeControlCore(control);
	}

	private static void LoadUrlCore(IntPtr webView, string url)
	{
		IntPtr pool = SendPtr(SendPtr(Cls("NSAutoreleasePool"), Sel("alloc")), Sel("init"));
		try
		{
			IntPtr nsUrlStr = CreateNSString(url);
			IntPtr nsUrl = SendPtrPtr(Cls("NSURL"), Sel("URLWithString:"), nsUrlStr);
			if (nsUrl == IntPtr.Zero)
			{
				return;
			}
			IntPtr request = SendPtrPtr(Cls("NSURLRequest"), Sel("requestWithURL:"), nsUrl);
			if (request != IntPtr.Zero)
			{
				SendPtrPtr(webView, Sel("loadRequest:"), request);
			}
		}
		finally
		{
			if (pool != IntPtr.Zero)
			{
				SendVoid(pool, Sel("drain"));
			}
		}
	}

	private static void EnsureRuntime()
	{
		if (_runtimeReady)
		{
			return;
		}
		lock (InitLock)
		{
			if (_runtimeReady)
			{
				return;
			}
			NativeLibrary.Load(WebKitPath);
			IntPtr existing = Cls("SdeWebViewMessageHandler");
			if (existing != IntPtr.Zero)
			{
				_handlerClass = existing;
				_runtimeReady = true;
				return;
			}
			IntPtr nsObject = Cls("NSObject");
			IntPtr cls = ObjCAllocateClassPair(nsObject, "SdeWebViewMessageHandler", UIntPtr.Zero);
			if (cls != IntPtr.Zero)
			{
				unsafe
				{
					delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void> cb = &OnScriptMessage;
					ClassAddMethod(cls, Sel("userContentController:didReceiveScriptMessage:"), (IntPtr)cb, "v@:@@");
				}
				ObjCRegisterClassPair(cls);
				_handlerClass = cls;
			}
			_runtimeReady = true;
		}
	}

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	private static void OnScriptMessage(IntPtr self, IntPtr cmd, IntPtr controller, IntPtr message)
	{
		try
		{
			if (!Handlers.TryGetValue(self, out WeakReference<MacWebView> weak) || !weak.TryGetTarget(out MacWebView view))
			{
				return;
			}
			IntPtr body = SendPtr(message, Sel("body"));
			if (body == IntPtr.Zero)
			{
				return;
			}
			IntPtr utf8Sel = Sel("UTF8String");
			if (!SendBoolPtr(body, Sel("respondsToSelector:"), utf8Sel))
			{
				return;
			}
			IntPtr utf8 = SendPtr(body, utf8Sel);
			if (utf8 == IntPtr.Zero)
			{
				return;
			}
			string json = Marshal.PtrToStringUTF8(utf8);
			if (json != null)
			{
				Dispatcher.UIThread.Post(() => view.MessageReceived?.Invoke(json));
			}
		}
		catch
		{
			// Never let a managed exception unwind across the Objective-C runtime boundary.
		}
	}

	private static IntPtr CreateNSString(string text)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(text + "\0");
		unsafe
		{
			fixed (byte* ptr = bytes)
			{
				return SendPtrPtr(Cls("NSString"), Sel("stringWithUTF8String:"), (IntPtr)ptr);
			}
		}
	}

	private static IntPtr Cls(string name) => ObjCGetClass(name);
	private static IntPtr Sel(string name) => SelRegisterName(name);

	[StructLayout(LayoutKind.Sequential)]
	private struct CGRect
	{
		public double X;
		public double Y;
		public double Width;
		public double Height;
	}

	[DllImport(ObjC, EntryPoint = "objc_getClass")]
	private static extern IntPtr ObjCGetClass(string name);

	[DllImport(ObjC, EntryPoint = "sel_registerName")]
	private static extern IntPtr SelRegisterName(string name);

	[DllImport(ObjC, EntryPoint = "objc_allocateClassPair")]
	private static extern IntPtr ObjCAllocateClassPair(IntPtr superclass, string name, UIntPtr extraBytes);

	[DllImport(ObjC, EntryPoint = "objc_registerClassPair")]
	private static extern void ObjCRegisterClassPair(IntPtr cls);

	[DllImport(ObjC, EntryPoint = "class_addMethod")]
	[return: MarshalAs(UnmanagedType.I1)]
	private static extern bool ClassAddMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);

	[DllImport(ObjC, EntryPoint = "objc_msgSend")]
	private static extern IntPtr SendPtr(IntPtr receiver, IntPtr selector);

	[DllImport(ObjC, EntryPoint = "objc_msgSend")]
	private static extern IntPtr SendPtrPtr(IntPtr receiver, IntPtr selector, IntPtr arg1);

	[DllImport(ObjC, EntryPoint = "objc_msgSend")]
	private static extern IntPtr SendPtrBool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool arg1);

	[DllImport(ObjC, EntryPoint = "objc_msgSend")]
	[return: MarshalAs(UnmanagedType.I1)]
	private static extern bool SendBoolPtr(IntPtr receiver, IntPtr selector, IntPtr arg1);

	[DllImport(ObjC, EntryPoint = "objc_msgSend")]
	private static extern void SendVoid(IntPtr receiver, IntPtr selector);

	[DllImport(ObjC, EntryPoint = "objc_msgSend")]
	private static extern void SendVoid(IntPtr receiver, IntPtr selector, IntPtr arg1);

	[DllImport(ObjC, EntryPoint = "objc_msgSend")]
	private static extern void SendVoid(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2);

	[DllImport(ObjC, EntryPoint = "objc_msgSend")]
	private static extern void SendVoidBool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool arg1);

	[DllImport(ObjC, EntryPoint = "objc_msgSend")]
	private static extern IntPtr SendInitWithFrameConfig(IntPtr receiver, IntPtr selector, CGRect frame, IntPtr config);
}
