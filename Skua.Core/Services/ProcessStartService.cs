using Skua.Core.Interfaces;
using Skua.Core.Models;
using System.Diagnostics;

namespace Skua.Core.Services;

public class ProcessStartService : IProcessService
{
    public ProcessStartService(ISettingsService settingsService, IDialogService dialogService)
    {
        _settingsService = settingsService;
        _dialogService = dialogService;
    }

    private readonly string _vscPath = Path.Combine(AppContext.BaseDirectory, "VSCode", "code");
    private readonly string _scriptsPath = ClientFileSources.SkuaScriptsDIR;
    private readonly ISettingsService _settingsService;
    private readonly IDialogService _dialogService;

    public void OpenLink(string link)
    {
        ProcessStartInfo startInfo = new(link)
        {
            UseShellExecute = true
        };
        Process.Start(startInfo);
    }

    public void OpenVSC()
    {
        if (_settingsService.Get("UseLocalVSC", false))
        {
            Process.Start(_vscPath, _scriptsPath);
            return;
        }

        try
        {
            VSCode(string.Empty);
        }
        catch
        {
            _dialogService.ShowMessageBox("VSCode was not found. If you have VSCode, reinstall it and be sure to check it to install in you PATH folder.", "VSCode not found");
        }
    }

    public void OpenVSC(string path)
    {
        if (_settingsService.Get("UseLocalVSC", false))
        {
            Process.Start(_vscPath, new[] { _scriptsPath, path, "--reuse-window" });
            return;
        }

        try
        {
            VSCode(path);
        }
        catch
        {
            OpenInTextEditor(path);
        }
    }

    private void VSCode(string path)
    {
        ProcessStartInfo startInfo = new("code")
        {
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(_scriptsPath);
        if (!string.IsNullOrWhiteSpace(path))
            startInfo.ArgumentList.Add(path);
        Process.Start(startInfo);
    }

    private static void OpenInTextEditor(string path)
    {
        ProcessStartInfo startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("notepad")
            : OperatingSystem.IsMacOS()
                ? new ProcessStartInfo("open")
                : new ProcessStartInfo("xdg-open");

        if (OperatingSystem.IsMacOS())
            startInfo.ArgumentList.Add("-t");

        startInfo.ArgumentList.Add(path);
        startInfo.UseShellExecute = false;
        Process.Start(startInfo);
    }
}
