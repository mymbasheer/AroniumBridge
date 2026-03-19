namespace AroniumBridge.Models;

// ──────────────────────────────────────────────────────────────────────────────
//  DisplayModel.cs  —  PDLED8 protocol types
//
//  Physical hardware layout (from LED8 command spec):
//
//    ┌─────────────────────────────────────────┐
//    │  8 8 8 8 8 8 8 8   ← 8 numeric digits   │
//    │ [PRICE][TOTAL][COLLECT][CHANGE]           │
//    │  ↑ hardware LED indicator labels          │
//    └─────────────────────────────────────────┘
//
//  The four labels (PRICE / TOTAL / COLLECT / CHANGE) are PHYSICAL LED indicators
//  soldered onto the display PCB.  They are controlled by the ESC s n command —
//  they are NEVER written as text characters.
//
//  ESC/POS wire sequence per update (see command set §6 and §9):
//
//    [0C]                     CLR     — clear 8-digit display
//    [1B][73H][n]             ESC s n — light one hardware indicator
//    [1B][6CH][pos]           ESC l p — cursor to right-align start position
//    [1B][51H][41H][data][0D] ESC Q A — send numeric data string + CR
//
//  Valid data chars: '0'–'9' (30H–39H), '-' (2DH), '.' (2EH).
//  Without decimal point: 1 ≤ n ≤ 8.
//  With decimal point:    1 ≤ n ≤ 15 (8 digit chars + up to 7 '.' chars).
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The four hardware LED indicator modes of the PDLED8.
/// Each maps directly to the <c>ESC s n</c> command byte (ASCII digit '0'–'4').
/// </summary>
public enum DisplayMode
{
    /// <summary>Internal sentinel: no keyword matched. Never sent to hardware.</summary>
    None    = '0',

    /// <summary>PRICE indicator on.             ESC s '1'</summary>
    Price   = '1',

    /// <summary>TOTAL indicator on.             ESC s '2'</summary>
    Total   = '2',

    /// <summary>COLLECT indicator on.           ESC s '3'</summary>
    Collect = '3',

    /// <summary>CHANGE indicator on.            ESC s '4'</summary>
    Change  = '4',
}

/// <summary>
/// A fully-validated display packet produced by <see cref="DisplayPacket.Create"/>.
/// <para>
/// <c>DataString</c> contains only valid PDLED8 characters (digits, '.', '-'),
/// trimmed of any leading spaces.  Maximum 8 digit characters + decimal points.
/// </para>
/// <c>CursorPosition</c> is the 1-based start position (1–8) for right-alignment
/// via the <c>ESC l n</c> command.  Positions outside 1–8 are clamped.
/// </summary>
public sealed record DisplayPacket(DisplayMode Mode, string DataString, int CursorPosition)
{
    // ── Static factory ────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a right-aligned <see cref="DisplayPacket"/> for the given
    /// <paramref name="mode"/> and <paramref name="amount"/>.
    /// </summary>
    public static DisplayPacket Create(DisplayMode mode, decimal amount)
    {
        // Format amount as a minimal string: no trailing zeros beyond 2 dp,
        // but always show exactly 2 decimal places (standard POS convention).
        var raw = amount.ToString("F2");   // e.g. "1250.75", "9.99", "0.00"

        // DataString validation: only digits, '-', '.'  (spec §6)
        var data = raw.Trim();

        // Count the digit characters (not decimal points) to determine
        // how many of the 8 display positions they occupy.
        int digitCount = data.Count(char.IsDigit);
        int dotCount   = data.Count(c => c == '.');
        // Each decimal point shares a digit position on physical 7-seg displays.
        // On the PDLED8 the decimal point sits in the digit cell to its left,
        // so it does NOT consume an extra position.  Total visual width = digitCount.
        int visualWidth = digitCount + (data.StartsWith('-') ? 1 : 0);

        // Clamp to 8 visible positions
        if (visualWidth > 8)
        {
            // Overflow: drop leading digits (shouldn't happen for normal POS amounts)
            data = data[^8..];
            visualWidth = 8;
        }

        // Cursor start position for right-alignment (1-indexed, clamped to 1–8)
        int startPos = Math.Max(1, Math.Min(8, 9 - visualWidth));

        return new DisplayPacket(mode, data, startPos);
    }

    // ── Display helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns a space-padded 8-character preview string for the simulator UI.
    /// Spaces represent unlit digit positions (cursor was positioned past them).
    /// </summary>
    public string PreviewString()
    {
        // Pad left with spaces to represent right-alignment
        var padded = DataString.PadLeft(8 + DataString.Count(c => c == '.'));
        // Trim to 8 visual chars accounting for decimal points
        return padded.Length <= 10 ? padded : padded[^10..];
    }
}
