using System.IO;
using System.IO.Ports;
using System.Text;
using AroniumBridge.Models;

// ──────────────────────────────────────────────────────────────────────────────
//  VirtualPortService.cs
//
//  Setup (confirmed from screenshots):
//    com0com pair  COM3 ↔ COM4
//    Aronium       → COM4   (Aronium writes Customer Display output here)
//    AroniumBridge → COM3   (we read here)
//    PDLED8        → real COM port (e.g. COM1 or COM2)
//
//  KEY FIX — DTR must be enabled:
//    com0com wires DTR on one port to DSR+DCD on the other.
//    If AroniumBridge opens COM3 with DtrEnable=false, Aronium sees DSR=false
//    on COM4 and may not transmit at all (device not ready).
//    Setting DtrEnable=true on COM3 signals to COM4 that we are listening.
//
//  DIAGNOSTIC LOGGING:
//    Every byte chunk received is written to:
//      %LOCALAPPDATA%\AroniumBridge\vport_log.txt
//    This lets us see exactly what Aronium sends so we can verify the decoder.
//    Delete or clear the file to start a fresh capture.
// ──────────────────────────────────────────────────────────────────────────────

namespace AroniumBridge.Services;

public sealed class VirtualPortService : IDisposable
{
    // ── State ─────────────────────────────────────────────────────────────────

    private SerialPort?               _port;
    private readonly HardwareService  _hardware;
    private readonly AroniumVfdDecoder _decoder = new();
    private CancellationTokenSource?  _cts;
    private bool                      _disposed;

    // ── Diagnostic log path ───────────────────────────────────────────────────

    private static string LogPath =>
        Path.Combine(App.AppDataFolder, "vport_log.txt");

    // ── Events ────────────────────────────────────────────────────────────────

    public event Action<bool, string>? ConnectionStateChanged;

    // ── Constructor ───────────────────────────────────────────────────────────

