using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Net.Http;
using System.Text.RegularExpressions;
using Microsoft.Win32;

// ──────────────────────────────────────────────────────────────────────────────
//  Com0ComService.cs
//
//  Manages com0com virtual COM port pairs.
//
//  KEY DESIGN DECISION — why we don't capture setupc.exe output:
//    setupc.exe requires Administrator rights.  To elevate it we must use
//    UseShellExecute=true + Verb="runas".  Windows does not allow stdout/stderr
//    redirection when UseShellExecute=true, so we can never read setupc output.
//
//  Instead:
//    • ListPairs()    → reads the Windows registry directly (no setupc needed)
//    • CreatePair()   → runs setupc elevated, then verifies via registry
//    • RemovePair()   → runs setupc elevated, then verifies via registry
// ──────────────────────────────────────────────────────────────────────────────

namespace AroniumBridge.Services;

/// <summary>One end of a virtual COM port pair.</summary>
public sealed record VirtualPortEnd(string BusId, string PortName);

/// <summary>A complete virtual COM port pair.</summary>
public sealed record VirtualPortPair(VirtualPortEnd A, VirtualPortEnd B)
{
    public override string ToString() => $"{A.PortName}  ↔  {B.PortName}";
}

public enum DiagnosticStatus { Ok, Warning, Error }

public sealed record DiagnosticResult(DiagnosticStatus Status, string Message, string? Details = null);

/// <summary>
/// Manages com0com virtual port pairs via setupc.exe + Windows registry.
/// </summary>
public static partial class Com0ComService
{
    // ── setupc.exe discovery ──────────────────────────────────────────────────

    private static readonly string[] KnownPaths =
    [
        @"C:\Program Files (x86)\com0com\setupc.exe",
        @"C:\Program Files\com0com\setupc.exe",
        @"C:\com0com\setupc.exe",
    ];

