using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using Brush  = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Color  = System.Windows.Media.Color;

using AroniumBridge.Models;
using AroniumBridge.Services;

namespace AroniumBridge.UI;

public partial class SettingsWindow : Window
{
    public event Action<AppSettings>? SettingsSaved;

    private readonly AppSettings _initial;
    private readonly bool        _firstRun;

    // ══════════════════════════════════════════════════════════════════════════
    //  CONSTRUCTOR
    // ══════════════════════════════════════════════════════════════════════════

    public SettingsWindow(AppSettings current, bool firstRun = false)
    {
        InitializeComponent();

        Icon = WindowIcons.Settings();

        _initial  = current;
        _firstRun = firstRun;

        if (firstRun)
        {
            TxtWindowTitle.Text    = "Welcome to Aronium Bridge";
            TxtWindowSubtitle.Text = "Set up your virtual COM ports and pole display.";
            Title = "Aronium Bridge — First-Run Setup";
        }

        PopulateDropdowns();
        ApplyCurrentSettings(current);
        LoadPairManager();
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  PAIR MANAGER
    // ══════════════════════════════════════════════════════════════════════════

    private void LoadPairManager()
    {
        if (!Com0ComService.IsInstalled)
        {
            // Show "not installed" warning, hide create controls
            PanelNotInstalled.Visibility    = Visibility.Visible;
            PanelPairsInstalled.Visibility  = Visibility.Collapsed;
            return;
        }

        PanelNotInstalled.Visibility   = Visibility.Collapsed;
        PanelPairsInstalled.Visibility = Visibility.Visible;

        RefreshPairsList();
    }

    private void RefreshPairsList()
    {
        PairsList.Children.Clear();
        HidePairFeedback();

        var pairs = Com0ComService.ListPairs();

        if (pairs.Count == 0)
        {
            TxtNoPairs.Visibility = Visibility.Visible;
            return;
        }

        TxtNoPairs.Visibility = Visibility.Collapsed;

        foreach (var pair in pairs)
        {
            // Each pair row: [COM3  ↔  COM4]  [Use A]  [Use B]  [Remove]
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Pair label
            var label = new TextBlock
            {
                Text              = $"  {pair.A.PortName}  ↔  {pair.B.PortName}",
                FontFamily        = new System.Windows.Media.FontFamily("Consolas"),
                FontSize          = 12,
                Foreground        = (Brush)FindResource("TextPrimaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(label, 0);
            row.Children.Add(label);

            // "Use A" — selects port A as the VirtualComPort
            var useA = MakeInlineButton($"Use {pair.A.PortName}", ColBlue, ColWhite);
            useA.Margin = new Thickness(6, 0, 0, 0);
            useA.Click += (_, _) =>
            {
                // Refresh port list first so the virtual port is definitely in the combo
                RefreshComPorts(CboComPort.SelectedItem as string, pair.A.PortName);
                ShowPairFeedback($"Virtual COM Port set to {pair.A.PortName}.", isError: false);
            };
            Grid.SetColumn(useA, 1);
            row.Children.Add(useA);

            // "Use B" — selects port B
            var useB = MakeInlineButton($"Use {pair.B.PortName}", ColBlue, ColWhite);
            useB.Margin = new Thickness(6, 0, 0, 0);
            useB.Click += (_, _) =>
            {
                RefreshComPorts(CboComPort.SelectedItem as string, pair.B.PortName);
                ShowPairFeedback($"Virtual COM Port set to {pair.B.PortName}.", isError: false);
            };
            Grid.SetColumn(useB, 2);
            row.Children.Add(useB);

            // "Remove" button
            var busId  = pair.A.BusId;   // capture for lambda
            var remove = MakeInlineButton("✕ Remove", ColRedBg, ColRedFg);
            remove.Margin = new Thickness(6, 0, 0, 0);
            remove.Click += (_, _) => RemovePair(busId);
            Grid.SetColumn(remove, 3);
            row.Children.Add(remove);

            PairsList.Children.Add(row);
        }
    }

    private void RemovePair(string busId)
    {
        // Extract the numeric index from the bus ID (e.g. "CNC0" → "0")
        var num = string.Concat(busId.Where(char.IsDigit));
        var (ok, err) = Com0ComService.RemovePair(num);

        if (ok)
        {
            RefreshPairsList();
            RefreshComPorts(CboComPort.SelectedItem as string,
                            CboVirtualPort.SelectedItem as string);
            ShowPairFeedback("Pair removed successfully.", isError: false);
        }
        else
        {
            ShowPairFeedback($"Could not remove pair.\n{err}\n\nTip: run as Administrator.", isError: true);
        }
    }

    private void BtnRefreshPairs_Click(object sender, RoutedEventArgs e)
    {
        RefreshPairsList();
        RefreshComPorts(CboComPort.SelectedItem as string,
                        CboVirtualPort.SelectedItem as string);
    }

    private async void BtnInstallCom0Com_Click(object sender, RoutedEventArgs e)
    {
        BtnInstallCom0Com.IsEnabled  = false;
        InstallProgress.Visibility   = Visibility.Visible;
        TxtInstallStatus.Visibility  = Visibility.Visible;
        TxtInstallStatus.Text        = "Downloading com0com…";
        InstallProgress.Value        = 0;

        var progress = new Progress<int>(p =>
        {
            InstallProgress.Value = p;
            TxtInstallStatus.Text = p switch
            {
                < 80  => $"Downloading… {p}%",
                  80  => "Extracting…",
                  90  => "Installing driver… Please click Yes on the security prompt.",
                _     => "Finishing up…",
            };
        });

        var (ok, err) = await Com0ComService.DownloadAndInstallAsync(progress);

        if (ok)
        {
            TxtInstallStatus.Text    = "com0com installed! Refreshing…";
            InstallProgress.Value    = 100;
            await Task.Delay(800);
            // Re-run the pair manager load now that com0com is installed
            LoadPairManager();
            RefreshComPorts(CboComPort.SelectedItem as string,
                            CboVirtualPort.SelectedItem as string);
        }
        else
        {
            TxtInstallStatus.Text        = err;
            TxtInstallStatus.Foreground  = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
            BtnInstallCom0Com.IsEnabled  = true;
        }
    }

    private async void BtnCreatePair_Click(object sender, RoutedEventArgs e)
    {
        var portA = TxtNewPortA.Text.Trim().ToUpperInvariant();
        var portB = TxtNewPortB.Text.Trim().ToUpperInvariant();

        if (!portA.StartsWith("COM") || !portB.StartsWith("COM"))
        {
            ShowPairFeedback("Port names must start with COM (e.g. COM3, COM4).", isError: true);
            return;
        }

        // Disable button and show waiting message while UAC prompt + driver install runs
        BtnCreatePair.IsEnabled = false;
        ShowPairFeedback("Click Yes on the Windows security prompt to install the port pair.", isError: false);

        // Run on background thread — CreatePair blocks for up to 1.5s waiting for driver
        var (ok, err) = await Task.Run(() => Com0ComService.CreatePair(portA, portB));

        BtnCreatePair.IsEnabled = true;

        if (ok)
        {
            RefreshPairsList();
            RefreshComPorts(CboComPort.SelectedItem as string, portA);   // pre-select portA

            // Auto-select portA in the Virtual COM Port dropdown
            if (CboVirtualPort.Items.Contains(portA))
                CboVirtualPort.SelectedItem = portA;

            ShowPairFeedback($"Pair {portA} ↔ {portB} created. Virtual Port set to {portA}.",
                isError: false);
        }
        else
        {
            ShowPairFeedback(err, isError: true);
        }
    }

    private void ShowPairFeedback(string message, bool isError)
    {
        TxtPairFeedback.Text = message;

        if (isError)
        {
            TxtPairFeedback.Foreground     = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
            PairFeedbackBorder.Background  = new SolidColorBrush(Color.FromArgb(0x20, 0xDC, 0x26, 0x26));
            PairFeedbackBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xDC, 0x26, 0x26));
        }
        else
        {
            TxtPairFeedback.Foreground     = new SolidColorBrush(Color.FromRgb(0x16, 0x7D, 0x32));
            PairFeedbackBorder.Background  = new SolidColorBrush(Color.FromArgb(0x20, 0x16, 0xA0, 0x34));
            PairFeedbackBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x16, 0xA0, 0x34));
        }

