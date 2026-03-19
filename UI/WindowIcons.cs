using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

// ──────────────────────────────────────────────────────────────────────────────
//  WindowIcons.cs
//  Generates programmatic window icons (32×32) at runtime.
//  No .ico files needed — icons are drawn with GDI+ and converted to
//  WPF ImageSource via Imaging.CreateBitmapSourceFromHIcon.
//
//  Usage (in any Window constructor, after InitializeComponent()):
//    Icon = WindowIcons.Simulator();
//    Icon = WindowIcons.Settings();
// ──────────────────────────────────────────────────────────────────────────────

namespace AroniumBridge.UI;

internal static partial class WindowIcons
{
    // ── Public factory methods ────────────────────────────────────────────────

    /// <summary>
    /// Amber "8" on a dark background — mirrors the PDLED8 display aesthetic.
    /// Used for the Display Simulator window.
    /// </summary>
    public static BitmapSource Simulator() =>
        Create(static g =>
        {
            const int S = 32;

            // Dark display background
            using var bg = new SolidBrush(Color.FromArgb(0x08, 0x08, 0x12));
            g.FillRoundedRect(bg, new RectangleF(0, 0, S, S), 5f);

            // Amber digit "8"
            using var font = new Font("Consolas", 20f, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            using var glow = new SolidBrush(Color.FromArgb(0xF5, 0x9E, 0x0B));
            var text = "8";
            var size = g.MeasureString(text, font);
            g.DrawString(text, font, glow,
                (S - size.Width)  / 2f - 1f,
                (S - size.Height) / 2f + 1f);

            // Small amber dot (decimal point) bottom-right of the digit
            using var dotBrush = new SolidBrush(Color.FromArgb(0xF5, 0x9E, 0x0B));
            g.FillEllipse(dotBrush, S - 9f, S - 9f, 5f, 5f);
        });

    /// <summary>
    /// Blue rounded square with a plug glyph — matches the Settings header badge.
    /// Used for the Settings / Configuration window.
    /// </summary>
    public static BitmapSource Settings() =>
        Create(static g =>
        {
            const int S = 32;

            // Blue rounded badge (same as the tray icon background)
            using var bg = new SolidBrush(Color.FromArgb(0x1E, 0x88, 0xE5));
            g.FillRoundedRect(bg, new RectangleF(0, 0, S, S), 6f);

            // White gear / settings symbol using Segoe UI Symbol ⚙ (U+2699)
            // Falls back gracefully if font not present — still shows the blue badge.
            using var font = new Font("Segoe UI Symbol", 18f,
                System.Drawing.FontStyle.Regular, GraphicsUnit.Pixel);
            var text = "\u2699";   // ⚙
            var size = g.MeasureString(text, font);
            g.DrawString(text, font, Brushes.White,
                (S - size.Width)  / 2f - 1f,
                (S - size.Height) / 2f + 1f);
        });

    // ── Internal drawing helper ───────────────────────────────────────────────

    private static BitmapSource Create(Action<Graphics> draw)
    {
        const int S = 32;
        using var bmp = new Bitmap(S, S, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g   = Graphics.FromImage(bmp);

        g.SmoothingMode      = SmoothingMode.AntiAlias;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.InterpolationMode  = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint  =
            System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        draw(g);

        nint hIcon = bmp.GetHicon();
        try
        {
            return Imaging.CreateBitmapSourceFromHIcon(
                hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    // ── GDI+ rounded rect extension ───────────────────────────────────────────

    private static void FillRoundedRect(
        this Graphics g, System.Drawing.Brush brush, RectangleF r, float radius)
    {
        using var path = BuildRoundedRect(r, radius);
        g.FillPath(brush, path);
    }

    private static GraphicsPath BuildRoundedRect(RectangleF r, float radius)
    {
        var d    = radius * 2f;
        var path = new GraphicsPath();
        path.AddArc(r.X,           r.Y,           d, d, 180, 90);
        path.AddArc(r.Right - d,   r.Y,           d, d, 270, 90);
        path.AddArc(r.Right - d,   r.Bottom - d,  d, d,   0, 90);
        path.AddArc(r.X,           r.Bottom - d,  d, d,  90, 90);
        path.CloseFigure();
        return path;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint _hIcon);
}
