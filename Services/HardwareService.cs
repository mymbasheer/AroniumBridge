using System.IO.Ports;
using System.Text;
using AroniumBridge.Models;

// ──────────────────────────────────────────────────────────────────────────────
//  HardwareService.cs
//  Manages the RS-232 connection to the PDLED8 Customer Pole Display.
//
//  ESC/POS command set used (from LED8 bits customer display command spec):
//
//  ┌──────────┬──────────────────┬──────────────────────────────────────────┐
//  │ Command  │ Bytes            │ Description                              │
//  ├──────────┼──────────────────┼──────────────────────────────────────────┤
//  │ ESC @    │ 1B 40            │ Initialise — restore power-on state      │
//  │ CLR      │ 0C               │ Clear all 8 digit positions              │
//  │ ESC s n  │ 1B 73 n          │ Set hardware mode indicator (0–4)        │
//  │ ESC l n  │ 1B 6C n          │ Move cursor to position n (1–8)          │
//  │ ESC Q A  │ 1B 51 41 … 0D   │ Write numeric data + CR                  │
//  └──────────┴──────────────────┴──────────────────────────────────────────┘
//
//  Full update sequence for a right-aligned amount (e.g. 999.99 in TOTAL mode):
//    1. 0x0C               → CLR: clear display
//    2. 1B 73 '2'          → ESC s 2: light TOTAL indicator
//    3. 1B 6C '3'          → ESC l 3: cursor to position 3 (right-align 999.99)
//    4. 1B 51 41 "999.99" 0D → ESC Q A "999.99" CR: display the amount
//
//  Thread safety:
//    All public methods are thread-safe.
//    Writes are serialised via System.Threading.Lock.
// ──────────────────────────────────────────────────────────────────────────────

namespace AroniumBridge.Services;

/// <summary>
/// Controls the serial port connection to the PDLED8 Customer Pole Display.
/// </summary>
public sealed class HardwareService : IDisposable
{
    // ── State ─────────────────────────────────────────────────────────────────

    private SerialPort? _port;
    private readonly Lock _lock = new();
    private string _comPort  = string.Empty;
    private bool   _disposed;

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>Fired whenever the port connection state changes. (isConnected, comPort)</summary>
    public event Action<bool, string>? ConnectionStateChanged;

    /// <summary>
    /// Fired on a thread-pool thread for every prepared <see cref="DisplayPacket"/>,
    /// regardless of whether a real COM port is open.
    /// Used by <see cref="DisplaySimulatorWindow"/> to mirror output without hardware.
    /// </summary>
    public static event Action<DisplayPacket>? DataSent;

    /// <summary><c>true</c> when the serial port is open.</summary>
    public bool IsConnected => _port?.IsOpen == true;

    // ══════════════════════════════════════════════════════════════════════════
    //  CONNECT / DISCONNECT
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Opens the COM port from <paramref name="cfg"/> and sends ESC @ init.</summary>
    public void Connect(AppSettings cfg)
    {
        lock (_lock)
        {
            CloseAndDisposePort();
            _comPort = cfg.ComPort;

            try
            {
                var parity   = Enum.Parse<Parity>  (cfg.Parity,   ignoreCase: true);
                var stopBits = Enum.Parse<StopBits>(cfg.StopBits, ignoreCase: true);

                _port = new SerialPort(cfg.ComPort, cfg.BaudRate, parity, cfg.DataBits, stopBits)
                {
                    Encoding     = Encoding.ASCII,
                    WriteTimeout = 1_000,
                    ReadTimeout  = 500,
                    DtrEnable    = false,  // spec: "No handshake signal"
                    RtsEnable    = false,
                };

                _port.Open();

                // ESC @ — initialise display (restore power-on state)
                SendRaw(0x1B, 0x40);

                System.Diagnostics.Debug.WriteLine(
                    $"[HardwareService] Opened {cfg.ComPort} @ {cfg.BaudRate} baud.");
            }
            catch (Exception ex)
            {
                CloseAndDisposePort();
                System.Diagnostics.Debug.WriteLine(
                    $"[HardwareService] Connect failed: {ex.Message}");
                RaiseConnectionState(connected: false);
                return;
            }
        }

        RaiseConnectionState(connected: true);
    }

