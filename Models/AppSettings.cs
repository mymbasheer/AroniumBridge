using System.IO;

namespace AroniumBridge.Models;

// ──────────────────────────────────────────────────────────────────────────────
//  AppSettings.cs  —  C# 14 / .NET 10
//  Stores only what is needed: virtual COM port + PDLED8 hardware settings.
//  Database mode and BridgeMode enum have been removed entirely.
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Immutable settings record serialized to / from
/// <c>%LOCALAPPDATA%\AroniumBridge\appsettings.json</c>.
/// </summary>
public sealed record AppSettings
{
    /// <summary>
    /// The "read" end of the com0com virtual port pair.
    /// Aronium writes to COM4; we read from COM3.
    /// </summary>
    public string VirtualComPort
    {
        get;
        init => field = value?.Trim() ?? string.Empty;
    } = string.Empty;

    /// <summary>COM port the PDLED8 is connected to. e.g. "COM1".</summary>
    public string ComPort
    {
        get;
        init => field = value?.Trim() ?? "COM1";
    } = "COM1";

    /// <summary>Baud rate. PDLED8 default is 2400.</summary>
    public int BaudRate { get; init; } = 2400;

    /// <summary>Data bits. PDLED8 default is 8.</summary>
    public int DataBits { get; init; } = 8;

    /// <summary>Parity: "None" | "Odd" | "Even" | "Mark" | "Space".</summary>
    public string Parity
    {
        get;
        init => field = value?.Trim() ?? "None";
    } = "None";

    /// <summary>Stop bits: "One" | "OnePointFive" | "Two".</summary>
    public string StopBits
    {
        get;
        init => field = value?.Trim() ?? "One";
    } = "One";

    /// <summary>True when both port fields are filled in.</summary>
    public bool IsValid() =>
        !string.IsNullOrWhiteSpace(VirtualComPort) &&
        !string.IsNullOrWhiteSpace(ComPort);
}

// ── C# 14 extension block ─────────────────────────────────────────────────────

public static class AppSettingsExtensions
{
    extension(AppSettings s)
    {
        /// <summary>One-liner shown in the tray tooltip and balloon tip.</summary>
        public string HardwareSummary =>
            $"{s.VirtualComPort} → {s.ComPort}  {s.BaudRate} baud  {s.DataBits}{s.Parity[0]}{s.StopBits[0]}";
    }
}
