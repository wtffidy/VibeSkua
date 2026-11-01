using Skua.Core.Interfaces;
using System;
using System.Xml.Linq;

namespace Skua.App.Avalonia.Flash;

#if IS_WINDOWS
using AxShockwaveFlashObjects;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Flash;
using Skua.Core.Messaging;
using Skua.Core.Utils;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading;
using System.Windows.Forms;

public class FlashUtil : IFlashUtil
{
    public FlashUtil(IMessenger messenger, Lazy<IScriptManager> manager)
    {
        _messenger = messenger;
        _lazyManager = manager;
    }

    private readonly IMessenger _messenger;
    private readonly Lazy<IScriptManager> _lazyManager;
    private AxShockwaveFlash? Flash;

    public event FlashCallHandler? FlashCall;

    private static byte[]? _cachedSwf;

    public static void PreloadSwf()
    {
        try
        {
            if (_cachedSwf != null)
                return;

            string swfPath = Path.Combine(AppContext.BaseDirectory, "skua.swf");
            if (File.Exists(swfPath))
                _cachedSwf = File.ReadAllBytes(swfPath);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to preload skua.swf: {ex}");
        }
    }

    public void InitializeFlash()
    {
        string swfPath = Path.Combine(AppContext.BaseDirectory, "skua.swf");
        if (!File.Exists(swfPath))
        {
            MessageBox.Show(
                "Missing skua.swf. Build the AS3 client first so Skua.App.Avalonia can load the game content.",
                "Missing game content",
                MessageBoxButtons.OK,
                MessageBoxIcon.Exclamation);
            Environment.Exit(0);
            return;
        }

        try
        {
            if (!EoLHook.IsHooked)
                EoLHook.Hook();

            CleanupOldFlash();

            AxShockwaveFlash flash = new();
            flash.BeginInit();
            flash.Name = "flash";
            flash.Dock = DockStyle.Fill;
            flash.TabIndex = 0;
            flash.FlashCall += CallHandler;

            _messenger.Send<FlashChangedMessage<AxShockwaveFlash>>(new(flash));
            flash.EndInit();
            flash.WMode = "window";
            flash.ScaleMode = 0;
            flash.CtlScale = "ShowAll";
            Flash = flash;
            byte[] swf = _cachedSwf ?? File.ReadAllBytes(swfPath);
            using MemoryStream stream = new();
            using BinaryWriter writer = new(stream);
            writer.Write(8 + swf.Length);
            writer.Write(1432769894);
            writer.Write(swf.Length);
            writer.Write(swf);
            writer.Seek(0, SeekOrigin.Begin);
            flash.OcxState = new AxHost.State(stream, 1, false, null);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Flash initialization error: {ex}");
            if (MessageBox.Show($"Please, uninstall and then reinstall CleanFlash via the Gif provided \r\nDo you want view the instructional Gif?", "Clean Flash missing/installed incorrectly", MessageBoxButtons.YesNo, MessageBoxIcon.Exclamation) == DialogResult.Yes)
                Ioc.Default.GetRequiredService<IProcessService>().OpenLink("https://imgur.com/ztsLYZ1");
            Environment.Exit(0);
        }
        finally
        {
            EoLHook.Unhook();
        }
    }

