using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using AroniumBridge.Models;
using AroniumBridge.Services;
using AroniumBridge.UI;

using FontStyle = System.Drawing.FontStyle;

// ──────────────────────────────────────────────────────────────────────────────
//  App.xaml.cs  —  Application entry point and orchestrator.
//
//  Responsibilities:
//    • Single-instance mutex guard
//    • appsettings.json load / persist
//    • System tray icon + context menu
//    • HardwareService + VirtualPortService lifecycle
//    • Routes startup: valid settings → start services, else → Settings UI
// ──────────────────────────────────────────────────────────────────────────────

namespace AroniumBridge;

public partial class App : System.Windows.Application
{
    private System.Threading.Mutex? _mutex;

    private NotifyIcon?        _trayIcon;
    private ToolStripMenuItem? _statusItem;

    private HardwareService?    _hardware;
    private VirtualPortService? _virtualPort;

    private AppSettings             _settings    = new();
    private SettingsWindow?         _settingsWin;
    private DisplaySimulatorWindow? _simulatorWin;

    // ── Well-known paths ──────────────────────────────────────────────────────

    internal static string AppDataFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AroniumBridge");

    internal static string SettingsFile { get; } =
        Path.Combine(AppDataFolder, "appsettings.json");

    private static readonly JsonSerializerOptions _jsonOptions =
        new() { WriteIndented = true };

    // ══════════════════════════════════════════════════════════════════════════
    //  STARTUP
    // ══════════════════════════════════════════════════════════════════════════

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new System.Threading.Mutex(
            initiallyOwned: true,
            name: "Global\\AroniumBridge_SingleInstance",
            createdNew: out bool isNewInstance);

