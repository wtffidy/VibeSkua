using Avalonia.Controls;
using Avalonia.Platform;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Skua.App.Avalonia.Controls;

#if IS_WINDOWS
using WinForms = System.Windows.Forms;
public class WinFormsFlashHost : NativeControlHost
{
    private WinForms.Panel? _panel;
    private WinForms.Control? _child;
    private ResizeHitTestRouter? _resizeHitTestRouter;

    public void SetChild(WinForms.Control child)
    {
        _child = child;
        if (_panel is null)
            return;

        AttachChildToPanel();
        _resizeHitTestRouter?.RefreshChildHooks();
    }

    public void SetScaleMode(bool isExactFit = false) => ApplyScaleMode();

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
        _resizeHitTestRouter?.Dispose();
        _resizeHitTestRouter = null;

        _panel = new WinForms.Panel
        {
            Dock = WinForms.DockStyle.Fill,
            BackColor = System.Drawing.Color.FromArgb(20, 20, 20)
        };

        _resizeHitTestRouter = new ResizeHitTestRouter(_panel);
        _panel.Resize += (s, e) =>
        {
            ApplyScaleMode();
            _resizeHitTestRouter?.RefreshChildHooks();
        };

        if (_child is not null)
            AttachChildToPanel();

        _resizeHitTestRouter.RefreshChildHooks();
        return new PlatformHandle(_panel.Handle, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _resizeHitTestRouter?.Dispose();
        _resizeHitTestRouter = null;

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
    /// Routes native resize hit tests from every descendant of the embedded Flash viewport to the top-level VibeSkua window.
    /// </summary>
    private sealed class ResizeHitTestRouter : IDisposable
    {
        private const uint WmNcHitTest = 0x0084;
        private const uint WmNcLButtonDown = 0x00A1;
        private const uint WmNcDestroy = 0x0082;
        private const uint GaRoot = 2;
        private const int HtLeft = 10;
        private const int HtRight = 11;
        private const int HtTop = 12;
        private const int HtTopLeft = 13;
        private const int HtTopRight = 14;
        private const int HtBottom = 15;
        private const int HtBottomLeft = 16;
        private const int HtBottomRight = 17;
        private const int ResizeBorderDip = 12;
        private static readonly nuint SubclassId = 1;

        private delegate IntPtr SubclassProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam, nuint subclassId, IntPtr referenceData);
        private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

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

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private readonly WinForms.Control _rootControl;
        private readonly HashSet<IntPtr> _hookedHandles = new();
        private readonly SubclassProc _subclassCallback;
        private readonly EnumChildProc _enumChildCallback;
        private readonly WinForms.Timer _refreshTimer;
        private bool _disposed;

        public ResizeHitTestRouter(WinForms.Control rootControl)
        {
            _rootControl = rootControl;
            _subclassCallback = WindowSubclassProc;
            _enumChildCallback = EnumerateChildWindow;
            _refreshTimer = new WinForms.Timer { Interval = 250 };
            _refreshTimer.Tick += RefreshTimer_Tick;

            if (_rootControl.IsHandleCreated)
                Attach(_rootControl.Handle);

            RefreshChildHooks();
            _refreshTimer.Start();
        }

        /// <summary>
        /// Enumerates the complete native child-window tree and hooks windows created after Flash initialization.
        /// </summary>
        public void RefreshChildHooks()
        {
            if (_disposed || !_rootControl.IsHandleCreated)
                return;

            IntPtr rootHandle = _rootControl.Handle;
            Attach(rootHandle);
            EnumChildWindows(rootHandle, _enumChildCallback, IntPtr.Zero);
        }

        private void RefreshTimer_Tick(object? sender, EventArgs e) => RefreshChildHooks();

        private bool EnumerateChildWindow(IntPtr hWnd, IntPtr lParam)
        {
            Attach(hWnd);
            return true;
        }

        private void Attach(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero || !_hookedHandles.Add(hWnd))
                return;

            if (!SetWindowSubclass(hWnd, _subclassCallback, SubclassId, IntPtr.Zero))
                _hookedHandles.Remove(hWnd);
        }

        private IntPtr WindowSubclassProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam, nuint subclassId, IntPtr referenceData)
        {
            if (message == WmNcHitTest && TryGetResizeHitTest(hWnd, lParam, out IntPtr hitTest))
                return hitTest;

            if (message == WmNcLButtonDown && IsResizeHitTest(wParam))
            {
                IntPtr topLevelHandle = GetAncestor(hWnd, GaRoot);
                if (topLevelHandle != IntPtr.Zero)
                {
                    ReleaseCapture();
                    return SendMessage(topLevelHandle, WmNcLButtonDown, wParam, lParam);
                }
            }

            if (message == WmNcDestroy)
                _hookedHandles.Remove(hWnd);

            return DefSubclassProc(hWnd, message, wParam, lParam);
        }

        /// <summary>
        /// Identifies the top-level resize edge beneath the embedded game viewport.
        /// </summary>
        private static bool TryGetResizeHitTest(IntPtr hWnd, IntPtr lParam, out IntPtr hitTest)
        {
            hitTest = IntPtr.Zero;
            IntPtr topLevelHandle = GetAncestor(hWnd, GaRoot);
            if (topLevelHandle == IntPtr.Zero || !GetWindowRect(topLevelHandle, out NativeRect bounds))
                return false;

            long packedPoint = lParam.ToInt64();
            int x = unchecked((short)(packedPoint & 0xFFFF));
            int y = unchecked((short)((packedPoint >> 16) & 0xFFFF));
            uint dpi = GetDpiForWindow(topLevelHandle);
            double scale = dpi > 0 ? dpi / 96.0 : 1.0;
            int resizeBand = Math.Max(8, (int)Math.Ceiling(ResizeBorderDip * scale));

            if (x < bounds.Left || x >= bounds.Right || y < bounds.Top || y >= bounds.Bottom)
                return false;

            bool left = x < bounds.Left + resizeBand;
            bool right = x >= bounds.Right - resizeBand;
            bool top = y < bounds.Top + resizeBand;
            bool bottom = y >= bounds.Bottom - resizeBand;

            if (top && left)
                hitTest = (IntPtr)HtTopLeft;
            else if (top && right)
                hitTest = (IntPtr)HtTopRight;
            else if (bottom && left)
                hitTest = (IntPtr)HtBottomLeft;
            else if (bottom && right)
                hitTest = (IntPtr)HtBottomRight;
            else if (left)
                hitTest = (IntPtr)HtLeft;
            else if (right)
                hitTest = (IntPtr)HtRight;
            else if (top)
                hitTest = (IntPtr)HtTop;
            else if (bottom)
                hitTest = (IntPtr)HtBottom;
            else
                return false;

            return true;
        }

        private static bool IsResizeHitTest(IntPtr hitTest)
        {
            int value = hitTest.ToInt32();
            return value is HtLeft or HtRight or HtTop or HtTopLeft or HtTopRight or HtBottom or HtBottomLeft or HtBottomRight;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _refreshTimer.Stop();
            _refreshTimer.Tick -= RefreshTimer_Tick;
            _refreshTimer.Dispose();

            IntPtr[] handles = new IntPtr[_hookedHandles.Count];
            _hookedHandles.CopyTo(handles);
            foreach (IntPtr handle in handles)
                RemoveWindowSubclass(handle, _subclassCallback, SubclassId);

            _hookedHandles.Clear();
        }
    }
}
#else
public class WinFormsFlashHost : NativeControlHost
{
}
#endif
