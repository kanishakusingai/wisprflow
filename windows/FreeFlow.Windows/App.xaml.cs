using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using FreeFlow.Windows.Services;
using FreeFlow.Windows.UI;
using Forms = System.Windows.Forms;

namespace FreeFlow.Windows;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private AppSettings _settings = new();
    private FlowBarWindow? _flowBar;
    private DictationController? _controller;
    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _recentMenu;
    private SettingsWindow? _settingsWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, @"Local\FreeFlow.Windows.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("FreeFlow is already running. Look for its icon in the system tray (near the clock).",
                "FreeFlow", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            // Keep the tray app alive on unexpected UI errors, but record them.
            ErrorLog.Write("ui", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) ErrorLog.Write("fatal", ex);
        };

        _settings = SettingsStore.Load();

        _flowBar = new FlowBarWindow();
        new WindowInteropHelper(_flowBar).EnsureHandle();

        _controller = new DictationController(_settings, _flowBar, Dispatcher);
        _controller.SettingsNeeded += OpenSettings;
        _controller.History.Changed += RebuildRecentMenu;

        try
        {
            _controller.Start();
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, "FreeFlow", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        CreateTrayIcon();

        var startedInBackground = e.Args.Contains("--background");
        if (!_settings.HasApiKey || !_settings.HasCompletedSetup)
        {
            OpenSettings();
        }
        else if (!startedInBackground)
        {
            _tray?.ShowBalloonTip(4000, "FreeFlow is ready",
                $"Hold {_controller.HotkeyName} in any app and start talking.", Forms.ToolTipIcon.None);
        }
    }

    private void CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Settings…", null, (_, _) => OpenSettings());
        menu.Items.Add("Copy last dictation", null, (_, _) =>
        {
            var last = _controller?.History.Latest;
            if (last != null) Clipboard.SetText(last);
        });
        _recentMenu = new Forms.ToolStripMenuItem("Recent dictations") { Enabled = false };
        menu.Items.Add(_recentMenu);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit FreeFlow", null, (_, _) => Quit());

        _tray = new Forms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "FreeFlow: voice dictation",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => OpenSettings();
    }

    private static Icon LoadAppIcon()
    {
        var resource = GetResourceStream(new Uri("pack://application:,,,/Assets/FreeFlow.ico"));
        if (resource != null)
        {
            using var stream = resource.Stream;
            return new Icon(stream, Forms.SystemInformation.SmallIconSize);
        }
        return SystemIcons.Application;
    }

    private void RebuildRecentMenu()
    {
        if (_recentMenu == null || _controller == null) return;
        _recentMenu.DropDownItems.Clear();
        foreach (var item in _controller.History.Items)
        {
            var text = item;
            var label = text.Replace('\n', ' ');
            if (label.Length > 60) label = label[..57] + "…";
            _recentMenu.DropDownItems.Add(label, null, (_, _) => Clipboard.SetText(text));
        }
        _recentMenu.Enabled = _recentMenu.DropDownItems.Count > 0;
    }

    private void OpenSettings()
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_settings);
        _settingsWindow.SetStatus(
            _settings.HasApiKey ? $"Ready: hold {_controller?.HotkeyName} to dictate" : "Add an API key to get started",
            _settings.HasApiKey);
        _settingsWindow.Saved += () =>
        {
            _controller?.ApplySettings();
            _settingsWindow?.SetStatus(
                _settings.HasApiKey ? $"Ready: {(_settings.Mode == RecordingMode.Hold ? "hold" : "tap")} {_controller?.HotkeyName} to dictate" : "Add an API key to get started",
                _settings.HasApiKey);
        };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void Quit()
    {
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _controller?.Dispose();
        _singleInstance?.ReleaseMutex();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _controller?.Dispose();
        base.OnExit(e);
    }
}