    public VirtualPortService(HardwareService hardware)
    {
        _hardware = hardware;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  START / STOP
    // ══════════════════════════════════════════════════════════════════════════

    public void Start(AppSettings cfg)
    {
        Stop();
        _cts = new CancellationTokenSource();

        // Start fresh log
        try
        {
            Directory.CreateDirectory(App.AppDataFolder);
            File.WriteAllText(LogPath,
                $"=== AroniumBridge VFD capture started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\r\n" +
                $"Virtual port: {cfg.VirtualComPort}  Baud: {cfg.BaudRate}\r\n\r\n");
        }
        catch { /* log failure is non-fatal */ }

        try
        {
            var parity   = Enum.Parse<Parity>  (cfg.Parity,   ignoreCase: true);
            var stopBits = Enum.Parse<StopBits>(cfg.StopBits, ignoreCase: true);

            _port = new SerialPort(cfg.VirtualComPort, cfg.BaudRate, parity, cfg.DataBits, stopBits)
            {
                Encoding     = Encoding.ASCII,
                ReadTimeout  = SerialPort.InfiniteTimeout,
                WriteTimeout = 500,
                // ── CRITICAL FIX ──────────────────────────────────────────────
                // com0com wires DTR ↔ DSR+DCD.  Raising DTR on COM3 tells
                // Aronium (on COM4) that a device is connected and ready.
                // Without this Aronium silently drops all VFD output.
                DtrEnable    = true,
                RtsEnable    = true,   // also raise RTS so CTS is asserted on COM4
            };

            _port.Open();

            LogLine($"Port opened OK — DtrEnable=true, RtsEnable=true");
            System.Diagnostics.Debug.WriteLine(
                $"[VirtualPort] Opened {cfg.VirtualComPort}  DTR+RTS raised");

            // Wire timer-flush: when 150ms passes with no new 0C,
            // the decoder fires PacketDecoded for each decoded (mode, amount).
            _decoder.SetFlushCallback(_decoder.ParseAndDeliver);
            _decoder.PacketDecoded += async (mode, amount) =>
                await _hardware.WriteAsync(mode, amount).ConfigureAwait(false);

            _ = Task.Run(() => ReadLoopAsync(_cts.Token), _cts.Token);

            ConnectionStateChanged?.Invoke(true, cfg.VirtualComPort);
        }
        catch (Exception ex)
        {
            LogLine($"FAILED to open port: {ex.Message}");
            ClosePort();
            System.Diagnostics.Debug.WriteLine(
                $"[VirtualPort] Failed to open {cfg.VirtualComPort}: {ex.Message}");
            ConnectionStateChanged?.Invoke(false, cfg.VirtualComPort);
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        ClosePort();
        _cts?.Dispose();
        _cts = null;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  READ LOOP
    // ══════════════════════════════════════════════════════════════════════════

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[256];

        try
        {
            while (!ct.IsCancellationRequested && _port is { IsOpen: true })
            {
                int n;
                try
                {
                    n = await _port.BaseStream.ReadAsync(buffer.AsMemory(), ct)
                                              .ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    LogLine($"Read error: {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"[VirtualPort] Read error: {ex.Message}");
                    await Task.Delay(200, ct).ConfigureAwait(false);
                    continue;
                }

                if (n <= 0) continue;

                var chunk = buffer[..n];

                // ── Diagnostic log — hex + ASCII ──────────────────────────────
                var hex   = string.Join(' ', chunk.Select(b => $"{b:X2}"));
                var ascii = new string(chunk.Select(b => b is >= 0x20 and <= 0x7E ? (char)b : '.').ToArray());
                var logLine = $"[{DateTime.Now:HH:mm:ss.fff}] {n,3}B  HEX: {hex,-72}  ASCII: {ascii}";
                LogLine(logLine);
                System.Diagnostics.Debug.WriteLine($"[VirtualPort] {logLine}");

                // ── Decode and forward ────────────────────────────────────────
                foreach (var (mode, amount) in _decoder.Feed(chunk))
                {
                    var decoded = $"  → DECODED  mode={mode}  amount={amount:F2}";
                    LogLine(decoded);
                    System.Diagnostics.Debug.WriteLine($"[VirtualPort]{decoded}");

                    await _hardware.WriteAsync(mode, amount).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex)
        {
            LogLine($"ReadLoop fatal: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[VirtualPort] ReadLoop fatal: {ex.Message}");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  HELPERS
    // ══════════════════════════════════════════════════════════════════════════

    private static void LogLine(string line)
    {
        try { File.AppendAllText(LogPath, line + "\r\n"); }
        catch { /* never crash because of logging */ }
    }

    private void ClosePort()
    {
        if (_port is null) return;
        try   { if (_port.IsOpen) _port.Close(); }
        catch { /* ignore */ }
        finally { _port.Dispose(); _port = null; }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  IDisposable
    // ══════════════════════════════════════════════════════════════════════════

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        GC.SuppressFinalize(this);
    }
}

// ──────────────────────────────────────────────────────────────────────────────
//  AroniumVfdDecoder
//
//  VERIFIED PROTOCOL from live vport_log.txt capture:
//
//  Every screen update starts with  0C 0B  (FF = clear, VT = cursor home).
//  Within the update, lines are separated by  0A 0D  or just  0D.
//
//  Captured examples:
//
//    Welcome:      0C 0B | "      WELCOME!"
//                   → no amount; ignored
//
//    Item scan:    0C 0B | "Samsung TV"  0A 0D  "1x 35,000.00"
//                   → line1="Samsung TV" (product), line2="1x 35,000.00"
//                   → mode=Price, amount=35000.00
//
//    Total:        0C 0B | 0A 0D  "Total:     75,000.00"
//                   → line1="" (empty), line2="Total:     75,000.00"
//                   → mode=Total, amount=75000.00
//
//    Paid+Change:  0C 0B | "Paid:      75,000.00"  0D  "Change:         0.00"
//                   → line1="Paid:      75,000.00", line2="Change:         0.00"
//                   → emit (Collect,75000) then (Change,0.00)
//
//  Number format: comma thousands separator, period decimal  e.g. "35,000.00"
//  Amount extraction: regex finds the last decimal number in the line.
// ──────────────────────────────────────────────────────────────────────────────

internal sealed class AroniumVfdDecoder
{
    // Accumulates bytes between 0C (FF) markers
    private readonly List<byte> _packet = new();
    private bool                _skipVt = false;    // skip 0B immediately after 0C

    // ── Flush timer ─────────────────────────────────────────────────────────────────
    // Without this timer, every item is displayed ONE event late because
    // Aronium delimits packets with 0C at the START: "0C [item1] 0C [item2] ..."
    // We only know item1 is complete when item2's 0C arrives — one step behind.
    //
    // Fix: after each read that adds bytes to _packet, arm a 150ms timer.
    // If no new 0C arrives within 150ms the packet is considered complete
    // and is flushed immediately.
    // When a new 0C DOES arrive before the timer fires, CancelFlushTimer() is
    // called and FlushPacket() runs synchronously (no duplicate flush).

    private System.Threading.Timer?  _flushTimer;
    private Action<byte[]>?          _onTimerFlush;   // callback set by ReadLoopAsync
    private readonly Lock            _packetLock = new();

    private const int FlushTimeoutMs = 150;

    /// <summary>
    /// Called once by the owning <see cref="VirtualPortService"/> to supply
    /// the async callback that processes decoded results outside the timer thread.
    /// </summary>
    internal void SetFlushCallback(Action<byte[]> callback)
    {
        _onTimerFlush = callback;
    }

    private void ArmFlushTimer()
    {
        _flushTimer?.Dispose();
        _flushTimer = new System.Threading.Timer(_ =>
        {
            byte[]? data = null;
            lock (_packetLock)
            {
                if (_packet.Count == 0) return;
                data = _packet.ToArray();
                _packet.Clear();
            }
            if (data is { Length: > 0 })
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[VfdDecoder] timer-flush  {data.Length}B");
                _onTimerFlush?.Invoke(data);
            }
        }, null, FlushTimeoutMs, System.Threading.Timeout.Infinite);
    }

    private void CancelFlushTimer()
    {
        _flushTimer?.Dispose();
        _flushTimer = null;
    }

    // Regex: last decimal number in a string, allows comma thousands separator
    private static readonly System.Text.RegularExpressions.Regex AmountRegex =
        new(@"(\d[\d,]*\.\d+)", System.Text.RegularExpressions.RegexOptions.Compiled);

    // ── Feed ─────────────────────────────────────────────────────────────────

    public IEnumerable<(DisplayMode Mode, decimal Amount)> Feed(byte[] bytes)
    {
        var results = new List<(DisplayMode, decimal)>();

        foreach (byte b in bytes)
        {
            if (_skipVt)
            {
                _skipVt = false;
                if (b == 0x0B) continue;
            }

            if (b == 0x0C)   // FF = start of new packet → flush previous immediately
            {
                CancelFlushTimer();
                FlushPacketInto(results);
                _skipVt = true;
                continue;
            }

            lock (_packetLock)
                _packet.Add(b);
        }

        // If we added bytes, arm the timer so we don't wait for the next 0C
        bool hasBytes;
        lock (_packetLock) hasBytes = _packet.Count > 0;
        if (hasBytes) ArmFlushTimer();

        return results;
    }

    // ── Flush (synchronous path, called from Feed) ─────────────────────────────

    private void FlushPacketInto(List<(DisplayMode, decimal)> results)
    {
        byte[]? data = null;
        lock (_packetLock)
        {
            if (_packet.Count == 0) return;
            data = _packet.ToArray();
            _packet.Clear();
        }
        if (data is { Length: > 0 })
            ParsePacket(data, results);
    }

    // ── Flush (timer path, results delivered via callback) ──────────────────────

    internal void ParseAndDeliver(byte[] data)
    {
        var results = new List<(DisplayMode, decimal)>();
        ParsePacket(data, results);
        foreach (var (mode, amount) in results)
            PacketDecoded?.Invoke(mode, amount);
    }

    /// <summary>Raised on the timer thread when a packet is decoded via timeout.</summary>
    internal event Action<DisplayMode, decimal>? PacketDecoded;

    // ── Parse one packet ─────────────────────────────────────────────────────

    private void ParsePacket(byte[] data, List<(DisplayMode, decimal)> results)
    {
        // Split data into text lines at  0A 0D  or just  0D  or just  0A
        var lines = SplitLines(data);

        System.Diagnostics.Debug.WriteLine(
            $"[VfdDecoder] packet {data.Length}B → {lines.Count} lines: " +
            string.Join(" | ", lines.Select(l => $"'{l.Trim()}'")) );

        switch (lines.Count)
        {
            case 0:
                return;

            case 1:
            {
                // Single line: welcome message or single-value update
                var line = lines[0].Trim();
                var amount = ExtractAmount(line);
                if (amount.HasValue)
                {
                    var mode = InferMode(line, contextLine: "");
                    Emit(results, mode, amount.Value, line, "");
                }
                // else: "WELCOME!" etc — no numeric content, ignore
                break;
            }

            default:
            {
                // Two or more lines.
                // Check every line for an amount; each line carries its own mode.
                // Exception: if line1 has NO amount, use it as context for line2's mode.
                var line1 = lines[0].Trim();
                var line2 = lines[1].Trim();

                var amount1 = ExtractAmount(line1);
                var amount2 = ExtractAmount(line2);

                if (amount1.HasValue)
                {
                    // line1 has an amount (e.g. "Paid:  75,000.00")
                    var mode1 = InferMode(line1, contextLine: "");
                    Emit(results, mode1, amount1.Value, line1, "");

                    if (amount2.HasValue)
                    {
                        // line2 also has an amount (e.g. "Change:  0.00")
                        var mode2 = InferMode(line2, contextLine: "");
                        Emit(results, mode2, amount2.Value, line2, "");
                    }
                }
                else if (amount2.HasValue)
                {
                    // Only line2 has amount; use line1 as mode context
                    // e.g. line1="Samsung TV", line2="1x 35,000.00"
                    // e.g. line1="",           line2="Total:  75,000.00"
                    var mode = InferMode(line2, contextLine: line1);
                    Emit(results, mode, amount2.Value, line2, line1);
                }
                break;
            }
        }
    }

    // ── Split bytes into text lines ───────────────────────────────────────────

    private static List<string> SplitLines(byte[] data)
    {
        var lines  = new List<string>();
        var sb     = new StringBuilder();
        int i      = 0;

        while (i < data.Length)
        {
            byte b = data[i];

            // Skip ESC sequences (ESC + one byte)
            if (b == 0x1B && i + 1 < data.Length)
            {
                i += 2;   // skip ESC + following byte
                continue;
            }

            // 0A 0D (LF+CR) = line separator — consume both
            if (b == 0x0A && i + 1 < data.Length && data[i + 1] == 0x0D)
            {
                lines.Add(sb.ToString());
                sb.Clear();
                i += 2;
                continue;
            }

            // 0D alone (CR) = line separator
            if (b == 0x0D)
            {
                lines.Add(sb.ToString());
                sb.Clear();
                i++;
                continue;
            }

            // 0A alone (LF) = line separator
            if (b == 0x0A)
            {
                lines.Add(sb.ToString());
                sb.Clear();
                i++;
                continue;
            }

            // Printable byte
            if (b >= 0x20)
                sb.Append((char)b);

            i++;
        }

        // Last segment (no trailing separator)
        if (sb.Length > 0)
            lines.Add(sb.ToString());

        return lines;
    }

    // ── Amount extraction ─────────────────────────────────────────────────────

    /// <summary>
    /// Extracts the last decimal number from a text line.
    /// Handles comma thousands separators: "1x 35,000.00" → 35000.00
    /// Returns null if no number found.
    /// </summary>
    private decimal? ExtractAmount(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        // Find all decimal numbers (with optional comma group separators)
        var matches = AmountRegex.Matches(line);
        if (matches.Count == 0) return null;

        // Take the last match (rightmost number)
        var raw = matches[^1].Value;

        // Remove comma group separators before parsing
        var normalized = raw.Replace(",", "");

        return decimal.TryParse(normalized,
                   System.Globalization.NumberStyles.Number,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out var v) ? v : null;
    }

    // ── Mode inference ────────────────────────────────────────────────────────

    /// <summary>
    /// Infers display mode from the line text, falling back to contextLine
    /// (the other line in the pair) when no keyword is found in the primary line.
    /// </summary>
    private static DisplayMode InferMode(string line, string contextLine)
    {
        var mode = InferModeFromKeyword(line);
        if (mode != DisplayMode.None) return mode;

        // Try context line (e.g. product name on line1 when line2 has the price)
        mode = InferModeFromKeyword(contextLine);
        if (mode != DisplayMode.None) return mode;

        // Non-empty context with no keyword = product name → PRICE
        if (!string.IsNullOrWhiteSpace(contextLine)) return DisplayMode.Price;

        return DisplayMode.Total;   // default
    }

    private static DisplayMode InferModeFromKeyword(string text)
    {
        var u = text.ToUpperInvariant();
        if (u.Contains("TOTAL") || u.Contains("SUBTOT") || u.Contains("SUM"))   return DisplayMode.Total;
        if (u.Contains("PAID")  || u.Contains("COLLECT") || u.Contains("CASH")
            || u.Contains("TENDER") || u.Contains("RECV"))                       return DisplayMode.Collect;
        if (u.Contains("CHANGE") || u.Contains("BALANCE") || u.Contains("DUE")) return DisplayMode.Change;
        if (u.Contains("PRICE") || u.Contains("UNIT"))                          return DisplayMode.Price;
        return DisplayMode.None;    // no keyword found
    }

    // ── Emit helper ───────────────────────────────────────────────────────────

    private static void Emit(List<(DisplayMode, decimal)> results,
                             DisplayMode mode, decimal amount,
                             string line, string context)
    {
        results.Add((mode, amount));
        System.Diagnostics.Debug.WriteLine(
            $"[VfdDecoder] EMIT  mode={mode,-8}  amount={amount,12:F2}  " +
            $"line=\"{line.Trim()}\"  ctx=\"{context.Trim()}\"");
    }
}
