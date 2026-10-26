using Avalonia.Controls;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Messaging;
using Skua.Core.Interfaces;
using Skua.Shared.Avalonia.Services;
using Avalonia.Threading;
using Skua.Manager.Avalonia.ViewModels;
using System;
using System.Runtime.InteropServices;

namespace Skua.Manager.Avalonia.Views;

public partial class MainWindow : Window
{
    private readonly ManagerMainViewModel _viewModel;
    private readonly AvaloniaThemeService? _themeService;
    private bool _allowClose;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = Ioc.Default.GetRequiredService<ManagerMainViewModel>();
        DataContext = _viewModel;

        _themeService = Ioc.Default.GetService<IThemeService>() as AvaloniaThemeService;
        if (_themeService != null)
        {
            TitleBar.UseAccentBackground = _themeService.UseAccentOnMainTitleBar;
            _themeService.ThemeChanged += OnThemeChanged;
        }

        StrongReferenceMessenger.Default.Register<MainWindow, ShowMainWindowMessage>(this, ShowManager);
        StrongReferenceMessenger.Default.Register<MainWindow, ExitManagerMessage>(this, AllowClose);

        Opened += (s, e) =>
        {
            try
            {
                Skua.Shared.Avalonia.Services.ThemeResourceApplicator.ApplyTitleBarTheme(this);
            }
            catch { }
        };

        Activated += (s, e) =>
        {
            try
            {
                var accVm = Ioc.Default.GetService<AccountManagerViewModel>();
                accVm?.ScheduleReload();
            }
            catch { }
        };
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            _viewModel.IsActive = false;
            return;
        }

        _viewModel.IsActive = false;
        if (_themeService != null)
            _themeService.ThemeChanged -= OnThemeChanged;
        StrongReferenceMessenger.Default.UnregisterAll(this);
        base.OnClosing(e);
    }

    private void OnThemeChanged(object? theme)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_themeService != null)
                TitleBar.UseAccentBackground = _themeService.UseAccentOnMainTitleBar;
        });
    }

    private void ShowManager(MainWindow recipient, ShowMainWindowMessage message)
    {
        if (!recipient.IsVisible)
        {
            recipient.Show();
        }

        if (recipient.WindowState == WindowState.Minimized)
            recipient.WindowState = WindowState.Normal;

        recipient.Activate();
        recipient.Topmost = true;
        recipient.Topmost = false;
        recipient.Focus();

        if (OperatingSystem.IsWindows() && recipient.TryGetPlatformHandle() is { Handle: var handle } && handle != IntPtr.Zero)
        {
            ShowWindow(handle, 5); // SW_SHOW: un-minimizing is done above; SW_RESTORE would also un-maximize
            SetForegroundWindow(handle);
        }
    }

    private void AllowClose(MainWindow recipient, ExitManagerMessage message)
    {
        recipient._allowClose = true;
    }
}
