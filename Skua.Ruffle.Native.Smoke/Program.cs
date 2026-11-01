using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Skua.Core.Models;
using Skua.Core.Services;

if (args.Length != 2)
    throw new ArgumentException("Expected the native library path and packaged SWF path.");

string nativePath = Path.GetFullPath(args[0]);
string swfPath = Path.GetFullPath(args[1]);
byte[] swfData = File.ReadAllBytes(swfPath);

UnifiedSettingsService settingsService = new();
settingsService.Initialize(AppRole.Client);
Console.WriteLine($"Initialized VibeSkua settings at {ClientFileSources.SkuaSettingsDIR}.");

nint library = NativeLibrary.Load(nativePath);
nint session = IntPtr.Zero;

try
{
    string[] requiredExports =
    [
        "skua_ruffle_session_create",
        "skua_ruffle_session_tick",
        "skua_ruffle_session_render",
        "skua_ruffle_session_drain_callbacks",
        "skua_ruffle_session_call",
        "skua_ruffle_session_mouse_move",
        "skua_ruffle_session_mouse_enter",
        "skua_ruffle_session_mouse_down",
        "skua_ruffle_session_mouse_up",
        "skua_ruffle_session_mouse_wheel",
        "skua_ruffle_session_mouse_leave",
        "skua_ruffle_session_key",
        "skua_ruffle_session_text_input",
        "skua_ruffle_session_focus",
        "skua_ruffle_session_allow_socket",
        "skua_ruffle_session_resize",
        "skua_ruffle_session_get_size",
        "skua_ruffle_session_destroy"
    ];

    foreach (string export in requiredExports)
    {
        if (!NativeLibrary.TryGetExport(library, export, out _))
            throw new EntryPointNotFoundException($"Missing native export: {export}");
    }

    SessionCreateDelegate create = GetExport<SessionCreateDelegate>(library, "skua_ruffle_session_create");
    SessionTickDelegate tick = GetExport<SessionTickDelegate>(library, "skua_ruffle_session_tick");
    SessionRenderDelegate render = GetExport<SessionRenderDelegate>(library, "skua_ruffle_session_render");
    SessionResizeDelegate resize = GetExport<SessionResizeDelegate>(library, "skua_ruffle_session_resize");
    SessionGetSizeDelegate getSize = GetExport<SessionGetSizeDelegate>(library, "skua_ruffle_session_get_size");
    SessionDrainCallbacksDelegate drainCallbacks = GetExport<SessionDrainCallbacksDelegate>(library, "skua_ruffle_session_drain_callbacks");
    SessionCallDelegate callActionScript = GetExport<SessionCallDelegate>(library, "skua_ruffle_session_call");
    SessionAllowSocketDelegate allowSocket = GetExport<SessionAllowSocketDelegate>(library, "skua_ruffle_session_allow_socket");
    SessionDestroyDelegate destroy = GetExport<SessionDestroyDelegate>(library, "skua_ruffle_session_destroy");
    SessionMouseMoveDelegate mouseMove = GetExport<SessionMouseMoveDelegate>(library, "skua_ruffle_session_mouse_move");
    SessionMouseEnterDelegate mouseEnter = GetExport<SessionMouseEnterDelegate>(library, "skua_ruffle_session_mouse_enter");
    SessionMouseButtonDelegate mouseDown = GetExport<SessionMouseButtonDelegate>(library, "skua_ruffle_session_mouse_down");
    SessionMouseButtonDelegate mouseUp = GetExport<SessionMouseButtonDelegate>(library, "skua_ruffle_session_mouse_up");
    SessionMouseWheelDelegate mouseWheel = GetExport<SessionMouseWheelDelegate>(library, "skua_ruffle_session_mouse_wheel");
    SessionMouseLeaveDelegate mouseLeave = GetExport<SessionMouseLeaveDelegate>(library, "skua_ruffle_session_mouse_leave");
    SessionKeyDelegate keyInput = GetExport<SessionKeyDelegate>(library, "skua_ruffle_session_key");
    SessionTextInputDelegate textInput = GetExport<SessionTextInputDelegate>(library, "skua_ruffle_session_text_input");
    SessionFocusDelegate focus = GetExport<SessionFocusDelegate>(library, "skua_ruffle_session_focus");

    byte[] errorBuffer = new byte[2048];
    session = create(swfData, (nuint)swfData.Length, swfPath, 960, 540, errorBuffer, (nuint)errorBuffer.Length);
    if (session == IntPtr.Zero)
    {
        string error = Encoding.UTF8.GetString(errorBuffer).TrimEnd('\0');
        throw new InvalidOperationException($"Ruffle session creation failed: {error}");
    }

    try
    {
        Require(mouseEnter(session), "mouse enter");
        Require(mouseMove(session, 120, 90), "mouse move");
        Require(mouseDown(session, 120, 90, 0), "mouse down");
        Require(mouseUp(session, 120, 90, 0), "mouse up");
        Require(mouseWheel(session, 1), "mouse wheel");
        Require(mouseLeave(session), "mouse leave");
        Require(focus(session, true), "focus gain");
        Require(keyInput(session, "Space", "Space", " ", "", true), "space key down");
        Require(keyInput(session, "Space", "Space", " ", "", false), "space key up");
        Require(textInput(session, 32), "text input");
        Require(focus(session, false), "focus loss");
        Require(resize(session, 960, 540, 1.0), "resize");
        Require(allowSocket(session, "game.aq.com", 843), "socket allow-list update");

        for (int frame = 0; frame < 3; frame++)
            if (tick(session, 16.0) != 0)
                throw new InvalidOperationException($"Ruffle tick failed on frame {frame + 1}.");

        nint callbackBuffer = Marshal.AllocHGlobal(64 * 1024);
        try
        {
            nuint callbackLength = drainCallbacks(session, callbackBuffer, 64 * 1024);
            string callbackJson = ReadUtf8Result(callbackBuffer, callbackLength);
            using JsonDocument callbacks = JsonDocument.Parse(callbackJson);
            bool requestedLoad = callbacks.RootElement.EnumerateArray().Any(item =>
                item.TryGetProperty("function", out JsonElement function) &&
                function.GetString() == "requestLoadGame");

            if (!requestedLoad)
                throw new InvalidOperationException("Ruffle did not forward the ActionScript requestLoadGame callback.");

            Console.WriteLine("Verified ActionScript-to-host ExternalInterface callback delivery.");
        }
        finally
        {
            Marshal.FreeHGlobal(callbackBuffer);
        }

        nint callBuffer = Marshal.AllocHGlobal(4096);
        try
        {
            nuint callLength = callActionScript(session, "isTrue", "[]", callBuffer, 4096);
            string resultJson = ReadUtf8Result(callBuffer, callLength);
            string? result = JsonSerializer.Deserialize<string>(resultJson);

            if (result != "true")
                throw new InvalidOperationException($"Ruffle returned an unexpected ActionScript callback result: {resultJson}");

            Console.WriteLine("Verified host-to-ActionScript ExternalInterface callback invocation.");
        }
        finally
        {
            Marshal.FreeHGlobal(callBuffer);
        }

        if (!getSize(session, out uint width, out uint height))
            throw new InvalidOperationException("Ruffle did not return its viewport dimensions.");

        int frameLength = checked((int)(width * height * 4));
        nint frameBuffer = Marshal.AllocHGlobal(frameLength);
        try
        {
            nuint written = render(session, frameBuffer, (nuint)frameLength);
            if (written != (nuint)frameLength)
                throw new InvalidOperationException($"Ruffle rendered {written} bytes; expected {frameLength}.");

            byte[] pixels = new byte[frameLength];
            Marshal.Copy(frameBuffer, pixels, 0, frameLength);
            Console.WriteLine($"Ruffle rendered {width}x{height} RGBA frame ({pixels.Length} bytes).");
        }
        finally
        {
            Marshal.FreeHGlobal(frameBuffer);
        }
    }
    finally
    {
        destroy(session);
        session = IntPtr.Zero;
    }

    Console.WriteLine($"Loaded {Path.GetFileName(nativePath)}, opened {Path.GetFileName(swfPath)}, verified {requiredExports.Length} exports, input events, ticking, resize, and rendering.");
}
finally
{
    if (session != IntPtr.Zero)
    {
        SessionDestroyDelegate destroy = GetExport<SessionDestroyDelegate>(library, "skua_ruffle_session_destroy");
        destroy(session);
    }

    NativeLibrary.Free(library);
}

