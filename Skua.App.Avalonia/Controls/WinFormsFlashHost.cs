using Avalonia.Controls;
using Avalonia.Platform;
using System;
using System.Runtime.InteropServices;

namespace Skua.App.Avalonia.Controls;

#if IS_WINDOWS
using WinForms = System.Windows.Forms;
public class WinFormsFlashHost : NativeControlHost
{
    private const int NativeCornerRadiusDip = 12;
    private const int RegionCombineOr = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int CombineRgn(IntPtr destination, IntPtr source1, IntPtr source2, int mode);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    private WinForms.Panel? _panel;
    private WinForms.Control? _child;

    public void SetChild(WinForms.Control child)
    {
        _child = child;
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
                // NoBorder fills the viewport without distorting the SWF's aspect ratio.
                flash.ScaleMode = 1;
                flash.CtlScale = "NoBorder";
            }
            catch { }
        }
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _panel = new WinForms.Panel
        {
            Dock = WinForms.DockStyle.Fill,
            BackColor = System.Drawing.Color.FromArgb(20, 20, 20)
        };

        _panel.Resize += (s, e) =>
        {
            ApplyScaleMode();
            ApplyNativeCornerRegions();
        };

        if (_child is not null)
            AttachChildToPanel();

        ApplyNativeCornerRegions();
        return new PlatformHandle(_panel.Handle, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
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
        _child.Resize -= Child_Resize;
        _child.HandleCreated -= Child_HandleCreated;
        _child.Resize += Child_Resize;
        _child.HandleCreated += Child_HandleCreated;

        _panel.Controls.Add(_child);
        ApplyScaleMode();
        ApplyNativeCornerRegions();
    }

    private void Child_Resize(object? sender, EventArgs e) => ApplyNativeCornerRegions();

    private void Child_HandleCreated(object? sender, EventArgs e) => ApplyNativeCornerRegions();

    /// <summary>
    /// Clips the native panel and Flash control to the same square-top, rounded-bottom shape as the backing frame.
    /// </summary>
    private void ApplyNativeCornerRegions()
    {
        ApplyBottomCornerRegion(_panel);
        ApplyBottomCornerRegion(_child);
    }

    /// <summary>
    /// Applies a native window region so the embedded HWND cannot paint its square corners over the rounded frame.
    /// </summary>
    private static void ApplyBottomCornerRegion(WinForms.Control? control)
    {
        if (control is null || !control.IsHandleCreated)
            return;

        IntPtr handle = control.Handle;
        if (!GetWindowRect(handle, out NativeRect bounds))
            return;

        int width = bounds.Right - bounds.Left;
        int height = bounds.Bottom - bounds.Top;
        if (width <= 0 || height <= 0)
            return;

        uint dpi = GetDpiForWindow(handle);
        double scale = dpi > 0 ? dpi / 96.0 : 1.0;
        int radius = Math.Min((int)Math.Ceiling(NativeCornerRadiusDip * scale), Math.Min(width / 2, height / 2));
        if (radius <= 0)
            return;

        IntPtr roundedRegion = CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);
        if (roundedRegion == IntPtr.Zero)
            return;

        IntPtr squareTopRegion = CreateRectRgn(0, 0, width + 1, Math.Max(1, height - radius + 1));
        if (squareTopRegion == IntPtr.Zero)
        {
            DeleteObject(roundedRegion);
            return;
        }

        int combineResult = CombineRgn(roundedRegion, roundedRegion, squareTopRegion, RegionCombineOr);
        DeleteObject(squareTopRegion);

        if (combineResult == 0 || SetWindowRgn(handle, roundedRegion, true) == 0)
            DeleteObject(roundedRegion);
    }
}
#else
public class WinFormsFlashHost : NativeControlHost
{
}
#endif
