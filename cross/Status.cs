using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace CapriDesktop
{
    internal sealed class AppUse
    {
        internal string App;
        internal double Cpu;
        internal long Memory;
    }

    internal sealed class PcUse
    {
        internal double Cpu;
        internal long MemoryUsed, MemoryTotal;
        internal List<AppUse> Apps = new List<AppUse>();
        internal List<string> Drives = new List<string>();
        internal string Battery;
    }

    // How busy the computer is, the same reading as Windows': half a second of each process's time, memory, drives and battery.
    internal static class Usage
    {
        internal static string Size(long bytes)
        {
            double gb = bytes / (double)(1L << 30);
            return gb >= 1 ? gb.ToString("0.#", CultureInfo.InvariantCulture) + " GB" : Math.Round(bytes / (double)(1L << 20)) + " MB";
        }

        internal static PcUse Read()
        {
            PcUse use = new PcUse();
            Dictionary<int, TimeSpan> before = new Dictionary<int, TimeSpan>();
            foreach (Process process in Process.GetProcesses())
            {
                try { before[process.Id] = process.TotalProcessorTime; }
                catch (Exception) { }
                finally { process.Dispose(); }
            }
            long idle, total, idleAfter, totalAfter;
            bool timed = Limbs.CpuTimes(out idle, out total);
            Stopwatch clock = Stopwatch.StartNew();
            Thread.Sleep(500);
            double spent = Math.Max(1, clock.Elapsed.TotalMilliseconds) * Environment.ProcessorCount;
            Dictionary<string, AppUse> apps = new Dictionary<string, AppUse>(StringComparer.OrdinalIgnoreCase);
            foreach (Process process in Process.GetProcesses())
            {
                try
                {
                    TimeSpan was;
                    double used = before.TryGetValue(process.Id, out was) ? Math.Max(0, (process.TotalProcessorTime - was).TotalMilliseconds) / spent * 100 : 0;
                    AppUse app;
                    if (!apps.TryGetValue(process.ProcessName, out app)) apps[process.ProcessName] = app = new AppUse { App = process.ProcessName };
                    app.Cpu += used;
                    app.Memory += process.WorkingSet64;
                }
                catch (Exception) { }
                finally { process.Dispose(); }
            }
            use.Apps.AddRange(apps.Values);
            if (timed && Limbs.CpuTimes(out idleAfter, out totalAfter) && totalAfter > total) use.Cpu = 100.0 * (1 - (idleAfter - idle) / (double)(totalAfter - total));
            else foreach (AppUse app in use.Apps) use.Cpu += app.Cpu;
            Memory(use);
            foreach (DriveInfo drive in DriveInfo.GetDrives())
                try
                {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady || drive.TotalSize < (1L << 30)) continue;
                    string line = drive.Name + " " + Size(drive.AvailableFreeSpace) + " free of " + Size(drive.TotalSize);
                    if (!use.Drives.Exists(delegate(string known) { return known.Substring(known.IndexOf(' ')) == line.Substring(line.IndexOf(' ')); })) use.Drives.Add(line);
                }
                catch (Exception) { }
            use.Battery = Battery();
            return use;
        }

        private static void Memory(PcUse use)
        {
            if (OperatingSystem.IsLinux() && File.Exists("/proc/meminfo"))
            {
                string info = File.ReadAllText("/proc/meminfo");
                long Kb(string name) { Match found = Regex.Match(info, "^" + name + @":\s+(\d+) kB", RegexOptions.Multiline); return found.Success ? long.Parse(found.Groups[1].Value) * 1024 : 0; }
                use.MemoryTotal = Kb("MemTotal");
                use.MemoryUsed = use.MemoryTotal - Kb("MemAvailable");
                return;
            }
            use.MemoryTotal = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            long rest = 0;
            foreach (AppUse app in use.Apps) rest += app.Memory;
            use.MemoryUsed = Math.Min(rest, use.MemoryTotal);
        }

        private static string Battery()
        {
            if (OperatingSystem.IsMacOS())
            {
                Match found = Regex.Match(Music.Run("pmset", new[] { "-g", "batt" }) ?? "", @"(\d+)%;\s*(\w+)");
                return found.Success ? found.Groups[1].Value + "% and " + found.Groups[2].Value : null;
            }
            try
            {
                foreach (string folder in Directory.Exists("/sys/class/power_supply") ? Directory.GetDirectories("/sys/class/power_supply", "BAT*") : new string[0])
                    return File.ReadAllText(Path.Combine(folder, "capacity")).Trim() + "% and " + File.ReadAllText(Path.Combine(folder, "status")).Trim().ToLowerInvariant();
            }
            catch (Exception) { }
            return null;
        }
    }

    internal static class Tunes
    {
        internal static string AppName(string app)
        {
            string name = (app ?? "").Trim();
            return name.Length == 0 ? "that app" : char.ToUpperInvariant(name[0]) + name.Substring(1);
        }
    }

    internal sealed class AppSound
    {
        internal string App;
        internal int Id;
        internal float Level;
        internal bool Muted;
        internal bool Playing;
    }

    // Each app's own volume, through PulseAudio's or PipeWire's list of what's playing. A Mac has no such list.
    internal static class Mixer
    {
        internal static List<AppSound> Read(string listed)
        {
            List<AppSound> sounds = new List<AppSound>();
            foreach (string block in Regex.Split(listed ?? "", @"(?m)^Sink Input #"))
            {
                Match id = Regex.Match(block, @"^(\d+)");
                if (!id.Success) continue;
                Match level = Regex.Match(block, @"(\d+)%"), name = Regex.Match(block, @"application\.name = ""([^""]*)"""), binary = Regex.Match(block, @"application\.process\.binary = ""([^""]*)""");
                sounds.Add(new AppSound
                {
                    Id = int.Parse(id.Groups[1].Value),
                    App = binary.Success ? binary.Groups[1].Value : name.Success ? name.Groups[1].Value : "app",
                    Level = level.Success ? int.Parse(level.Groups[1].Value) / 100f : 1,
                    Muted = Regex.IsMatch(block, @"(?m)^\s*(?:Mute|Silenciado): (?:yes|sí)"),
                    Playing = !Regex.IsMatch(block, @"(?m)^\s*(?:Corked|Pausado): (?:yes|sí)")
                });
            }
            return sounds;
        }

        internal static List<AppSound> Sounds()
        {
            if (!OperatingSystem.IsLinux()) return null;
            string listed = Music.Run("pactl", new[] { "list", "sink-inputs" });
            return listed == null ? null : Read(listed);
        }

        internal static bool Set(int id, float? level, bool? muted)
        {
            if (level != null) Music.Run("pactl", new[] { "set-sink-input-volume", id.ToString(), (int)Math.Round(level.Value * 100) + "%" });
            if (muted != null) Music.Run("pactl", new[] { "set-sink-input-mute", id.ToString(), muted.Value ? "1" : "0" });
            return true;
        }
    }
}
