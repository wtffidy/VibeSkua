using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Skua.Core.Models;
using Skua.Core.Services;

if (args.Length != 3)
    throw new ArgumentException("Expected the native library, packaged AQW SWF, and bridge fixture SWF paths.");

string nativePath = Path.GetFullPath(args[0]);
string swfPath = Path.GetFullPath(args[1]);
string bridgeSwfPath = Path.GetFullPath(args[2]);
byte[] swfData = File.ReadAllBytes(swfPath);
byte[] bridgeSwfData = File.ReadAllBytes(bridgeSwfPath);

UnifiedSettingsService settingsService = new();
settingsService.Initialize(AppRole.Client);
Console.WriteLine($"Initialized VibeSkua settings at {ClientFileSources.SkuaSettingsDIR}.");

nint library = NativeLibrary.Load(nativePath);

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

    nint gameSession = CreateSession(create, swfData, swfPath, 960, 540);
    try
    {
        Require(mouseEnter(gameSession), "mouse enter");
        Require(mouseMove(gameSession, 120, 90), "mouse move");
        Require(mouseDown(gameSession, 120, 90, 0), "mouse down");
        Require(mouseUp(gameSession, 120, 90, 0), "mouse up");
        Require(mouseWheel(gameSession, 1), "mouse wheel");
        Require(mouseLeave(gameSession), "mouse leave");
        Require(focus(gameSession, true), "focus gain");
        Require(keyInput(gameSession, "Space", "Space", " ", "", true), "space key down");
        Require(keyInput(gameSession, "Space", "Space", " ", "", false), "space key up");
        Require(textInput(gameSession, 32), "text input");
        Require(focus(gameSession, false), "focus loss");
        Require(resize(gameSession, 960, 540, 1.0), "resize");
        Require(allowSocket(gameSession, "game.aq.com", 843), "socket allow-list update");

        TickFrames(tick, gameSession, 6);

        if (!getSize(gameSession, out uint width, out uint height))
            throw new InvalidOperationException("Ruffle did not return its viewport dimensions.");

        int frameLength = checked((int)(width * height * 4));
        nint frameBuffer = Marshal.AllocHGlobal(frameLength);
        try
        {
            nuint written = render(gameSession, frameBuffer, (nuint)frameLength);
            if (written != (nuint)frameLength)
                throw new InvalidOperationException($"Ruffle rendered {written} bytes; expected {frameLength}.");

            byte[] pixels = new byte[frameLength];
            Marshal.Copy(frameBuffer, pixels, 0, frameLength);
            Console.WriteLine($"AQW SWF rendered {width}x{height} RGBA frame ({pixels.Length} bytes).");
        }
        finally
        {
            Marshal.FreeHGlobal(frameBuffer);
        }
    }
    finally
    {
        destroy(gameSession);
    }

    nint bridgeSession = CreateSession(create, bridgeSwfData, bridgeSwfPath, 960, 540);
    try
    {
        TickFrames(tick, bridgeSession, 6);

        nint callBuffer = Marshal.AllocHGlobal(4096);
        try
        {
            nuint callLength = callActionScript(bridgeSession, "isTrue", "[]", callBuffer, 4096);
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

        nint callbackBuffer = Marshal.AllocHGlobal(64 * 1024);
        try
        {
            nuint callbackLength = drainCallbacks(bridgeSession, callbackBuffer, 64 * 1024);
            string callbackJson = ReadUtf8Result(callbackBuffer, callbackLength);
            using JsonDocument callbacks = JsonDocument.Parse(callbackJson);
            bool callbackReceived = callbacks.RootElement.EnumerateArray().Any(item =>
                item.TryGetProperty("function", out JsonElement function) &&
                function.GetString() == "bridgeSmoke" &&
                item.TryGetProperty("arguments", out JsonElement arguments) &&
                arguments.ValueKind == JsonValueKind.Array &&
                arguments.GetArrayLength() == 1 &&
                arguments[0].GetString() == "ready");

            if (!callbackReceived)
                throw new InvalidOperationException($"ActionScript-to-host ExternalInterface callback was not delivered. Received: {callbackJson}");

            Console.WriteLine("Verified ActionScript-to-host ExternalInterface callback delivery.");
        }
        finally
        {
            Marshal.FreeHGlobal(callbackBuffer);
        }

    }
    finally
    {
        destroy(bridgeSession);
    }

    Console.WriteLine($"Loaded {Path.GetFileName(nativePath)}, opened {Path.GetFileName(swfPath)}, verified {requiredExports.Length} exports, input, socket allow-list, ticking, resize, rendering, and both ExternalInterface directions.");
}
finally
{
    NativeLibrary.Free(library);
}

static nint CreateSession(SessionCreateDelegate create, byte[] swfData, string swfPath, uint width, uint height)
{
    byte[] errorBuffer = new byte[2048];
    nint session = create(swfData, (nuint)swfData.Length, swfPath, width, height, errorBuffer, (nuint)errorBuffer.Length);

    if (session != IntPtr.Zero)
        return session;

    string error = Encoding.UTF8.GetString(errorBuffer).TrimEnd('\0');
    throw new InvalidOperationException($"Ruffle initialization failed for '{Path.GetFileName(swfPath)}': {error}");
}

static void TickFrames(SessionTickDelegate tick, nint session, int frameCount)
{
    for (int frame = 0; frame < frameCount; frame++)
        if (tick(session, 16.67) != 0)
            throw new InvalidOperationException($"Ruffle tick failed on frame {frame + 1}.");
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
