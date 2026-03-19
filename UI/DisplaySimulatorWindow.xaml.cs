using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

// Resolve ambiguities: UseWPF + UseWindowsForms expose duplicate type names.
using Color        = System.Windows.Media.Color;
using FontFamily   = System.Windows.Media.FontFamily;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

// ──────────────────────────────────────────────────────────────────────────────
//  DisplaySimulatorWindow.xaml.cs
//
//  Accurately mirrors the physical PDLED8 layout:
//
//    ┌──────────────────────────────────────────────┐
//    │  [8] [8] [8] [8] [8] [8] [8] [8]            │
//    │   ← 8 amber 7-segment digit positions →      │
//    │                                               │
//    │  ● PRICE   ● TOTAL  ● COLLECT  ● CHANGE      │
//    │   ↑ hardware LED indicator dots + labels      │
//    └──────────────────────────────────────────────┘
//
//  The digit display always shows 8 character cells.
//  Decimal points ('.') sit inside the amber digit cell to their left —
//  they share a cell position, displayed smaller and lower.
//  Blank positions (left of the number due to right-alignment) render
//  as dim ghost cells matching the hardware "off" segment appearance.
//
//  ESC s n indicator mapping (spec §9):
//    n='0' (0x30) → all indicators off
//    n='1' (0x31) → PRICE on
//    n='2' (0x32) → TOTAL on
//    n='3' (0x33) → COLLECT on
//    n='4' (0x34) → CHANGE on
// ──────────────────────────────────────────────────────────────────────────────

using AroniumBridge.Models;
using AroniumBridge.Services;

namespace AroniumBridge.UI;

// ── DigitCell view-model ──────────────────────────────────────────────────────
// Each of the 8 digit positions is represented by this record.
// HasDot = true means this digit position has a decimal point in its
// bottom-right corner (exactly as a real 7-segment display works).
// The digit character itself always renders at full size.

file sealed record DigitCell(string Char, bool HasDot)
{
    public System.Windows.Visibility DotVisibility =>
        HasDot ? System.Windows.Visibility.Visible
               : System.Windows.Visibility.Collapsed;
}

// ─────────────────────────────────────────────────────────────────────────────