        PairFeedbackBorder.BorderThickness = new Thickness(1);
        PairFeedbackBorder.Visibility      = Visibility.Visible;
    }

    private void HidePairFeedback() =>
        PairFeedbackBorder.Visibility = Visibility.Collapsed;

    private Button MakeInlineButton(string text, Color bg, Color fg) =>
        new()
        {
            Content     = text,
            Style       = (System.Windows.Style)FindResource("InlineButton"),
            Background  = new SolidColorBrush(bg),
            BorderBrush = new SolidColorBrush(bg),
            Foreground  = new SolidColorBrush(fg),
        };

    // Pre-parsed colours used by MakeInlineButton — avoids ColorConverter ambiguity
    private static readonly Color ColBlue   = Color.FromRgb(0x1E, 0x88, 0xE5);
    private static readonly Color ColWhite  = Color.FromRgb(0xFF, 0xFF, 0xFF);
    private static readonly Color ColRedBg  = Color.FromRgb(0xFE, 0xE2, 0xE2);
    private static readonly Color ColRedFg  = Color.FromRgb(0xDC, 0x26, 0x26);

    // ══════════════════════════════════════════════════════════════════════════
    //  DROPDOWNS
    // ══════════════════════════════════════════════════════════════════════════

    private void PopulateDropdowns()
    {
        CboBaudRate.ItemsSource = new[] { 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200 };
        CboDataBits.ItemsSource = new[] { 7, 8 };
        CboParity.ItemsSource   = new[] { "None", "Odd", "Even", "Mark", "Space" };
        CboStopBits.ItemsSource = new[] { "One", "OnePointFive", "Two" };

        RefreshComPorts(_initial.ComPort, _initial.VirtualComPort);
    }

    private void RefreshComPorts(string? selectPhysical, string? selectVirtual)
    {
        // Combine system COM ports + com0com virtual ports from registry.
        // SerialPort.GetPortNames() may miss virtual ports that were just
        // created, so we also pull port names directly from the registry.
        var systemPorts  = SerialPort.GetPortNames();
        var virtualPorts = Com0ComService.ListPairs()
                               .SelectMany(p => new[] { p.A.PortName, p.B.PortName });

        var ports = systemPorts
            .Concat(virtualPorts)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (ports.Length == 0)
        {
            var none = new[] { "(no ports detected)" };
            CboComPort.ItemsSource     = none;
            CboVirtualPort.ItemsSource = none;
            CboComPort.SelectedIndex     = 0;
            CboVirtualPort.SelectedIndex = 0;
            return;
        }

        CboComPort.ItemsSource     = ports;
        CboVirtualPort.ItemsSource = ports;

        CboComPort.SelectedItem =
            selectPhysical is not null && ports.Contains(selectPhysical)
                ? selectPhysical : ports[0];

        CboVirtualPort.SelectedItem =
            selectVirtual is not null && ports.Contains(selectVirtual)
                ? selectVirtual : ports[0];
    }

    private void ApplyCurrentSettings(AppSettings s)
    {
        if (!string.IsNullOrWhiteSpace(s.VirtualComPort) &&
            CboVirtualPort.ItemsSource is string[] vPorts &&
            vPorts.Contains(s.VirtualComPort))
            CboVirtualPort.SelectedItem = s.VirtualComPort;

        if (!string.IsNullOrWhiteSpace(s.ComPort) &&
            CboComPort.ItemsSource is string[] cPorts &&
            cPorts.Contains(s.ComPort))
            CboComPort.SelectedItem = s.ComPort;

        CboBaudRate.SelectedItem = CboBaudRate.Items.Cast<int>().Contains(s.BaudRate) ? s.BaudRate : 2400;
        CboDataBits.SelectedItem = CboDataBits.Items.Cast<int>().Contains(s.DataBits) ? s.DataBits : 8;
        CboParity.SelectedItem   = CboParity.Items.Cast<string>().Contains(s.Parity)  ? s.Parity  : "None";
        CboStopBits.SelectedItem = CboStopBits.Items.Cast<string>().Contains(s.StopBits) ? s.StopBits : "One";

        CboBaudRate.SelectedItem  ??= 2400;
        CboDataBits.SelectedItem  ??= 8;
        CboParity.SelectedItem    ??= "None";
        CboStopBits.SelectedItem  ??= "One";
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  COM PORT DROPDOWN EVENTS
    // ══════════════════════════════════════════════════════════════════════════

    private void CboComPort_DropDownOpened(object sender, EventArgs e) =>
        RefreshComPorts(CboComPort.SelectedItem as string,
                        CboVirtualPort.SelectedItem as string);

    private void CboVirtualPort_DropDownOpened(object sender, EventArgs e) =>
        RefreshComPorts(CboComPort.SelectedItem as string,
                        CboVirtualPort.SelectedItem as string);

    // ══════════════════════════════════════════════════════════════════════════
    //  SAVE
    // ══════════════════════════════════════════════════════════════════════════

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (CboVirtualPort.SelectedItem is null ||
            CboVirtualPort.SelectedItem.ToString()?.StartsWith('(') == true)
        {
            ShowStatus("No virtual COM port selected. Create a pair above first.", isError: true);
            return;
        }

        if (CboComPort.SelectedItem is null ||
            CboComPort.SelectedItem.ToString()?.StartsWith('(') == true)
        {
            ShowStatus("No PDLED8 COM port selected.", isError: true);
            return;
        }

        if (CboVirtualPort.SelectedItem?.ToString() == CboComPort.SelectedItem?.ToString())
        {
            ShowStatus("Virtual COM port and PDLED8 port must be different.", isError: true);
            return;
        }

        // SelectedItem is guaranteed non-null by the guards above
        var newSettings = new AppSettings
        {
            VirtualComPort = (string)CboVirtualPort.SelectedItem!,
            ComPort        = (string)CboComPort.SelectedItem!,
            BaudRate       = (int)CboBaudRate.SelectedItem!,
            DataBits       = (int)CboDataBits.SelectedItem!,
            Parity         = CboParity.SelectedItem!.ToString()!,
            StopBits       = CboStopBits.SelectedItem!.ToString()!,
        };

        try { App.PersistSettings(newSettings); }
        catch (Exception ex)
        {
            ShowStatus($"Failed to save: {ex.Message}", isError: true);
            return;
        }

        ShowStatus("Settings saved. Service is restarting…", isError: false);
        BtnSave.IsEnabled = false;

        SettingsSaved?.Invoke(newSettings);

        _ = Task.Delay(900).ContinueWith(
            _ => Dispatcher.Invoke(Close),
            TaskScheduler.Default);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  FEEDBACK
    // ══════════════════════════════════════════════════════════════════════════

    private void ShowStatus(string message, bool isError)
    {
        TxtStatus.Text = message;

        if (isError)
        {
            TxtStatus.Foreground     = (Brush)FindResource("ErrorBrush");
            StatusBorder.Background  = new SolidColorBrush(Color.FromArgb(0x1A, 0xE5, 0x39, 0x35));
            StatusBorder.BorderBrush = (Brush)FindResource("ErrorBrush");
        }
        else
        {
            TxtStatus.Foreground     = (Brush)FindResource("SuccessBrush");
            StatusBorder.Background  = new SolidColorBrush(Color.FromArgb(0x1A, 0x2E, 0x7D, 0x32));
            StatusBorder.BorderBrush = (Brush)FindResource("SuccessBrush");
        }

        StatusBorder.BorderThickness = new Thickness(1);
        StatusBorder.Visibility      = Visibility.Visible;
    }
}
