using Avalonia.Controls;
using Avalonia.Platform;
using System;
using System.Runtime.InteropServices;

namespace Skua.App.Avalonia.Controls;

#if IS_WINDOWS
using WinForms = System.Windows.Forms;
public class WinFormsFlashHost : NativeControlHost
{
    private WinForms.Panel? _panel;
    private WinForms.Control? _child;
    private ResizeHitTestPassthrough? _panelResizeHitTest;
    private ResizeHitTestPassthrough? _childResizeHitTest;

    public void SetChild(WinForms.Control child)
    {
        _childResizeHitTest?.Dispose();
        _child = child;
        _childResizeHitTest = new ResizeHitTestPassthrough(child);
        if (_panel is null)
            return;

        AttachChildToPanel();
    }

    public void SetScaleMode(bool isExactFit = false)
    {
        ApplyScaleMode();
    }

    private void ApplyScaleMode()
    {
        if (_child is AxShockwaveFlashObjects.AxShockwaveFlash flash)
        {
            try
            {
                flash.ScaleMode = 0;
                flash.CtlScale = "ShowAll";
            }
            catch { }
        }
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _childResizeHitTest?.Dispose();
        _childResizeHitTest = _child is not null ? new ResizeHitTestPassthrough(_child) : null;

        _panel = new WinForms.Panel
        {
            Dock = WinForms.DockStyle.Fill,
            BackColor = System.Drawing.Color.FromArgb(20, 20, 20)
        };
        _panelResizeHitTest = new ResizeHitTestPassthrough(_panel);

        _panel.Resize += (s, e) => ApplyScaleMode();

        if (_child is not null)
            AttachChildToPanel();

        return new PlatformHandle(_panel.Handle, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _childResizeHitTest?.Dispose();
        _childResizeHitTest = null;
        _panelResizeHitTest?.Dispose();
        _panelResizeHitTest = null;
        if (_panel is not null)
        {
            _panel.Controls.Clear();
            _panel.Dispose();
            _panel = null;
        }
    }

    private void AttachChildToPanel()
    {
        if (_panel is null || _child is null)
            return;

        if (_child.Parent is not null && _child.Parent != _panel)
            _child.Parent.Controls.Remove(_child);

        _child.Dock = WinForms.DockStyle.Fill;
        _panel.Controls.Clear();
        _panel.Controls.Add(_child);
        ApplyScaleMode();
    }

    /// <summary>
    /// Passes resize-border hit tests through native Flash child HWNDs to the top-level client.
    /// This prevents the embedded ActiveX viewport from consuming edge and corner pointers before Windows can resize VibeSkua.
    /// </summary>
    private sealed class ResizeHitTestPassthrough : IDisposable
    {
        private const uint WmNcHitTest = 0x0084;
        private const uint GaRoot = 2;
        private const int HtTransparent = -1;
        private const int ResizeBorderDip = 12;
        private static readonly nuint SubclassId = 1;

        private delegate IntPtr SubclassProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam, nuint subclassId, IntPtr referenceData);

        [DllImport("comctl32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc callback, nuint subclassId, IntPtr referenceData);

        [DllImport("comctl32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc callback, nuint subclassId);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private readonly WinForms.Control _control;
        private readonly SubclassProc _callback;
        private IntPtr _hookedHandle;

        public ResizeHitTestPassthrough(WinForms.Control control)
        {
            _control = control;
            _callback = WindowSubclassProc;
            _control.HandleCreated += Control_HandleCreated;
            _control.HandleDestroyed += Control_HandleDestroyed;
            if (_control.IsHandleCreated)
                Attach(_control.Handle);
        }

        private void Control_HandleCreated(object? sender, EventArgs e) => Attach(_control.Handle);

        private void Control_HandleDestroyed(object? sender, EventArgs e) => Detach();

        private void Attach(IntPtr handle)
        {
            if (handle == IntPtr.Zero || _hookedHandle == handle)
                return;

            Detach();
            if (SetWindowSubclass(handle, _callback, SubclassId, IntPtr.Zero))
                _hookedHandle = handle;
        }

        private void Detach()
        {
            if (_hookedHandle == IntPtr.Zero)
                return;

            RemoveWindowSubclass(_hookedHandle, _callback, SubclassId);
            _hookedHandle = IntPtr.Zero;
        }

        private IntPtr WindowSubclassProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam, nuint subclassId, IntPtr referenceData)
        {
            if (message == WmNcHitTest && IsResizeBorder(hWnd, lParam))
                return (IntPtr)HtTransparent;

            return DefSubclassProc(hWnd, message, wParam, lParam);
        }

        private static bool IsResizeBorder(IntPtr hWnd, IntPtr lParam)
        {
            IntPtr rootHandle = GetAncestor(hWnd, GaRoot);
            if (rootHandle == IntPtr.Zero || !GetWindowRect(rootHandle, out NativeRect bounds))
                return false;

            long packedPoint = lParam.ToInt64();
            int x = unchecked((short)(packedPoint & 0xFFFF));
            int y = unchecked((short)((packedPoint >> 16) & 0xFFFF));
            uint dpi = GetDpiForWindow(rootHandle);
            double scale = dpi > 0 ? dpi / 96.0 : 1.0;
            int resizeBand = Math.Max(8, (int)Math.Ceiling(ResizeBorderDip * scale));

            if (x < bounds.Left || x >= bounds.Right || y < bounds.Top || y >= bounds.Bottom)
                return false;

            bool leftEdge = x < bounds.Left + resizeBand;
            bool rightEdge = x >= bounds.Right - resizeBand;
            bool topEdge = y < bounds.Top + resizeBand;
            bool bottomEdge = y >= bounds.Bottom - resizeBand;
            return leftEdge || rightEdge || topEdge || bottomEdge;
        }

        public void Dispose()
        {
            _control.HandleCreated -= Control_HandleCreated;
            _control.HandleDestroyed -= Control_HandleDestroyed;
            Detach();
        }
    }
}
#else
public class WinFormsFlashHost : NativeControlHost
{
    
}
#endif
