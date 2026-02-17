using System.ComponentModel;
using System.Drawing;
using System.IO;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media.Imaging;
using SwitchDcrpc.Wpf.Services;
using SwitchDcrpc.Wpf.ViewModels;

namespace SwitchDcrpc.Wpf;

public partial class MainWindow : Window
{
    private const string AutoStartRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AutoStartValueName = "RichNX";

    private readonly NotifyIcon _trayIcon;
    private readonly ClientConfigStore _configStore = new();
    private Icon? _trayAppIcon;
    private bool _exitRequested;
    private bool _shutdownDone;

    public MainWindow()
    {
        InitializeComponent();
        TrySetWindowIcon();
        DataContext = new MainViewModel();
        _trayIcon = BuildTrayIcon();
        StateChanged += OnStateChanged;
        Closing += OnClosing;
        _ = SyncAutostartFromConfigAsync();
    }

    private void TrySetWindowIcon()
    {
        try
        {
            Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Nintendo-switch-icon.ico"));
        }
        catch
        {
            // Ignore icon load errors to prevent startup crashes.
        }
    }

    private NotifyIcon BuildTrayIcon()
    {
        Icon? appIcon = null;

        try
        {
            var localIcoPath = Path.Combine(AppContext.BaseDirectory, "Nintendo-switch-icon.ico");
            if (File.Exists(localIcoPath))
            {
                appIcon = new Icon(localIcoPath);
            }
        }
        catch
        {
            // Ignore icon file load errors.
        }

        if (appIcon is null)
        {
            try
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    appIcon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                }
            }
            catch
            {
                // Ignore executable icon extraction errors.
            }
        }

        appIcon ??= SystemIcons.Application;
        _trayAppIcon = appIcon;

        var icon = new NotifyIcon
        {
            Text = "RichNX",
            Icon = appIcon,
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip()
        };

        icon.ContextMenuStrip.Items.Add("Open", null, (_, _) => RestoreFromTray());
        icon.ContextMenuStrip.Items.Add("Exit", null, async (_, _) => await ExitApplicationAsync());
        icon.DoubleClick += (_, _) => RestoreFromTray();

        return icon;
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            Hide();
        }
    }

    private void RestoreFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_exitRequested)
        {
            e.Cancel = true;
            Hide();
            ShowInTaskbar = false;
            ShowBackgroundRunningToast();
            return;
        }

        if (!_shutdownDone && DataContext is MainViewModel vm)
        {
            _shutdownDone = true;
            await vm.ShutdownAsync();
        }
    }

    private async Task ExitApplicationAsync()
    {
        _exitRequested = true;
        if (!_shutdownDone && DataContext is MainViewModel vm)
        {
            _shutdownDone = true;
            await vm.ShutdownAsync();
        }

        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        Close();
    }

    private void ShowBackgroundRunningToast()
    {
        try
        {
            _trayIcon.BalloonTipIcon = ToolTipIcon.Info;
            _trayIcon.BalloonTipTitle = "RichNX is still running";
            _trayIcon.BalloonTipText = "The app is running in the background. Use the tray icon to reopen or exit.";
            _trayIcon.ShowBalloonTip(4000);
        }
        catch
        {
            // Ignore notification errors.
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayAppIcon?.Dispose();
        base.OnClosed(e);
    }

    private async void MenuSettings_Click(object sender, RoutedEventArgs e)
    {
        var cfg = await _configStore.LoadAsync(CancellationToken.None);
        var dialog = new SettingsWindow(cfg.StartWithWindows, cfg.ConnectOnStartup, cfg.ShowGithubButton, cfg.ShowBatteryStatus)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        cfg.StartWithWindows = dialog.StartWithWindows;
        cfg.ConnectOnStartup = dialog.ConnectOnStartup;
        cfg.ShowGithubButton = dialog.ShowGithubButton;
        cfg.ShowBatteryStatus = dialog.ShowBatteryStatus;

        if (DataContext is MainViewModel vm)
        {
            vm.StartWithWindows = cfg.StartWithWindows;
            vm.ConnectOnStartup = cfg.ConnectOnStartup;
            vm.ShowGithubButton = cfg.ShowGithubButton;
            vm.ShowBatteryStatus = cfg.ShowBatteryStatus;
            cfg.SwitchIp = vm.SwitchIp;
            cfg.Port = vm.Port;
            cfg.PollIntervalMs = vm.PollIntervalMs;
            cfg.RpcName = vm.RpcName;
            cfg.TitleDbPack = vm.SelectedTitleDbPack;
        }

        await _configStore.SaveAsync(cfg, CancellationToken.None);

        try
        {
            SetAutostart(cfg.StartWithWindows);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                this,
                $"Could not update Windows autostart:\n{ex.Message}",
                "Settings",
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
        }
    }

    private void MenuAbout_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AboutWindow
        {
            Owner = this
        };
        dialog.ShowDialog();
    }

    private async Task SyncAutostartFromConfigAsync()
    {
        try
        {
            var cfg = await _configStore.LoadAsync(CancellationToken.None);
            if (DataContext is MainViewModel vm)
            {
                vm.StartWithWindows = cfg.StartWithWindows;
                vm.ConnectOnStartup = cfg.ConnectOnStartup;
                vm.ShowGithubButton = cfg.ShowGithubButton;
                vm.ShowBatteryStatus = cfg.ShowBatteryStatus;
            }

            SetAutostart(cfg.StartWithWindows);
        }
        catch
        {
            // Ignore startup settings sync errors.
        }
    }

    private static void SetAutostart(bool enabled)
    {
        using var runKey = Registry.CurrentUser.OpenSubKey(AutoStartRunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(AutoStartRunKeyPath, writable: true);
        if (runKey is null)
        {
            return;
        }

        if (!enabled)
        {
            runKey.DeleteValue(AutoStartValueName, throwOnMissingValue: false);
            return;
        }

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return;
        }

        runKey.SetValue(AutoStartValueName, $"\"{exePath}\"");
    }
}