public partial class DisplaySimulatorWindow : Window
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private const int MaxLogEntries = 100;

    // ── Mode accent colours ───────────────────────────────────────────────────

    private static readonly Dictionary<DisplayMode, Color> ModeAccent = new()
    {
        [DisplayMode.None]    = Color.FromRgb(0x1F, 0x29, 0x37),   // off / dark
        [DisplayMode.Price]   = Color.FromRgb(0x60, 0xA5, 0xFA),   // blue
        [DisplayMode.Total]   = Color.FromRgb(0xF5, 0x9E, 0x0B),   // amber
        [DisplayMode.Collect] = Color.FromRgb(0x34, 0xD3, 0x99),   // green
        [DisplayMode.Change]  = Color.FromRgb(0xA7, 0x8B, 0xFA),   // purple
    };

    // ── State ─────────────────────────────────────────────────────────────────

    private System.Threading.Timer? _idleTimer;
    private DisplayMode              _selectedMode = DisplayMode.Total;

    // ══════════════════════════════════════════════════════════════════════════
    //  CONSTRUCTOR
    // ══════════════════════════════════════════════════════════════════════════


    public DisplaySimulatorWindow()
    {
        InitializeComponent();

        Icon = WindowIcons.Simulator();

        RenderDigits("        ");           // blank 8 cells on startup
        SetIndicators(DisplayMode.None);    // all indicator lights off

        // Database mode: receives structured DisplayPacket
        HardwareService.DataSent    += OnDataSent;

        Closed += (_, _) =>
        {
            HardwareService.DataSent    -= OnDataSent;
        };
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  LIVE DATA  (thread-pool → UI thread)
    // ══════════════════════════════════════════════════════════════════════════

    // ── Database mode handler ───────────────────────────────────────────────
    private void OnDataSent(DisplayPacket packet) =>
        Dispatcher.InvokeAsync(() => UpdateDisplay(packet, "LIVE"));

    // ══════════════════════════════════════════════════════════════════════════
    //  DISPLAY UPDATE
    // ══════════════════════════════════════════════════════════════════════════

    private void UpdateDisplay(DisplayPacket packet, string source)
    {
        // Build right-aligned display string: pad left with spaces to fill 8 positions.
        // Decimal points share the cell of the preceding digit (visual width unchanged).
        var preview = BuildPreview(packet.DataString, packet.CursorPosition);

        RenderDigits(preview);
        SetIndicators(packet.Mode);
        FlashDigits();

        var ts = DateTime.Now.ToString("HH:mm:ss.fff");
        TxtLastUpdate.Text =
            $"Last update:  {ts}   [{preview}]   {packet.Mode}   pos={packet.CursorPosition}";

        SetLivePill(live: true);
        ResetIdleTimer();
        AppendLog(ts, packet, preview, source);
    }

    // ── Build preview string ──────────────────────────────────────────────────

    /// <summary>
    /// Creates the 8-character preview string that fills the digit cells.
    /// Spaces fill the left side; the data right-aligns from <paramref name="cursorPos"/>.
    /// </summary>
    private static string BuildPreview(string data, int cursorPos)
    {
        // cursorPos is 1-based.  Fill left with spaces.
        var padded = data.PadLeft(data.Length + (cursorPos - 1));

        // Trim or pad to exactly 8 display chars.
        // Decimal points don't consume a separate cell — they sit in their
        // preceding digit's cell — so they count zero towards the 8-cell limit.
        int visualLen = padded.Count(c => c != '.');
        if (visualLen < 8)
            padded = padded.PadLeft(padded.Length + (8 - visualLen));

        return padded;
    }

    // ── Digit rendering ───────────────────────────────────────────────────────

    private void RenderDigits(string preview)
    {
        // Walk the preview string and build DigitCell view-models.
        //
        // Each non-dot character becomes one cell.  If the character
        // immediately following is a '.' that dot is consumed and sets
        // HasDot = true on the current cell.  The digit itself always
        // renders at full size (36 pt); the dot is a separate small
        // glowing Ellipse in the bottom-right corner of the cell,
        // controlled by DotVisibility — exactly like a real 7-segment.
        var cells = new List<DigitCell>(8);
        int i     = 0;

        while (i < preview.Length && cells.Count < 8)
        {
            char c = preview[i];

            if (c == '.')
            {
                // Orphan dot (shouldn't normally occur after BuildPreview).
                // Skip it — it belongs to the preceding cell which already
                // consumed it, or it is a leading dot with nothing before it.
                i++;
                continue;
            }

            bool hasDot = i + 1 < preview.Length && preview[i + 1] == '.';
            cells.Add(new DigitCell(c.ToString(), hasDot));
            i += hasDot ? 2 : 1;   // consume dot too if present
        }

        // Always emit exactly 8 cells; trailing blanks = unlit digit positions
        while (cells.Count < 8)
            cells.Add(new DigitCell(" ", HasDot: false));

        DigitPanel.ItemsSource = cells;
    }

    // ── Flash animation ───────────────────────────────────────────────────────

    private void FlashDigits()
    {
        var flash = new DoubleAnimation(0.25, 1.0, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        DigitPanel.BeginAnimation(OpacityProperty, flash);
    }

    // ── Hardware indicator lights ─────────────────────────────────────────────

    /// <summary>
    /// Mirrors the ESC s n command: lights one indicator dot and its label,
    /// dims all others. Accurately represents the physical PDLED8 behaviour.
    /// </summary>
    private void SetIndicators(DisplayMode active)
    {
        SetOneIndicator(DisplayMode.Price,   BrushPrice,   LblPrice,   active);
        SetOneIndicator(DisplayMode.Total,   BrushTotal,   LblTotal,   active);
        SetOneIndicator(DisplayMode.Collect, BrushCollect, LblCollect, active);
        SetOneIndicator(DisplayMode.Change,  BrushChange,  LblChange,  active);
    }

    private static void SetOneIndicator(
        DisplayMode mode,
        SolidColorBrush dot,
        System.Windows.Controls.TextBlock label,
        DisplayMode active)
    {
        bool on = mode == active;
        var accent = ModeAccent[mode];

        dot.Color   = on ? accent : Color.FromRgb(0x1F, 0x29, 0x37);
        label.Foreground = new SolidColorBrush(
            on ? accent : Color.FromRgb(0x37, 0x41, 0x51));

        if (on)
        {
            // Animate dot glow when active
            var glow = new ColorAnimation(
                Color.FromRgb(0x1F, 0x29, 0x37), accent,
                TimeSpan.FromMilliseconds(150));
            dot.BeginAnimation(SolidColorBrush.ColorProperty, glow);
        }
    }

    // ── Live / Idle pill ──────────────────────────────────────────────────────

    private void SetLivePill(bool live)
    {
        if (live)
        {
            TxtLiveStatus.Text       = "● LIVE";
            TxtLiveStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
            LivePill.Background      = new SolidColorBrush(Color.FromRgb(0x06, 0x4E, 0x3B));
        }
        else
        {
            TxtLiveStatus.Text       = "○  IDLE";
            TxtLiveStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63));
            LivePill.Background      = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x25));
        }
    }

    private void ResetIdleTimer()
    {
        _idleTimer?.Dispose();
        _idleTimer = new System.Threading.Timer(_ =>
            Dispatcher.InvokeAsync(() => SetLivePill(live: false)),
            state: null, dueTime: 3_000, period: System.Threading.Timeout.Infinite);
    }

    // ── Log ───────────────────────────────────────────────────────────────────

    private void AppendLog(string ts, DisplayPacket packet, string preview, string source)
    {
        while (LogPanel.Children.Count >= MaxLogEntries)
            LogPanel.Children.RemoveAt(0);

        var accent     = ModeAccent[packet.Mode];
        var sourceColor = source switch
        {
            "TEST"  => Color.FromRgb(0x60, 0xA5, 0xFA),   // blue
            "VPORT" => Color.FromRgb(0xA7, 0x8B, 0xFA),   // purple (virtual port relay)
            _       => accent,                              // amber/green (database mode)
        };

        var row  = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 11.5 };
        var span = new System.Windows.Documents.Span();

        // Timestamp
        span.Inlines.Add(new System.Windows.Documents.Run($"{ts}  ")
            { Foreground = new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63)) });

        // ESC s n byte shown explicitly
        span.Inlines.Add(new System.Windows.Documents.Run(
            $"ESC s '{(char)packet.Mode}'  ")
            { Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)) });

        // Display value
        span.Inlines.Add(new System.Windows.Documents.Run($"[{preview}]")
        {
            Foreground = new SolidColorBrush(sourceColor),
            FontWeight = FontWeights.Bold,
        });

        // Mode label
        span.Inlines.Add(new System.Windows.Documents.Run($"  {packet.Mode}")
            { Foreground = new SolidColorBrush(accent) });

        // Source tag
        span.Inlines.Add(new System.Windows.Documents.Run($"  ← {source}")
            { Foreground = new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63)) });

        row.Inlines.Add(span);
        LogPanel.Children.Add(row);
        LogScroller.ScrollToBottom();
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  MANUAL TEST INPUT
    // ══════════════════════════════════════════════════════════════════════════

    private void ModeButton_Checked(object sender, RoutedEventArgs e)
    {
        _selectedMode = sender switch
        {
            System.Windows.Controls.RadioButton rb when rb == RbPrice   => DisplayMode.Price,
            System.Windows.Controls.RadioButton rb when rb == RbCollect => DisplayMode.Collect,
            System.Windows.Controls.RadioButton rb when rb == RbChange  => DisplayMode.Change,
            _                                                            => DisplayMode.Total,
        };
    }

    /// <summary>Allow only digits, one decimal point, and one leading minus.</summary>
    private void TxtTestAmount_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        // Guard: WPF fires PreviewTextInput with an empty Text string for IME
        // pre-edit and certain paste events — accessing [0] would throw.
        if (e.Text.Length == 0) { e.Handled = false; return; }

        var cur = TxtTestAmount.Text;
        char ch  = e.Text[0];
        e.Handled = !(char.IsDigit(ch)
                   || (ch == '.' && !cur.Contains('.'))
                   || (ch == '-' && cur.Length == 0));
    }

    private void TxtTestAmount_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SendTestAmount();
    }

    private void BtnSend_Click(object sender, RoutedEventArgs e) => SendTestAmount();

    private void SendTestAmount()
    {
        // Use InvariantCulture so that '.' is always the decimal separator
        // regardless of the Windows regional settings (e.g. Sri Lanka locale).
        if (!decimal.TryParse(TxtTestAmount.Text,
                              NumberStyles.Number,
                              CultureInfo.InvariantCulture,
                              out var amount))
        {
            ShakeInput();
            return;
        }

        var packet = DisplayPacket.Create(_selectedMode, amount);
        UpdateDisplay(packet, "TEST");
        TxtTestAmount.SelectAll();
        TxtTestAmount.Focus();
    }

    private void ShakeInput()
    {
        var shake = new DoubleAnimationUsingKeyFrames();
        shake.KeyFrames.Add(new EasingDoubleKeyFrame(0,   KeyTime.FromTimeSpan(TimeSpan.Zero)));
        shake.KeyFrames.Add(new EasingDoubleKeyFrame(6,   KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(60))));
        shake.KeyFrames.Add(new EasingDoubleKeyFrame(-6,  KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
        shake.KeyFrames.Add(new EasingDoubleKeyFrame(4,   KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180))));
        shake.KeyFrames.Add(new EasingDoubleKeyFrame(0,   KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(240))));

        var t = new TranslateTransform();
        TxtTestAmount.RenderTransform = t;
        t.BeginAnimation(TranslateTransform.XProperty, shake);
    }

    private void BtnClear_Click(object _, RoutedEventArgs _1)
    {
        LogPanel.Children.Clear();
        RenderDigits("        ");
        SetIndicators(DisplayMode.None);
        TxtLastUpdate.Text = "Log cleared.";
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  CLEANUP
    // ══════════════════════════════════════════════════════════════════════════

    protected override void OnClosed(EventArgs e)
    {
        _idleTimer?.Dispose();
        base.OnClosed(e);
    }
}