static T GetExport<T>(nint library, string name) where T : Delegate
{
    IntPtr export = NativeLibrary.GetExport(library, name);
    return Marshal.GetDelegateForFunctionPointer<T>(export);
}

static string ReadUtf8Result(IntPtr buffer, nuint length)
{
    if (length == nuint.MaxValue || length == 0)
        throw new InvalidOperationException("Ruffle returned an invalid UTF-8 result buffer length.");

    return Marshal.PtrToStringUTF8(buffer, checked((int)length - 1)) ?? string.Empty;
}

static void Require(bool succeeded, string operation)
{
    if (!succeeded)
        throw new InvalidOperationException($"Ruffle rejected the {operation} event.");
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate nuint SessionDrainCallbacksDelegate(IntPtr session, IntPtr output, nuint capacity);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate nuint SessionCallDelegate(IntPtr session, [MarshalAs(UnmanagedType.LPUTF8Str)] string functionName, [MarshalAs(UnmanagedType.LPUTF8Str)] string argumentsJson, IntPtr output, nuint capacity);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
[return: MarshalAs(UnmanagedType.I1)]
delegate bool SessionAllowSocketDelegate(IntPtr session, [MarshalAs(UnmanagedType.LPUTF8Str)] string host, ushort port);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate IntPtr SessionCreateDelegate([In] byte[] swfData, nuint swfLength, [MarshalAs(UnmanagedType.LPUTF8Str)] string swfPath, uint width, uint height, [Out] byte[] errorBuffer, nuint errorCapacity);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate int SessionTickDelegate(IntPtr session, double elapsedMilliseconds);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate nuint SessionRenderDelegate(IntPtr session, IntPtr output, nuint capacity);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
[return: MarshalAs(UnmanagedType.I1)]
delegate bool SessionResizeDelegate(IntPtr session, uint width, uint height, double scaleFactor);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
[return: MarshalAs(UnmanagedType.I1)]
delegate bool SessionGetSizeDelegate(IntPtr session, out uint width, out uint height);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate void SessionDestroyDelegate(IntPtr session);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
[return: MarshalAs(UnmanagedType.I1)]
delegate bool SessionMouseMoveDelegate(IntPtr session, double x, double y);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
[return: MarshalAs(UnmanagedType.I1)]
delegate bool SessionMouseEnterDelegate(IntPtr session);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
[return: MarshalAs(UnmanagedType.I1)]
delegate bool SessionMouseButtonDelegate(IntPtr session, double x, double y, int button);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
[return: MarshalAs(UnmanagedType.I1)]
delegate bool SessionMouseWheelDelegate(IntPtr session, double deltaY);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
[return: MarshalAs(UnmanagedType.I1)]
delegate bool SessionMouseLeaveDelegate(IntPtr session);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
[return: MarshalAs(UnmanagedType.I1)]
delegate bool SessionKeyDelegate(IntPtr session, [MarshalAs(UnmanagedType.LPUTF8Str)] string physicalKey, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, [MarshalAs(UnmanagedType.LPUTF8Str)] string keySymbol, [MarshalAs(UnmanagedType.LPUTF8Str)] string modifiers, [MarshalAs(UnmanagedType.I1)] bool isDown);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
[return: MarshalAs(UnmanagedType.I1)]
delegate bool SessionTextInputDelegate(IntPtr session, uint codepoint);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
[return: MarshalAs(UnmanagedType.I1)]
delegate bool SessionFocusDelegate(IntPtr session, [MarshalAs(UnmanagedType.I1)] bool focused);
