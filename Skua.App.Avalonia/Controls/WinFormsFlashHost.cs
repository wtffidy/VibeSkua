using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
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
                flash.ScaleMode = 0;
                flash.CtlScale = "ShowAll";
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

        _panel.Resize += (s, e) => ApplyScaleMode();

        if (_child is not null)
            AttachChildToPanel();

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
        _panel.Controls.Add(_child);
        ApplyScaleMode();
    }
}
#else
public class WinFormsFlashHost : UserControl
{
    private readonly Image _image = new()
    {
        Stretch = Stretch.Uniform
    };

    private WriteableBitmap? _bitmap;

    public WinFormsFlashHost()
    {
        Content = _image;
    }

    public void SetFrame(byte[] rgba, uint width, uint height)
    {
        if (rgba.Length != checked((int)(width * height * 4)))
            return;

        PixelSize pixelSize = new((int)width, (int)height);
        if (_bitmap is null || _bitmap.PixelSize != pixelSize)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(
                pixelSize,
                new Vector(96, 96),
                PixelFormat.Rgba8888,
                AlphaFormat.Unpremul);
            _image.Source = _bitmap;
        }

        using ILockedFramebuffer framebuffer = _bitmap.Lock();
        int sourceStride = checked((int)width * 4);
        if (framebuffer.RowBytes < sourceStride)
            return;

        int heightPixels = (int)height;
        for (int y = 0; y < heightPixels; y++)
        {
            IntPtr destination = IntPtr.Add(framebuffer.Address, y * framebuffer.RowBytes);
            Marshal.Copy(rgba, y * sourceStride, destination, sourceStride);
        }
    }

    public void SetScaleMode(bool isExactFit = false)
    {
    }
}
#endif
