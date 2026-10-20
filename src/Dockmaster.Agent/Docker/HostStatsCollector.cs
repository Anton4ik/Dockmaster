using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Dockmaster.Protocol;

namespace Dockmaster.Agent.Docker;

/// <summary>Host level metrics read from /proc (Linux). Falls back gracefully elsewhere.</summary>
public sealed class HostStatsCollector
{
    private (long idle, long total)? _prev;
    private readonly AgentOptions _opts;
    public HostStatsCollector(IOptions<AgentOptions> opts) => _opts = opts.Value;

    public string ReadHostname()
    {
        try
        {
            if (File.Exists(_opts.HostnameFile))
            {
                var h = File.ReadAllText(_opts.HostnameFile).Trim();
                if (h.Length > 0) return h;
            }
        }
        catch { }
        return Environment.MachineName;
    }

    /// <summary>The address this host reaches the internet from (no packet is sent: a UDP connect only picks the route).</summary>
    public static string GuessLocalIp()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            s.Connect("8.8.8.8", 65530);
            return (s.LocalEndPoint as IPEndPoint)?.Address.ToString() ?? "";
        }
        catch { return ""; }
    }

    /// <summary>Every heartbeat: what can be read is in, a file that cannot be leaves its numbers at 0.</summary>
    public HostStats Collect(string? dockerRoot)
    {
        var s = new HostStats();
        if (OperatingSystem.IsLinux())
        {
            try { s.CpuPercent = CpuPercent(); } catch { }
            try { (s.MemoryTotal, s.MemoryUsed) = Memory(); } catch { }
            try { s.Load1 = FirstNumber(File.ReadAllText("/proc/loadavg")); } catch { }
            try { s.UptimeSeconds = (long)FirstNumber(File.ReadAllText("/proc/uptime")); } catch { }
        }
        else s.MemoryTotal = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        try
        {
            var root = !string.IsNullOrEmpty(dockerRoot) && Directory.Exists(dockerRoot) ? dockerRoot : Path.GetPathRoot(Environment.CurrentDirectory)!;
            var di = new DriveInfo(root);
            s.DiskTotal = di.TotalSize;
            s.DiskUsed = di.TotalSize - di.AvailableFreeSpace;
        }
        catch { }
        return s;
    }

    /// <summary>
    /// The share of the ticks since the last reading that were not idle, from the "cpu" line of /proc/stat
    /// (user nice system idle iowait irq softirq steal guest guest_nice); 0 for the first reading.
    /// </summary>
    private double CpuPercent()
    {
        var (idle, total) = CpuTicks(File.ReadLines("/proc/stat").First(l => l.StartsWith("cpu ")));
        var percent = _prev is { } p && total > p.total ? Math.Round((1.0 - (double)(idle - p.idle) / (total - p.total)) * 100.0, 1) : 0;
        _prev = (idle, total);
        return percent;
    }

    /// <summary>The idle (idle + iowait) and total ticks of a "cpu  user nice system idle iowait …" line.</summary>
    internal static (long idle, long total) CpuTicks(string cpuLine)
    {
        var fields = cpuLine.AsSpan(Math.Max(0, cpuLine.IndexOf(' ')));
        long idle = 0, total = 0;
        var i = 0;
        foreach (var range in fields.Split(' '))
        {
            var field = fields[range];
            if (field.IsEmpty || !long.TryParse(field, out var ticks)) continue;
            total += ticks;
            if (i is 3 or 4) idle += ticks; // idle and iowait
            i++;
        }
        return (idle, total);
    }

    /// <summary>MemTotal and what is in use (MemTotal - MemAvailable), bytes, from /proc/meminfo.</summary>
    private static (long total, long used) Memory() => MemoryOf(File.ReadLines("/proc/meminfo"));

    internal static (long total, long used) MemoryOf(IEnumerable<string> meminfoLines)
    {
        long total = 0, available = 0;
        foreach (var line in meminfoLines)
        {
            if (line.StartsWith("MemTotal:")) total = KiloBytes(line);
            else if (line.StartsWith("MemAvailable:")) available = KiloBytes(line);
            if (total > 0 && available > 0) break; // the first lines of the file
        }
        return (total, total - available);
    }

    /// <summary>"MemTotal:       16314840 kB" → bytes.</summary>
    private static long KiloBytes(string line)
    {
        var value = line.AsSpan(line.IndexOf(':') + 1).Trim();
        if (value.IndexOf(' ') is > 0 and var end) value = value[..end];
        return long.TryParse(value, out var kb) ? kb * 1024 : 0;
    }

    /// <summary>The first space-separated number of a /proc file's text ("0.52 0.58 0.59 1/1250 12345").</summary>
    internal static double FirstNumber(string text)
    {
        var value = text.AsSpan();
        if (value.IndexOf(' ') is > 0 and var end) value = value[..end];
        return double.Parse(value, CultureInfo.InvariantCulture);
    }
}