    /// <summary>Path to setupc.exe, or null if com0com is not installed.</summary>
    public static string? SetupCPath
    {
        get
        {
            // 1. Check known default paths
            var path = KnownPaths.FirstOrDefault(File.Exists);
            if (path != null) return path;

            // 2. Check registry for installation path
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\com0com");
                if (key?.GetValue("InstallLocation") is string loc && Directory.Exists(loc))
                {
                    path = Path.Combine(loc, "setupc.exe");
                    if (File.Exists(path)) return path;
                }
                
                // Also check 32-bit registry on 64-bit Windows
                using var key32 = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\com0com");
                if (key32?.GetValue("InstallLocation") is string loc32 && Directory.Exists(loc32))
                {
                    path = Path.Combine(loc32, "setupc.exe");
                    if (File.Exists(path)) return path;
                }
            }
            catch { /* ignore registry errors */ }

            return null;
        }
    }

    /// <summary>True when com0com is installed and setupc.exe is found.</summary>
    public static bool IsInstalled => SetupCPath is not null;

    // ── Registry path where com0com stores its port configuration ─────────────
    //
    //  com0com writes one sub-key per port under:
    //    HKLM\SYSTEM\CurrentControlSet\Services\com0com\Parameters
    //  Each sub-key is named after the device (e.g. "CNC0", "CNC1") and has
    //  a "PortName" string value (e.g. "COM3").
    //  Ports are paired: CNC0↔CNC1, CNC2↔CNC3, etc.

    private const string RegPath =
        @"SYSTEM\CurrentControlSet\Services\com0com\Parameters";

    private const string EnumPath =
        @"SYSTEM\CurrentControlSet\Enum\com0com";

    // ── Installer download ────────────────────────────────────────────────────

    private const string InstallerUrl =
        "https://sourceforge.net/projects/com0com/files/com0com/2.2.2.0/com0com-2.2.2.0-x64-fre-signed.zip/download";

    private static string InstallerCache =>
        Path.Combine(Path.GetTempPath(), "com0com_setup.zip");

    /// <summary>
    /// Downloads, extracts, and silently installs com0com.
    /// The user sees only one UAC prompt — no wizard, no Next/Finish.
    /// Progress: 0–79 = downloading, 80 = extracting, 90 = installing, 100 = done.
    /// </summary>
    public static async Task<(bool Success, string Error)> DownloadAndInstallAsync(
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            // ── 1. Download ───────────────────────────────────────────────────
            using var http = new HttpClient();
            http.Timeout   = TimeSpan.FromMinutes(5);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AroniumBridge/1.0");

            using var resp = await http.GetAsync(InstallerUrl,
                HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            var total   = resp.Content.Headers.ContentLength ?? -1L;
            var zipPath = InstallerCache;

            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(zipPath))
            {
                var buf      = new byte[65536];
                long written = 0;
                int  n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    written += n;
                    if (total > 0)
                        progress?.Report((int)(written * 79 / total));
                }
            }

            // ── 2. Extract ────────────────────────────────────────────────────
            progress?.Report(80);

            var extractDir = Path.Combine(Path.GetTempPath(), "com0com_install");
            if (Directory.Exists(extractDir))
                Directory.Delete(extractDir, recursive: true);

            await Task.Run(() =>
                System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, extractDir), ct);

            var setupExe = Directory.GetFiles(extractDir, "setup.exe",
                               SearchOption.AllDirectories).FirstOrDefault()
                        ?? Directory.GetFiles(extractDir, "*.exe",
                               SearchOption.AllDirectories).FirstOrDefault();

            if (setupExe is null)
                return (false, "Could not find setup.exe in the downloaded package.");

            // ── 3. Silent install via NSIS /S flag ────────────────────────────
            progress?.Report(90);

            var installDir = @"C:\Program Files (x86)\com0com";
            var psi = new ProcessStartInfo(setupExe, $"/S /D={installDir}")
            {
                UseShellExecute = true,
                Verb            = "runas",
                CreateNoWindow  = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return (false, "Could not start the installer. Try running as Administrator.");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(3));
            await proc.WaitForExitAsync(timeoutCts.Token);

            progress?.Report(100);

            // Verify by checking if setupc.exe now exists
            bool installed = KnownPaths.Any(File.Exists);
            return installed
                ? (true,  string.Empty)
                : (false, "Installation may have failed — setupc.exe not found.\n" +
                          "Try running AroniumBridge as Administrator and install again.");
        }
        catch (OperationCanceledException)
        {
            return (false, "Installation timed out or was cancelled.");
        }
        catch (Exception ex)
        {
            return (false, $"Installation failed: {ex.Message}");
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all installed virtual port pairs by reading the Windows registry.
    /// Does NOT call setupc.exe — works without elevation.
    /// </summary>
    public static List<VirtualPortPair> ListPairs()
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(RegPath);
            if (root is null) return [];

            // Collect all sub-keys that have a PortName value, sorted by name
            // so that CNC0/CNC1 stay together, CNC2/CNC3 together, etc.
            var ends = new List<VirtualPortEnd>();

            var subKeyNames = root.GetSubKeyNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var name in subKeyNames)
            {
                using var sub = root.OpenSubKey(name);
                if (sub?.GetValue("PortName") is string portName && !string.IsNullOrWhiteSpace(portName))
                    ends.Add(new VirtualPortEnd(name, portName));
            }

            // Pair adjacent entries: [0]↔[1], [2]↔[3], …
            var pairs = new List<VirtualPortPair>();
            for (int i = 0; i + 1 < ends.Count; i += 2)
                pairs.Add(new VirtualPortPair(ends[i], ends[i + 1]));

            return pairs;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Com0Com] ListPairs registry failed: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Checks if the com0com kernel driver is loaded and running.
    /// </summary>
    public static (bool Running, string Status) GetDriverStatus()
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", "query com0com")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return (false, "Could not start sc.exe");
            
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();

            if (output.Contains("RUNNING")) return (true, "Running");
            if (output.Contains("START_PENDING")) return (true, "Starting");
            if (output.Contains("STOPPED")) return (false, "Stopped");
            if (output.Contains("1060")) return (false, "Service not installed");

            return (false, "Unknown status");
        }
        catch (Exception ex)
        {
            return (false, $"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Attempts to start the com0com kernel driver elevated.
    /// </summary>
    public static async Task<(bool Success, string Error)> StartDriverAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", "start com0com")
            {
                UseShellExecute = true,
                Verb            = "runas",
                WindowStyle     = ProcessWindowStyle.Hidden,
            };
            
            using var proc = Process.Start(psi);
            if (proc == null) return (false, "Could not start sc.exe elevated.");
            
            await proc.WaitForExitAsync();

            // Polling check for Running state
            for (int i = 0; i < 10; i++)
            {
                var (running, _) = GetDriverStatus();
                if (running) return (true, string.Empty);
                await Task.Delay(1000);
            }

            return (false, "Driver start command sent, but driver is not yet Running.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Performs a comprehensive diagnostic of com0com installation and configuration.
    /// </summary>
    public static List<DiagnosticResult> PerformComprehensiveDiagnostic(string? targetVirtualPort = null)
    {
        var results = new List<DiagnosticResult>();

        // 1. Installation check
        string? installPath = SetupCPath;
        if (installPath == null)
        {
            results.Add(new DiagnosticResult(DiagnosticStatus.Error, 
                "com0com is not installed.", "setupc.exe not found in known paths or registry."));
            return results;
        }
        results.Add(new DiagnosticResult(DiagnosticStatus.Ok, "com0com files found.", $"Path: {installPath}"));

        // 2. Driver check
        var (running, status) = GetDriverStatus();
        if (!running)
        {
            results.Add(new DiagnosticResult(DiagnosticStatus.Error, 
                $"com0com driver is {status}.", "The kernel driver must be running for virtual ports to work."));
        }
        else
        {
            results.Add(new DiagnosticResult(DiagnosticStatus.Ok, "com0com kernel driver is running."));
        }

        // 3. Registry check
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(RegPath);
            if (root == null)
            {
                results.Add(new DiagnosticResult(DiagnosticStatus.Error, 
                    "com0com configuration not found in registry.", $"Path: HKLM\\{RegPath}"));
            }
            else
            {
                var pairs = ListPairs();
                if (pairs.Count == 0)
                {
                    results.Add(new DiagnosticResult(DiagnosticStatus.Warning, 
                        "No virtual port pairs are configured.", "Use com0com setup or AroniumBridge settings to create a pair."));
                }
                else
                {
                    results.Add(new DiagnosticResult(DiagnosticStatus.Ok, 
                        $"{pairs.Count} virtual port pair(s) found in registry."));

                    // Check if target port exists in pairs
                    if (!string.IsNullOrEmpty(targetVirtualPort))
                    {
                        bool found = pairs.Any(p => 
                            p.A.PortName.Equals(targetVirtualPort, StringComparison.OrdinalIgnoreCase) || 
                            p.B.PortName.Equals(targetVirtualPort, StringComparison.OrdinalIgnoreCase));
                        
                        if (!found)
                        {
                            results.Add(new DiagnosticResult(DiagnosticStatus.Error, 
                                $"Target virtual port {targetVirtualPort} is not part of any com0com pair.", 
                                "Check your settings and ensure the port is correctly assigned in com0com."));
                        }
                        else
                        {
                            results.Add(new DiagnosticResult(DiagnosticStatus.Ok, 
                                $"Target port {targetVirtualPort} is correctly configured in a pair."));
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            results.Add(new DiagnosticResult(DiagnosticStatus.Error, 
                "Error reading com0com registry.", ex.Message));
        }

        // 4. PnP check (Enum)
        try
        {
            using var enumRoot = Registry.LocalMachine.OpenSubKey(EnumPath);
            if (enumRoot == null)
            {
                results.Add(new DiagnosticResult(DiagnosticStatus.Warning, 
                    "com0com PnP enumeration key missing.", "The driver may not have initialized any devices yet."));
            }
            else
            {
                results.Add(new DiagnosticResult(DiagnosticStatus.Ok, "com0com PnP enumeration key exists."));
            }
        }
        catch (Exception ex)
        {
            results.Add(new DiagnosticResult(DiagnosticStatus.Warning, 
                "Could not access com0com PnP registry.", ex.Message));
        }

        // 5. System Port check
        var systemPorts = SerialPort.GetPortNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(targetVirtualPort))
        {
            if (!systemPorts.Contains(targetVirtualPort))
            {
                results.Add(new DiagnosticResult(DiagnosticStatus.Error, 
                    $"Port {targetVirtualPort} is configured but not visible to the system.", 
                    "Windows does not see this COM port. Try restarting the com0com service or your computer."));
            }
            else
            {
                results.Add(new DiagnosticResult(DiagnosticStatus.Ok, 
                    $"Port {targetVirtualPort} is physically present in the system."));
            }
        }

        return results;
    }

    /// <summary>
    /// Creates a new virtual COM port pair by running setupc.exe elevated.
    /// Verifies success via the registry after the process exits.
    /// </summary>
    public static (bool Success, string Error) CreatePair(string portA, string portB)
    {
        if (!IsInstalled)
            return (false, "com0com is not installed. Use the Install button above.");

        if (string.IsNullOrWhiteSpace(portA) || string.IsNullOrWhiteSpace(portB))
            return (false, "Both port names must be specified.");

        if (portA.Equals(portB, StringComparison.OrdinalIgnoreCase))
            return (false, "Port A and Port B must be different.");

        // Snapshot ports before so we can verify they were actually created
        var beforePorts = SerialPort.GetPortNames()
            .Select(p => p.ToUpperInvariant()).ToHashSet();

        // Create the pair.
        // "use Ports class" makes the ports register as standard Windows COM ports
        // (appear in Device Manager under "Ports (COM & LPT)" and in GetPortNames()).
        // Without this flag the ports exist only in com0com's internal registry
        // and are invisible to everything else.
        var cmd = $"install PortName={portA.ToUpperInvariant()} PortName={portB.ToUpperInvariant()}";
        RunSetupCElevated(cmd);

        // Wait for driver to register the base pair
        System.Threading.Thread.Sleep(2000);

        // Now find the bus IDs that were just created so we can set Ports class on them.
        // The new devices will be the ones whose PortName matches portA or portB.
        var newPairs = ListPairs();
        var newA = newPairs.SelectMany(p => new[] { p.A, p.B })
                           .FirstOrDefault(e => e.PortName.Equals(portA, StringComparison.OrdinalIgnoreCase));
        var newB = newPairs.SelectMany(p => new[] { p.A, p.B })
                           .FirstOrDefault(e => e.PortName.Equals(portB, StringComparison.OrdinalIgnoreCase));

        // Enable "use Ports class" on both ends so they appear as standard COM ports
        if (newA is not null)
            RunSetupCElevated($"change {newA.BusId} PortName={portA.ToUpperInvariant()} use Ports class");
        if (newB is not null)
            RunSetupCElevated($"change {newB.BusId} PortName={portB.ToUpperInvariant()} use Ports class");

        // Give Windows time to enumerate the new COM ports
        System.Threading.Thread.Sleep(3000);

        // Verify via registry
        var afterPairs = ListPairs();
        bool aFound = afterPairs.Any(p =>
            p.A.PortName.Equals(portA, StringComparison.OrdinalIgnoreCase) ||
            p.B.PortName.Equals(portA, StringComparison.OrdinalIgnoreCase));
        bool bFound = afterPairs.Any(p =>
            p.A.PortName.Equals(portB, StringComparison.OrdinalIgnoreCase) ||
            p.B.PortName.Equals(portB, StringComparison.OrdinalIgnoreCase));

        if (aFound && bFound)
            return (true, string.Empty);

        // Also accept if COM ports appeared in the system port list
        var afterPorts = SerialPort.GetPortNames()
            .Select(p => p.ToUpperInvariant()).ToHashSet();
        if (afterPorts.Contains(portA.ToUpperInvariant()) &&
            afterPorts.Contains(portB.ToUpperInvariant()))
            return (true, string.Empty);

        return (false,
            "Could not verify that the pair was created.\n" +
            "Make sure you clicked Yes on the security prompt,\n" +
            "and that the port names are not already in use.");
    }

    /// <summary>
    /// Removes a virtual COM port pair. <paramref name="busId"/> is the sub-key
    /// name from the registry, e.g. "CNC0". Extracts the numeric index and calls
    /// <c>setupc remove N</c> elevated.
    /// </summary>
    public static (bool Success, string Error) RemovePair(string busId)
    {
        if (!IsInstalled)
            return (false, "com0com is not installed.");

        // Extract numeric index from bus ID (e.g. "CNC0" → 0)
        var digits = string.Concat(busId.Where(char.IsDigit));
        if (!int.TryParse(digits, out int index))
            return (false, $"Cannot determine pair index from bus ID '{busId}'.");

        // setupc remove N removes the pair that contains device index N
        // (each pair has two devices; we pass the index of the first one)
        RunSetupCElevated($"remove {index}");

        System.Threading.Thread.Sleep(1500);

        // Verify: the bus IDs for this pair should no longer exist in registry
        var remaining = ListPairs();
        bool stillExists = remaining.Any(p =>
            p.A.BusId.Equals(busId, StringComparison.OrdinalIgnoreCase) ||
            p.B.BusId.Equals(busId, StringComparison.OrdinalIgnoreCase));

        return stillExists
            ? (false, "Pair may still exist after removal attempt.\n" +
                      "Check that you clicked Yes on the security prompt.")
            : (true, string.Empty);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Runs setupc.exe elevated (UAC) and waits for it to finish.
    /// Output cannot be captured when elevated — success is verified by the caller.
    ///
    /// CRITICAL: WorkingDirectory must be set to the folder containing setupc.exe.
    /// setupc.exe resolves com0com.inf and driver binaries relative to its own
    /// directory.  If WorkingDirectory is wrong the install silently exits with
    /// code 1 and nothing is created.
    /// </summary>
    private static void RunSetupCElevated(string arguments)
    {
        var path = SetupCPath;
        if (path is null) return;

        var workDir = Path.GetDirectoryName(path)!;

        // Write a tiny .bat file that:
        //   1. cd /d into the com0com directory  (so setupc finds com0com.inf)
        //   2. runs setupc with the given arguments
        // A .bat with cd /d is the only reliable way to set the working directory
        // for an elevated process across all Windows versions.
        var batPath = Path.Combine(Path.GetTempPath(), "com0com_run.bat");
        File.WriteAllText(batPath,
            $"@echo off\r\n" +
            $"cd /d \"{workDir}\"\r\n" +
            $"\"{path}\" {arguments}\r\n");

        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/C \"{batPath}\"")
            {
                UseShellExecute = true,
                Verb            = "runas",
                WindowStyle     = ProcessWindowStyle.Hidden,
            };

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                System.Diagnostics.Debug.WriteLine("[Com0Com] Failed to start elevated cmd");
                return;
            }

            proc.WaitForExit(30_000);
            System.Diagnostics.Debug.WriteLine(
                $"[Com0Com] setupc {arguments} exited with code {proc.ExitCode}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[Com0Com] RunSetupCElevated failed: {ex.Message}");
        }
        finally
        {
            try { File.Delete(batPath); } catch { }
        }
    }

}