        if (!isNewInstance)
        {
            System.Windows.MessageBox.Show(
                "Aronium Bridge is already running.\n\nLook for its icon in the system tray.",
                "Already Running",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Directory.CreateDirectory(AppDataFolder);
        BuildTrayIcon();

        _settings = LoadSettings();

        // ── Comprehensive Diagnostics ───────────────────────────────────────
        var diagResults = Com0ComService.PerformComprehensiveDiagnostic(_settings.VirtualComPort);
        diagResults.Add(HardwareService.VerifyHardwarePort(_settings.ComPort));

        var hasErrors = diagResults.Any(r => r.Status == DiagnosticStatus.Error);
        
        if (hasErrors || !Com0ComService.IsInstalled)
        {
            var diagWin = new DiagnosticDialog(diagResults, _settings);
            if (diagWin.ShowDialog() != true)
            {
                // If user cancels or diagnostic fails, just open normal settings
                OpenSettingsWindow(firstRun: !_settings.IsValid());
                return;
            }
        }

        WriteStartupDiagnostic(_settings);

        if (_settings.IsValid())
            StartServices(_settings);
        else
            OpenSettingsWindow(firstRun: true);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  SETTINGS  I/O
    // ══════════════════════════════════════════════════════════════════════════

    private static AppSettings LoadSettings()
    {
        if (!File.Exists(SettingsFile)) return new();
        try
        {
            var json = File.ReadAllText(SettingsFile);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[App] Failed to load settings: {ex.Message}");
            return new();
        }
    }

    internal static void PersistSettings(AppSettings settings)
    {
        Directory.CreateDirectory(AppDataFolder);
        File.WriteAllText(SettingsFile,
            JsonSerializer.Serialize(settings, _jsonOptions));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  DIAGNOSTIC
    // ══════════════════════════════════════════════════════════════════════════

    private static void WriteStartupDiagnostic(AppSettings s)
    {
        try
        {
            var lines = new[]
            {
                $"=== AroniumBridge Startup  {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===",
                $"VirtualComPort : {(string.IsNullOrEmpty(s.VirtualComPort) ? "(not set)" : s.VirtualComPort)}",
                $"ComPort        : {s.ComPort}",
                $"BaudRate       : {s.BaudRate}",
                $"IsValid        : {s.IsValid()}",
                "",
                "Available COM ports: " + string.Join(", ",
                    System.IO.Ports.SerialPort.GetPortNames().OrderBy(p => p)),
            };
            File.WriteAllLines(Path.Combine(AppDataFolder, "startup_diag.txt"), lines);
        }
        catch { /* non-fatal */ }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  TRAY ICON
    // ══════════════════════════════════════════════════════════════════════════

    private void BuildTrayIcon()
    {
        _trayIcon = new NotifyIcon
        {
            Icon            = CreateProgrammaticIcon(),
            Text            = "Aronium Bridge",
            Visible         = true,
            BalloonTipTitle = "Aronium Bridge",
            BalloonTipIcon  = ToolTipIcon.Info,
        };

        var menu = new ContextMenuStrip
        {
            Renderer = new ToolStripProfessionalRenderer(new ModernMenuColors()),
            Font     = new Font("Segoe UI", 9.5f),
        };

        _statusItem = new ToolStripMenuItem("● Disconnected")
        {
            Enabled   = false,
            ForeColor = System.Drawing.Color.FromArgb(0xD3, 0x2F, 0x2F),
        };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(new ToolStripMenuItem(
            "⚙  Settings", null, (_, _) => OpenSettingsWindow())
        { Font = new Font(menu.Font, FontStyle.Regular) });

        menu.Items.Add(new ToolStripMenuItem(
            "🖥  Open Display Simulator", null, (_, _) => OpenSimulatorWindow())
        { Font = new Font(menu.Font, FontStyle.Regular) });

        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(new ToolStripMenuItem(
            "✕  Exit", null, (_, _) => ExitApplication()));

        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick     += (_, _) => OpenSettingsWindow();
    }

    private void SetTrayStatus(bool connected, string label)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (connected)
            {
                _statusItem!.Text      = $"● Connected  →  {label}";
                _statusItem.ForeColor  = System.Drawing.Color.FromArgb(0x2E, 0x7D, 0x32);
                _trayIcon!.Text        = $"Aronium Bridge  [{label}]";
            }
            else
            {
                _statusItem!.Text      = "● Disconnected";
                _statusItem.ForeColor  = System.Drawing.Color.FromArgb(0xD3, 0x2F, 0x2F);
                _trayIcon!.Text        = "Aronium Bridge — Disconnected";
            }
        }, DispatcherPriority.Normal);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  SERVICE LIFECYCLE
    // ══════════════════════════════════════════════════════════════════════════

    private void StartServices(AppSettings cfg)
    {
        StopServices();

        _hardware = new HardwareService();
        _hardware.ConnectionStateChanged += SetTrayStatus;
        _hardware.Connect(cfg);

        _virtualPort = new VirtualPortService(_hardware);
        _virtualPort.ConnectionStateChanged += (connected, port) =>
            SetTrayStatus(connected, $"{port}→{cfg.ComPort}");
        _virtualPort.Start(cfg);
    }

    private void StopServices()
    {
        _virtualPort?.Stop();
        _virtualPort?.Dispose();
        _virtualPort = null;

        _hardware?.Disconnect();
        _hardware?.Dispose();
        _hardware = null;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  SETTINGS WINDOW
    // ══════════════════════════════════════════════════════════════════════════

    internal void OpenSettingsWindow(bool firstRun = false)
    {
        if (_settingsWin is { IsVisible: true })
        {
            _settingsWin.Activate();
            _settingsWin.WindowState = System.Windows.WindowState.Normal;
            return;
        }

        _settingsWin = new SettingsWindow(_settings, firstRun);

        _settingsWin.SettingsSaved += newCfg =>
        {
            _settings = newCfg;
            PersistSettings(newCfg);
            StartServices(newCfg);

            _trayIcon?.ShowBalloonTip(
                timeout:  3000,
                tipTitle: "Service Started",
                tipText:  newCfg.HardwareSummary,
                tipIcon:  ToolTipIcon.Info);
        };

        _settingsWin.Show();
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  SIMULATOR WINDOW
    // ══════════════════════════════════════════════════════════════════════════

    private void OpenSimulatorWindow()
    {
        if (_simulatorWin is { IsVisible: true })
        {
            _simulatorWin.Activate();
            _simulatorWin.WindowState = System.Windows.WindowState.Normal;
            return;
        }
        _simulatorWin = new DisplaySimulatorWindow();
        _simulatorWin.Show();
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  EXIT
    // ══════════════════════════════════════════════════════════════════════════

    private void ExitApplication()
    {
        StopServices();
        _trayIcon?.Dispose();
        try { _mutex?.ReleaseMutex(); _mutex?.Dispose(); } catch { }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        StopServices();
        _trayIcon?.Dispose();
        base.OnExit(e);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  PROGRAMMATIC TRAY ICON
    // ══════════════════════════════════════════════════════════════════════════

    private static System.Drawing.Icon CreateProgrammaticIcon()
    {
        const int S = 32;
        using var bmp = new Bitmap(S, S, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g   = Graphics.FromImage(bmp);
        g.SmoothingMode      = SmoothingMode.AntiAlias;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.InterpolationMode  = InterpolationMode.HighQualityBicubic;
        g.Clear(System.Drawing.Color.Transparent);

        using var bgBrush = new SolidBrush(System.Drawing.Color.FromArgb(0x1E, 0x88, 0xE5));
        using var path    = RoundedRect(new RectangleF(1, 1, S - 2, S - 2), 6f);
        g.FillPath(bgBrush, path);

        using var font = new Font("Segoe UI", 17f, FontStyle.Bold, GraphicsUnit.Pixel);
        var ts = g.MeasureString("P", font);
        g.DrawString("P", font, Brushes.White, (S - ts.Width) / 2f - 1f, (S - ts.Height) / 2f);

        nint hIcon = bmp.GetHicon();
        var  icon  = (System.Drawing.Icon)System.Drawing.Icon.FromHandle(hIcon).Clone();
        DestroyIcon(hIcon);
        return icon;
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = radius * 2f;
        var p = new GraphicsPath();
        p.AddArc(r.X,         r.Y,          d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y,          d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d,   0, 90);
        p.AddArc(r.X,         r.Bottom - d, d, d,  90, 90);
        p.CloseFigure();
        return p;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint hIcon);

    private sealed class ModernMenuColors : ProfessionalColorTable
    {
        public override System.Drawing.Color MenuItemSelectedGradientBegin =>
            System.Drawing.Color.FromArgb(0xEB, 0xF3, 0xFC);
        public override System.Drawing.Color MenuItemSelectedGradientEnd =>
            System.Drawing.Color.FromArgb(0xEB, 0xF3, 0xFC);
        public override System.Drawing.Color MenuItemSelected =>
            System.Drawing.Color.FromArgb(0xEB, 0xF3, 0xFC);
        public override System.Drawing.Color MenuBorder =>
            System.Drawing.Color.FromArgb(0xDD, 0xE3, 0xED);
        public override System.Drawing.Color ToolStripDropDownBackground =>
            System.Drawing.Color.White;
        public override System.Drawing.Color ImageMarginGradientBegin  => System.Drawing.Color.White;
        public override System.Drawing.Color ImageMarginGradientMiddle => System.Drawing.Color.White;
        public override System.Drawing.Color ImageMarginGradientEnd    => System.Drawing.Color.White;
        public override System.Drawing.Color SeparatorDark =>
            System.Drawing.Color.FromArgb(0xDD, 0xE3, 0xED);
        public override System.Drawing.Color SeparatorLight =>
            System.Drawing.Color.FromArgb(0xDD, 0xE3, 0xED);
    }
}
