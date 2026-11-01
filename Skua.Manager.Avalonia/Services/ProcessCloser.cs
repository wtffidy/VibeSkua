using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Skua.Manager.Avalonia.Services;

/// <summary>
/// Stops client processes by asking them to close first, so they run their own shutdown (stopping
/// scripts, removing the tray icon). A killed process leaves its tray icon behind as a ghost.
/// </summary>
internal static class ProcessCloser
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private const uint WM_CLOSE = 0x0010;
    private const uint GW_OWNER = 4;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// Requests a graceful close using the platform's available main-window mechanism.
    /// Returns false when the process has no closable main window.
    /// </summary>
    public static bool RequestClose(Process process)
    {
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            try
            {
                ProcessStartInfo signalInfo = new("/bin/kill")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                signalInfo.ArgumentList.Add("-TERM");
                signalInfo.ArgumentList.Add(process.Id.ToString());
                using Process? signalProcess = Process.Start(signalInfo);
                signalProcess?.WaitForExit();
                return signalProcess?.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                return process.CloseMainWindow();
            }
            catch
            {
                return false;
            }
        }

        uint pid = (uint)process.Id;
        bool posted = false;
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out uint windowPid);
            if (windowPid == pid
                && IsWindowVisible(hWnd)
                && GetWindow(hWnd, GW_OWNER) == IntPtr.Zero
                && GetWindowTextLength(hWnd) > 0)
            {
                posted |= PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
            return true;
        }, IntPtr.Zero);
        return posted;
    }

    /// <summary>
    /// Asks the process to close and waits up to <paramref name="timeout"/>; kills it if it is still running.
    /// </summary>
    public static async Task CloseAsync(Process process, TimeSpan timeout)
    {
        if (process.HasExited)
            return;

        if (RequestClose(process))
        {
            using CancellationTokenSource cts = new(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
                return;
            }
            catch (OperationCanceledException)
            {
                Trace.WriteLine($"Process {process.Id} did not close within {timeout.TotalSeconds:0.#}s, killing it.");
            }
        }

        if (!process.HasExited)
        {
            process.Kill();
            await process.WaitForExitAsync();
        }
    }
}
