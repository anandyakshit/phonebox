
        if (node.TryGetProperty("Children", out var children) && children.ValueKind == JsonValueKind.Array)
            foreach (var child in children.EnumerateArray())
                CollectCpuTemperatures(child, names, temperatures);
    }

    private static Dictionary<string, (long Rx, long Tx)> NetBytes()
    {
        var counters = new Dictionary<string, (long Rx, long Tx)>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet)) continue;
            var d = ni.Description + " " + ni.Name;
            if (!OperatingSystem.IsWindows() && (ni.Name.StartsWith("docker") || ni.Name.StartsWith("veth") || ni.Name.StartsWith("br-")
                || ni.Name.StartsWith("virbr") || ni.Name.StartsWith("utun") || ni.Name.StartsWith("awdl") || ni.Name.StartsWith("llw"))) continue;
            if (d.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || d.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
                || d.Contains("vEthernet", StringComparison.OrdinalIgnoreCase) || d.Contains("VMware", StringComparison.OrdinalIgnoreCase)
                || d.Contains("WSL", StringComparison.OrdinalIgnoreCase) || d.Contains("TAP", StringComparison.Ordinal)
                || d.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var s = ni.GetIPStatistics();
                counters[ni.Id] = (s.BytesReceived, s.BytesSent);
            }
            catch { }
        }
        return counters;
    }

    internal static (double Down, double Up) NetworkRates(
        IReadOnlyDictionary<string, (long Rx, long Tx)> before,
        IReadOnlyDictionary<string, (long Rx, long Tx)> after, double seconds)
    {
        double received = 0, sent = 0;
        foreach (var (id, now) in after)
        {
            // Newly active adapters already carry lifetime totals. Only compare
            // counters for adapters sampled in both readings.
            if (!before.TryGetValue(id, out var previous)) continue;
            if (now.Rx >= previous.Rx) received += now.Rx - previous.Rx;
            if (now.Tx >= previous.Tx) sent += now.Tx - previous.Tx;
        }
        return (received / seconds, sent / seconds);
    }

    private static IReadOnlyList<DiskInfo> ReadDisks()
    {
        const double Gb = 1024d * 1024 * 1024;
        var drives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady && SafeSize(d) > 2 * Gb);
        if (!OperatingSystem.IsWindows())
            // Linux / macOS: real volumes only (no snaps, boot partitions or macOS system snapshots)
            drives = drives.Where(d => d.Name == "/" || d.Name.StartsWith("/home") || d.Name.StartsWith("/mnt/") || d.Name.StartsWith("/media/")
                                       || d.Name.StartsWith("/Volumes/") || d.Name.StartsWith("/data"))
                .Where(d => !d.Name.Contains("/wsl") && !d.Name.Contains("docker") && !d.Name.StartsWith("/mnt/c") && d.Name.Length > 0)
                .DistinctBy(d => (SafeSize(d), d.TotalFreeSpace / (1L << 30)));
        return drives
            .Select(d => new DiskInfo(DiskLabel(d.Name), Math.Round((d.TotalSize - d.TotalFreeSpace) / Gb), Math.Round(d.TotalSize / Gb)))
            .Take(6)
            .ToList();
    }

    private static long SafeSize(DriveInfo d) { try { return d.TotalSize; } catch { return 0; } }

    private static string DiskLabel(string name) =>
        OperatingSystem.IsWindows() ? name.TrimEnd('\\') : name == "/" ? "/" : Path.GetFileName(name.TrimEnd('/'));

    private static string ReadCpuName()
    {
#if !WINDOWS
        return UnixSystem.CpuName();
#else
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "CPU";
        }
        catch { return "CPU"; }
#endif
    }

    private static async Task<string> RunAsync(string file, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file, args) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppPaths.NeutralCwd };
        using var p = Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return output;
    }

#if WINDOWS
    private static (long Idle, long Total) CpuTimes()
    {
        GetSystemTimes(out var idle, out var kernel, out var user);
        return (idle, kernel + user); // kernel time includes idle time
    }

    private static (double Used, double Total) Memory()
    {
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref mem);
        return (mem.ullTotalPhys - mem.ullAvailPhys, mem.ullTotalPhys);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
#else
    private static (long Idle, long Total) CpuTimes() => UnixSystem.CpuTimes();
    private static (double Used, double Total) Memory() => UnixSystem.Memory();
#endif
}
