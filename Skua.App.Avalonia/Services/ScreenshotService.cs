using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Skua.Core.Interfaces;
using System;
using System.IO;
using System.Threading.Tasks;

#if IS_WINDOWS
using System.Drawing;
using System.Drawing.Imaging;
#endif

namespace Skua.App.Avalonia.Services;

public class ScreenshotService : IScreenshotService
{
    public async Task<byte[]> TakeScreenshotAsync()
    {
#if IS_WINDOWS
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            global::Avalonia.Controls.Window? mainWindow = null;
            bool wasMinimized = false;

            try
            {
                if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop || desktop.MainWindow is null)
                    return Array.Empty<byte>();

                mainWindow = desktop.MainWindow;
                wasMinimized = mainWindow.WindowState == global::Avalonia.Controls.WindowState.Minimized;
                if (wasMinimized)
                {
                    mainWindow.WindowState = global::Avalonia.Controls.WindowState.Normal;
                    await Task.Delay(200);
                }

                var point = mainWindow.PointToScreen(new global::Avalonia.Point(0, 0));
                double scaling = mainWindow.RenderScaling;
                int width = (int)Math.Max(1, mainWindow.Bounds.Width * scaling);
                int height = (int)Math.Max(1, mainWindow.Bounds.Height * scaling);

                using var bmp = new Bitmap(width, height);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(point.X, point.Y, 0, 0, bmp.Size, CopyPixelOperation.SourceCopy);
                }

                using var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
            catch
            {
                return Array.Empty<byte>();
            }
            finally
            {
                if (wasMinimized && mainWindow is not null)
                {
                    try
                    {
                        mainWindow.WindowState = global::Avalonia.Controls.WindowState.Minimized;
                    }
                    catch
                    {
                    }
                }
            }
        });
#else
        return Array.Empty<byte>();
#endif
    }
}