    /// <summary>Closes the serial port gracefully.</summary>
    public void Disconnect()
    {
        lock (_lock) { CloseAndDisposePort(); }
        RaiseConnectionState(connected: false);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  PUBLIC WRITE METHODS
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Display a <b>TOTAL</b> amount (called by DatabaseMonitorService).</summary>
    public Task WriteTotalAsync(decimal amount)   => WriteAsync(DisplayMode.Total,   amount);

    /// <summary>Display a <b>PRICE</b> (unit item price).</summary>
    public Task WritePriceAsync(decimal amount)   => WriteAsync(DisplayMode.Price,   amount);

    /// <summary>Display a <b>COLLECT</b> (tender / cash received).</summary>
    public Task WriteCollectAsync(decimal amount) => WriteAsync(DisplayMode.Collect, amount);

    /// <summary>Display a <b>CHANGE</b> amount due back to the customer.</summary>
    public Task WriteChangeAsync(decimal amount)  => WriteAsync(DisplayMode.Change,  amount);


    // ── Core write ────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="DisplayPacket"/>, fires <see cref="DataSent"/> for the
    /// simulator, then sends the full ESC/POS byte sequence to the serial port.
    /// </summary>
    public Task WriteAsync(DisplayMode mode, decimal amount)
    {
        var packet = DisplayPacket.Create(mode, amount);

        // Always fire — simulator receives this even with no hardware connected
        DataSent?.Invoke(packet);

        return Task.Run(() =>
        {
            lock (_lock)
            {
                if (_port is not { IsOpen: true }) return;

                try
                {
                    // ── Step 1: CLR (0x0C) ────────────────────────────────
                    // Clear all 8 digit positions on the display.
                    _port.BaseStream.WriteByte(0x0C);

                    // ── Step 2: ESC s n (1B 73 n) ─────────────────────────
                    // Light the correct hardware indicator label.
                    // n is the ASCII digit char stored in DisplayMode enum value.
                    _port.BaseStream.WriteByte(0x1B);
                    _port.BaseStream.WriteByte(0x73);
                    _port.BaseStream.WriteByte((byte)mode);  // '0'–'4'

                    // ── Step 3: ESC l pos (1B 6C pos) ─────────────────────
                    // Move cursor to the right-align start position.
                    // pos is ASCII '1'–'8' (add 0x30 to convert 1-based int).
                    _port.BaseStream.WriteByte(0x1B);
                    _port.BaseStream.WriteByte(0x6C);
                    _port.BaseStream.WriteByte((byte)(0x30 + packet.CursorPosition));

                    // ── Step 4: ESC Q A data CR (1B 51 41 … 0D) ──────────
                    // Send the numeric data string terminated with CR.
                    // Valid chars: digits (30H–39H), '.' (2EH), '-' (2DH).
                    _port.BaseStream.WriteByte(0x1B);
                    _port.BaseStream.WriteByte(0x51);
                    _port.BaseStream.WriteByte(0x41);
                    _port.Write(packet.DataString);
                    _port.BaseStream.WriteByte(0x0D);  // CR

                    System.Diagnostics.Debug.WriteLine(
                        $"[HardwareService] ► mode={mode}  pos={packet.CursorPosition}  data=\"{packet.DataString}\"");
                }
                catch (TimeoutException tex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[HardwareService] Write timeout: {tex.Message}");
                }
                catch (InvalidOperationException iex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[HardwareService] Port closed mid-write: {iex.Message}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[HardwareService] Write error: {ex.Message}");
                }
            }
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  PRIVATE HELPERS
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Writes raw bytes to the port stream.  Caller must hold <c>_lock</c>.
    /// Used for init commands (ESC @) where no packet is needed.
    /// </summary>
    private void SendRaw(params byte[] bytes)
    {
        if (_port is not { IsOpen: true }) return;
        foreach (var b in bytes)
            _port.BaseStream.WriteByte(b);
    }

    /// <remarks>Caller must hold <c>_lock</c>.</remarks>
    private void CloseAndDisposePort()
    {
        if (_port is null) return;
        try   { if (_port.IsOpen) _port.Close(); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[HardwareService] Error closing port: {ex.Message}");
        }
        finally
        {
            _port.Dispose();
            _port = null;
        }
    }

    private void RaiseConnectionState(bool connected) =>
        ConnectionStateChanged?.Invoke(connected, _comPort);

    /// <summary>
    /// Verifies if a physical COM port is present and accessible.
    /// </summary>
    public static DiagnosticResult VerifyHardwarePort(string portName)
    {
        if (string.IsNullOrWhiteSpace(portName))
            return new DiagnosticResult(DiagnosticStatus.Error, "Hardware port name is not set.");

        var systemPorts = SerialPort.GetPortNames();
        if (!systemPorts.Any(p => p.Equals(portName, StringComparison.OrdinalIgnoreCase)))
        {
            return new DiagnosticResult(DiagnosticStatus.Error, 
                $"Physical COM port {portName} not found.", 
                "Check that the PDLED8 is plugged in and recognized by Windows in Device Manager.");
        }

        // Try to open it briefly to check for "In Use"
        try
        {
            using var testPort = new SerialPort(portName);
            testPort.Open();
            testPort.Close();
        }
        catch (UnauthorizedAccessException)
        {
            return new DiagnosticResult(DiagnosticStatus.Warning, 
                $"Port {portName} is in use by another application.", 
                "If AroniumBridge is already running elsewhere, please close it.");
        }
        catch (Exception ex)
        {
            return new DiagnosticResult(DiagnosticStatus.Error, 
                $"Could not access {portName}.", ex.Message);
        }

        return new DiagnosticResult(DiagnosticStatus.Ok, 
            $"Physical port {portName} is present and accessible.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  IDisposable
    // ══════════════════════════════════════════════════════════════════════════

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lock) { CloseAndDisposePort(); }
        GC.SuppressFinalize(this);
    }
}