    private void CleanupOldFlash()
    {
        if (Flash == null) return;

        Flash.FlashCall -= CallHandler;

        try
        {
            Flash.Stop();
        }
        catch { /* ignored */ }

        object? ocx = null;
        try
        {
            ocx = Flash.GetOcx();
        }
        catch { /* ignored */ }

        Flash.Dispose();
        Flash = null;

        if (ocx != null)
        {
            try
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(ocx);
            }
            catch { /* ignored */ }
        }
    }

    private void CallHandler(object sender, _IShockwaveFlashEvents_FlashCallEvent e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(e?.request))
                return;

            XElement el = XElement.Parse(e.request);
            string? function = el.Attribute("name")?.Value;
            if (string.IsNullOrEmpty(function))
                return;

            object[] args = el.Elements().Select(x => FromFlashXml(x)).ToArray();
            FlashCall?.Invoke(function, args);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Flash CallHandler exception: {ex.Message}");
        }
    }

    public string Call(string function, params object[] args)
    {
        return Call<string>(function, args);
    }

    public T Call<T>(string function, params object[] args)
    {
        try
        {
            object? o = Call(function, typeof(T), args);
            return o is T value ? value : GetDefaultValue<T>();
        }
        catch
        {
            return GetDefaultValue<T>();
        }
    }

    private static T GetDefaultValue<T>()
    {
        object? defaultValue = DefaultProvider.GetDefault<T>(typeof(T));
        return defaultValue is T typed ? typed : default!;
    }

    public object Call(string function, Type type, params object[] args)
    {
        if (_lazyManager.Value.ShouldExit && Thread.CurrentThread.Name == "Script Thread")
            _lazyManager.Value.ScriptCts?.Token.ThrowIfCancellationRequested();

        try
        {
            if (Flash is null)
                return default!;

            StringBuilder req = new StringBuilder().Append($"<invoke name=\"{function}\" returntype=\"xml\">");
            if (args.Length > 0)
            {
                req.Append("<arguments>");
                args.ForEach(o => req.Append(ToFlashXml(o)));
                req.Append("</arguments>");
            }
            req.Append("</invoke>");

            string? result = null;
            const int maxRetries = 2;
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                try
                {
                    result = Flash.CallFunction(req.ToString());
                    break;
                }
                catch (COMException comEx) when (attempt < maxRetries && IsTransientComError(comEx.HResult))
                {
                    Thread.Sleep(10 * (attempt + 1));
                }
            }

            if (string.IsNullOrWhiteSpace(result))
                return default!;

            XElement el = XElement.Parse(result);
            if (el.FirstNode is null)
                return default!;

            object? value = Convert.ChangeType(el.FirstNode.ToString(), type);
            return value ?? default!;
        }
        catch (Exception e)
        {
            if (function != "isKicked")
                _messenger.Send<FlashErrorMessage>(new(e, function, args));
            return default!;
        }
    }

    private static bool IsTransientComError(int hresult)
    {
        return hresult == unchecked((int)0x8001010A) || hresult == unchecked((int)0x80010001);
    }

    public static string ToFlashXml(object o)
    {
        switch (o)
        {
            case null:
                return "<null/>";

            case bool _:
                return $"<{o.ToString()!.ToLower()}/>";

            case double _:
            case float _:
            case long _:
            case int _:
                return $"<number>{o}</number>";

            case ExpandoObject _:
                StringBuilder sb = new StringBuilder().Append("<object>");
                foreach (KeyValuePair<string, object> kvp in (o as IDictionary<string, object>)!)
                    sb.Append($"<property id=\"{kvp.Key}\">{ToFlashXml(kvp.Value)}</property>");
                return sb.Append("</object>").ToString();

            default:
                if (o is Array)
                {
                    StringBuilder _sb = new StringBuilder().Append("<array>");
                    int k = 0;
                    foreach (object el in (o as Array)!)
                        _sb.Append($"<property id=\"{k++}\">{ToFlashXml(el)}</property>");
                    return _sb.Append("</array>").ToString();
                }
                return $"<string>{SecurityElement.Escape(o.ToString())}</string>";
        }
    }

    public object FromFlashXml(XElement el)
    {
        switch (el.Name.ToString())
        {
            case "number":
                return int.TryParse(el.Value, out int i) ? i : float.TryParse(el.Value, out float f) ? f : 0;

            case "true":
                return true;

            case "false":
                return false;

            case "null":
                return null!;

            case "array":
                return el.Elements().Select(e => FromFlashXml(e)).ToArray();

            case "object":
                dynamic d = new ExpandoObject();
                el.Elements().ForEach(e => d[e.Attribute("id")!.Value] = FromFlashXml(e.Elements().First()));
                return d;

            default:
                return el.Value;
        }
    }

    public IFlashObject<T> CreateFlashObject<T>(string path)
    {
        return new FlashObject<T>(Call<int>("lnkCreate", path), this);
    }

    public void Dispose()
    {
        try
        {
            CleanupOldFlash();
        }
        catch { /* ignored */ }
        finally
        {
            EoLHook.Unhook();
        }
    }
}
#else
using global::Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Skua.Core.Flash;
using Skua.Core.Messaging;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Dynamic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public class FlashUtil : IFlashUtil
{
    private const int CallBufferSize = 4 * 1024 * 1024;

    [DllImport("skua_ruffle_native", EntryPoint = "skua_ruffle_session_create", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SessionCreate([In] byte[] swfData, nuint swfLength, [MarshalAs(UnmanagedType.LPUTF8Str)] string swfPath, uint width, uint height, [Out] byte[] errorBuffer, nuint errorCapacity);

    [DllImport("skua_ruffle_native", EntryPoint = "skua_ruffle_session_tick", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SessionTick(IntPtr session, double elapsedMilliseconds);

    [DllImport("skua_ruffle_native", EntryPoint = "skua_ruffle_session_render", CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint SessionRender(IntPtr session, IntPtr output, nuint capacity);

    [DllImport("skua_ruffle_native", EntryPoint = "skua_ruffle_session_drain_callbacks", CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint SessionDrainCallbacks(IntPtr session, IntPtr output, nuint capacity);

    [DllImport("skua_ruffle_native", EntryPoint = "skua_ruffle_session_call", CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint SessionCall(IntPtr session, [MarshalAs(UnmanagedType.LPUTF8Str)] string functionName, [MarshalAs(UnmanagedType.LPUTF8Str)] string argumentsJson, IntPtr output, nuint capacity);

    [DllImport("skua_ruffle_native", EntryPoint = "skua_ruffle_session_resize", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SessionResize(IntPtr session, uint width, uint height, double scaleFactor);

    [DllImport("skua_ruffle_native", EntryPoint = "skua_ruffle_session_get_size", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SessionGetSize(IntPtr session, out uint width, out uint height);


    [DllImport("skua_ruffle_native", EntryPoint = "skua_ruffle_session_mouse_move", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SessionMouseMove(IntPtr session, double x, double y);

    [DllImport("skua_ruffle_native", EntryPoint = "skua_ruffle_session_mouse_enter", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SessionMouseEnter(IntPtr session);

    [DllImport("skua_ruffle_native", EntryPoint = "skua_ruffle_session_mouse_down", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SessionMouseDown(IntPtr session, double x, double y, int button);

    [DllImport("skua_ruffle_native", EntryPoint = "skua_ruffle_session_mouse_up", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SessionMouseUp(IntPtr session, double x, double y, int button);

    [DllImport("skua_ruffle_native", EntryPoint = "skua_ruffle_session_mouse_wheel", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SessionMouseWheel(IntPtr session, double deltaY);

    [DllImport("skua_ruffle_native", EntryPoint = "skua_ruffle_session_mouse_leave", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SessionMouseLeave(IntPtr session);

    [DllImport("skua_ruffle_native", EntryPoint = "skua_ruffle_session_key", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SessionKey(IntPtr session,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string physicalKey,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string key,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string keySymbol,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string modifiers,
        [MarshalAs(UnmanagedType.I1)] bool isDown);

    [DllImport("skua_ruffle_native", EntryPoint = "skua_ruffle_session_text_input", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SessionTextInput(IntPtr session, uint codepoint);

    [DllImport("skua_ruffle_native", EntryPoint = "skua_ruffle_session_focus", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SessionFocus(IntPtr session, [MarshalAs(UnmanagedType.I1)] bool focused);

    [DllImport("skua_ruffle_native", EntryPoint = "skua_ruffle_session_destroy", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SessionDestroy(IntPtr session);

    private readonly IMessenger _messenger;
    private readonly Lazy<IScriptManager> _lazyManager;
    private IntPtr _session;
    private IntPtr _callBuffer;
    private IntPtr _renderBuffer;
    private int _renderBufferSize;
    private byte[] _managedFrameBuffer = Array.Empty<byte>();
    private uint _width = 960;
    private uint _height = 540;

    public uint Width => _width;
    public uint Height => _height;

    public FlashUtil(IMessenger messenger, Lazy<IScriptManager> manager)
    {
        _messenger = messenger;
        _lazyManager = manager;
    }

    public event FlashCallHandler? FlashCall;

    public static void PreloadSwf()
    {
    }

    public bool IsInitialized => _session != IntPtr.Zero;

    public void InitializeFlash()
    {
        if (_session != IntPtr.Zero)
            return;

        string swfPath = Path.Combine(AppContext.BaseDirectory, "skua.swf");
        if (!File.Exists(swfPath))
            throw new FileNotFoundException("Missing skua.swf. Build the AS3 client content before launching VibeSkua.", swfPath);

        byte[] swfData = File.ReadAllBytes(swfPath);
        byte[] errorBuffer = new byte[2048];
        try
        {
            _session = SessionCreate(
                swfData,
                (nuint)swfData.Length,
                swfPath,
                _width,
                _height,
                errorBuffer,
                (nuint)errorBuffer.Length);

            if (_session == IntPtr.Zero)
            {
                string error = Encoding.UTF8.GetString(errorBuffer).TrimEnd('\0');
                throw new InvalidOperationException($"Ruffle initialization failed: {error}");
            }

            _callBuffer = Marshal.AllocHGlobal(CallBufferSize);
        }
        catch
        {
            if (_session != IntPtr.Zero)
            {
                SessionDestroy(_session);
                _session = IntPtr.Zero;
            }
            throw;
        }
    }

    public void Tick(double elapsedMilliseconds)
    {
        if (_session == IntPtr.Zero)
            return;

        InvokeOnUiThread(() =>
        {
            int result = SessionTick(_session, elapsedMilliseconds);
            if (result != 0)
                throw new InvalidOperationException("Ruffle failed while advancing the player.");
            DrainFlashCalls();
            return true;
        });
    }

    public byte[]? RenderFrame()
    {
        if (_session == IntPtr.Zero)
            return null;

        return InvokeOnUiThread(() =>
        {
            if (!SessionGetSize(_session, out uint width, out uint height))
                return null;

            int frameLength = checked((int)(width * height * 4));
            if (_managedFrameBuffer.Length != frameLength)
                _managedFrameBuffer = new byte[frameLength];
            EnsureRenderBufferCapacity(frameLength);
            nuint written = SessionRender(_session, _renderBuffer, (nuint)_renderBufferSize);
            if (written == nuint.MaxValue || written == 0)
                return null;
            if (written != (nuint)frameLength)
                throw new InvalidOperationException("Ruffle returned an invalid frame buffer size.");

            Marshal.Copy(_renderBuffer, _managedFrameBuffer, 0, frameLength);
            return _managedFrameBuffer;
        });
    }

    private void EnsureRenderBufferCapacity(int required)
    {
        if (required <= _renderBufferSize)
            return;

        if (_renderBuffer != IntPtr.Zero)
            Marshal.FreeHGlobal(_renderBuffer);

        _renderBuffer = Marshal.AllocHGlobal(required);
        _renderBufferSize = required;
    }


    public void MouseMove(double x, double y)
    {
        if (_session != IntPtr.Zero)
            InvokeOnUiThread(() => SessionMouseMove(_session, x, y));
    }

    public void MouseEnter()
    {
        if (_session != IntPtr.Zero)
            InvokeOnUiThread(() => SessionMouseEnter(_session));
    }

    public void MouseDown(double x, double y, int button)
    {
        if (_session != IntPtr.Zero)
            InvokeOnUiThread(() => SessionMouseDown(_session, x, y, button));
    }

    public void MouseUp(double x, double y, int button)
    {
        if (_session != IntPtr.Zero)
            InvokeOnUiThread(() => SessionMouseUp(_session, x, y, button));
    }

    public void MouseWheel(double deltaY)
    {
        if (_session != IntPtr.Zero)
            InvokeOnUiThread(() => SessionMouseWheel(_session, deltaY));
    }

    public void MouseLeave()
    {
        if (_session != IntPtr.Zero)
            InvokeOnUiThread(() => SessionMouseLeave(_session));
    }

    public void KeyInput(string physicalKey, string key, string? keySymbol, string modifiers, bool isDown)
    {
        if (_session != IntPtr.Zero)
            InvokeOnUiThread(() => SessionKey(_session, physicalKey, key, keySymbol ?? string.Empty, modifiers, isDown));
    }

    public void TextInput(string? text)
    {
        if (_session == IntPtr.Zero || string.IsNullOrEmpty(text))
            return;

        InvokeOnUiThread(() =>
        {
            foreach (Rune rune in text.EnumerateRunes())
                SessionTextInput(_session, (uint)rune.Value);
            return true;
        });
    }

    public void SetFocus(bool focused)
    {
        if (_session != IntPtr.Zero)
            InvokeOnUiThread(() => SessionFocus(_session, focused));
    }

    public void Resize(uint width, uint height, double scaleFactor)
    {
        if (_session == IntPtr.Zero || width == 0 || height == 0)
            return;

        InvokeOnUiThread(() =>
        {
            if (!SessionResize(_session, width, height, scaleFactor))
                throw new InvalidOperationException("Ruffle could not resize the player viewport.");
            _width = width;
            _height = height;
            return true;
        });
    }

    public string? Call(string function, params object[] args)
    {
        return Call<string>(function, args);
    }

    public T? Call<T>(string function, params object[] args)
    {
        object? result = Call(function, typeof(T), args);
        return result is T typed ? typed : default;
    }

    public object? Call(string function, Type type, params object[] args)
    {
        if (_session == IntPtr.Zero)
            return GetDefault(type);

        if (_lazyManager.Value.ShouldExit && Thread.CurrentThread.Name == "Script Thread")
            _lazyManager.Value.ScriptCts?.Token.ThrowIfCancellationRequested();

        try
        {
            return InvokeOnUiThread(() =>
            {
                string argumentsJson = JsonConvert.SerializeObject(args);
                nuint written = SessionCall(_session, function, argumentsJson, _callBuffer, (nuint)CallBufferSize);
                if (written == nuint.MaxValue || written == 0 || written > (nuint)CallBufferSize)
                    throw new InvalidOperationException($"Ruffle call '{function}' failed or exceeded the native result buffer.");

                byte[] resultBytes = new byte[(int)written - 1];
                Marshal.Copy(_callBuffer, resultBytes, 0, resultBytes.Length);
                string resultJson = Encoding.UTF8.GetString(resultBytes);
                object? converted = ConvertJsonToken(JToken.Parse(resultJson));
                object? typedResult = type == typeof(object) || converted == null || type.IsInstanceOfType(converted)
                    ? converted
                    : JsonConvert.DeserializeObject(resultJson, type);
                DrainFlashCalls();
                return typedResult;
            });
        }
        catch (Exception ex)
        {
            if (function != "isKicked")
                _messenger.Send<FlashErrorMessage>(new(ex, function, args));
            return GetDefault(type);
        }
    }

    public object FromFlashXml(XElement element)
    {
        return element.Name.ToString() switch
        {
            "number" => int.TryParse(element.Value, out int intValue) ? intValue : float.TryParse(element.Value, out float floatValue) ? floatValue : 0,
            "true" => true,
            "false" => false,
            "null" => null!,
            "array" => element.Elements().Select(FromFlashXml).ToArray(),
            "object" => ConvertFlashObject(element),
            _ => element.Value
        };
    }

    private object ConvertFlashObject(XElement element)
    {
        IDictionary<string, object> values = new ExpandoObject();
        foreach (XElement property in element.Elements())
        {
            string? name = property.Attribute("id")?.Value;
            XElement? value = property.Elements().FirstOrDefault();
            if (name != null && value != null)
                values[name] = FromFlashXml(value);
        }
        return (ExpandoObject)values;
    }

    public IFlashObject<T> CreateFlashObject<T>(string path)
    {
        return new FlashObject<T>(Call<int>("lnkCreate", path), this);
    }

    private static object? ConvertJsonToken(JToken token)
    {
        if (token is JObject jsonObject)
        {
            if (jsonObject.Count == 1 && (string?)jsonObject["$ruffle"] == "undefined")
                return string.Empty;

            IDictionary<string, object> values = new ExpandoObject();
            foreach (JProperty property in jsonObject.Properties())
                values[property.Name] = ConvertJsonToken(property.Value)!;
            return (ExpandoObject)values;
        }

        if (token is JArray jsonArray)
            return jsonArray.Select(ConvertJsonToken).ToArray();

        if (token is JValue value)
        {
            return value.Type switch
            {
                JTokenType.Integer => value.Value<long>() is long number && number is >= int.MinValue and <= int.MaxValue ? (object)(int)number : number,
                JTokenType.Float => value.Value<double>(),
                JTokenType.Boolean => value.Value<bool>(),
                JTokenType.String => value.Value<string>(),
                JTokenType.Null or JTokenType.Undefined => null,
                _ => value.Value
            };
        }

        return null;
    }

    private bool _isDrainingCallbacks;

    private void DrainFlashCalls()
    {
        if (_isDrainingCallbacks || _session == IntPtr.Zero)
            return;

        _isDrainingCallbacks = true;
        try
        {
            while (true)
            {
                nuint written = SessionDrainCallbacks(_session, _callBuffer, (nuint)CallBufferSize);
                if (written == nuint.MaxValue || written == 0 || written > CallBufferSize)
                    throw new InvalidOperationException("Ruffle could not drain its ExternalInterface callback queue.");

                byte[] callbackBytes = new byte[(int)written - 1];
                Marshal.Copy(_callBuffer, callbackBytes, 0, callbackBytes.Length);
                JArray callbacks = JArray.Parse(Encoding.UTF8.GetString(callbackBytes));
                if (callbacks.Count == 0)
                    break;

                foreach (JToken callbackToken in callbacks)
                {
                    if (callbackToken is not JObject callback)
                        continue;

                    string function = (string?)callback["function"] ?? string.Empty;
                    JArray arguments = callback["arguments"] as JArray ?? new JArray();
                    object[] args = arguments.Select(token => ConvertJsonToken(token)!).ToArray();
                    try
                    {
                        FlashCall?.Invoke(function, args);
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"Ruffle FlashCall handler failed for '{function}': {ex}");
                    }
                }
            }
        }
        finally
        {
            _isDrainingCallbacks = false;
        }
    }

    private static object? GetDefault(Type type)
    {
        return type == typeof(void) ? null : type.IsValueType ? Activator.CreateInstance(type) : null;
    }

    private static T InvokeOnUiThread<T>(Func<T> action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return action();

        return Dispatcher.UIThread.InvokeAsync(action).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (_session != IntPtr.Zero)
        {
            try
            {
                InvokeOnUiThread(() =>
                {
                    SessionDestroy(_session);
                    _session = IntPtr.Zero;
                    return true;
                });
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Could not dispose Ruffle session cleanly: {ex}");
            }
        }

        if (_callBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_callBuffer);
            _callBuffer = IntPtr.Zero;
        }

        if (_renderBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_renderBuffer);
            _renderBuffer = IntPtr.Zero;
            _renderBufferSize = 0;
        }

    }
}
#endif
