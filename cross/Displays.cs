using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace CapriDesktop
{
    internal sealed class DisplayMode
    {
        internal string Id;
        internal int Width, Height;
        internal double Rate;
        internal string Label { get { return Width + " x " + Height + " · " + Math.Round(Rate).ToString(CultureInfo.InvariantCulture) + " Hz"; } }
    }

    internal sealed class Display
    {
        internal string Name;
        internal DisplayMode Now;
        internal List<DisplayMode> Modes = new List<DisplayMode>();
        internal string Place = "0x0", Scale = "1";
    }

    // Her screens' resolutions on Linux, each desktop through its own tool: kscreen-doctor on KDE Plasma, hyprctl on Hyprland,
    // xrandr on X11. Other Wayland desktops and Mac have no tool to ask, so nothing lists there. xrandr isn't asked on Wayland,
    // where it only sees XWayland's made-up screen.
    internal static class Displays
    {
        internal static Func<string, string[], string> Run = delegate(string file, string[] args) { return Music.RunFor(file, args, 10000); };
        internal static Func<string> Kind = Which;
        internal static int Settle = 400;

        private static string Which()
        {
            if (!OperatingSystem.IsLinux()) return null;
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE"))) return "hyprland";
            bool wayland = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") == "wayland" || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
            if (wayland) return (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").IndexOf("KDE", StringComparison.OrdinalIgnoreCase) >= 0 ? "kde" : null;
            return string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) ? null : "x11";
        }

        internal static List<Display> Read()
        {
            try
            {
                string kind = Kind();
                if (kind == "kde") return Kde(Run("kscreen-doctor", new[] { "-j" }));
                if (kind == "hyprland") return Hypr(Run("hyprctl", new[] { "-j", "monitors" }));
                if (kind == "x11") return X(Run("xrandr", new[] { "--query" }));
            }
            catch (Exception) { }
            return new List<Display>();
        }

        // Asks for the mode, then reads it back, since a desktop can say nothing and leave the screen as it was.
        internal static bool Change(Display display, DisplayMode mode)
        {
            string kind = Kind();
            if (kind == "kde") Run("kscreen-doctor", new[] { "output." + display.Name + ".mode." + mode.Id });
            else if (kind == "hyprland") Run("hyprctl", new[] { "keyword", "monitor", display.Name + "," + mode.Width + "x" + mode.Height + "@" + mode.Rate.ToString("F2", CultureInfo.InvariantCulture) + "," + display.Place + "," + display.Scale });
            else if (kind == "x11") Run("xrandr", new[] { "--output", display.Name, "--mode", mode.Width + "x" + mode.Height, "--rate", mode.Id });
            else return false;
            for (int i = 0; i < 6; i++)
            {
                foreach (Display found in Read())
                    if (found.Name == display.Name && found.Now != null && found.Now.Label == mode.Label) return true;
                if (i < 5) Thread.Sleep(Settle);
            }
            return false;
        }

        internal static List<Display> Kde(string json)
        {
            List<Display> found = new List<Display>();
            if (string.IsNullOrEmpty(json)) return found;
            using (JsonDocument parsed = JsonDocument.Parse(json))
            {
                foreach (JsonElement output in parsed.RootElement.GetProperty("outputs").EnumerateArray())
                {
                    if (!output.GetProperty("connected").GetBoolean() || !output.GetProperty("enabled").GetBoolean()) continue;
                    Display display = new Display { Name = output.GetProperty("name").GetString() };
                    string current = Text(output.GetProperty("currentModeId"));
                    foreach (JsonElement mode in output.GetProperty("modes").EnumerateArray())
                    {
                        JsonElement size = mode.GetProperty("size");
                        DisplayMode one = new DisplayMode { Id = Text(mode.GetProperty("id")), Width = size.GetProperty("width").GetInt32(), Height = size.GetProperty("height").GetInt32(), Rate = mode.GetProperty("refreshRate").GetDouble() };
                        display.Modes.Add(one);
                        if (one.Id == current) display.Now = one;
                    }
                    Add(found, display);
                }
            }
            return found;
        }

        internal static List<Display> Hypr(string json)
        {
            List<Display> found = new List<Display>();
            if (string.IsNullOrEmpty(json)) return found;
            using (JsonDocument parsed = JsonDocument.Parse(json))
            {
                foreach (JsonElement monitor in parsed.RootElement.EnumerateArray())
                {
                    Display display = new Display
                    {
                        Name = monitor.GetProperty("name").GetString(),
                        Place = monitor.GetProperty("x").GetInt32() + "x" + monitor.GetProperty("y").GetInt32(),
                        Scale = monitor.GetProperty("scale").GetDouble().ToString("0.##", CultureInfo.InvariantCulture)
                    };
                    int width = monitor.GetProperty("width").GetInt32(), height = monitor.GetProperty("height").GetInt32();
                    double rate = monitor.GetProperty("refreshRate").GetDouble();
                    foreach (JsonElement mode in monitor.GetProperty("availableModes").EnumerateArray())
                    {
                        Match match = Regex.Match(mode.GetString() ?? "", @"^(\d+)x(\d+)@([\d.]+)");
                        if (!match.Success) continue;
                        DisplayMode one = new DisplayMode { Id = match.Value, Width = int.Parse(match.Groups[1].Value), Height = int.Parse(match.Groups[2].Value), Rate = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) };
                        display.Modes.Add(one);
                        if (one.Width == width && one.Height == height && (display.Now == null || Math.Abs(one.Rate - rate) < Math.Abs(display.Now.Rate - rate))) display.Now = one;
                    }
                    Add(found, display);
                }
            }
            return found;
        }

        internal static List<Display> X(string text)
        {
            List<Display> found = new List<Display>();
            if (string.IsNullOrEmpty(text)) return found;
            Display display = null;
            foreach (string line in text.Split('\n'))
            {
                if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
                {
                    if (display != null) Add(found, display);
                    Match head = Regex.Match(line, @"^(\S+) connected");
                    display = head.Success ? new Display { Name = head.Groups[1].Value } : null;
                    continue;
                }
                Match row = Regex.Match(line, @"^\s+(\d+)x(\d+)\s+(.+)$");
                if (display == null || !row.Success) continue;
                foreach (Match rate in Regex.Matches(row.Groups[3].Value, @"([\d.]+)(\*?)"))
                {
                    DisplayMode one = new DisplayMode { Id = rate.Groups[1].Value, Width = int.Parse(row.Groups[1].Value), Height = int.Parse(row.Groups[2].Value), Rate = double.Parse(rate.Groups[1].Value, CultureInfo.InvariantCulture) };
                    display.Modes.Add(one);
                    if (rate.Groups[2].Value == "*") display.Now = one;
                }
            }
            if (display != null) Add(found, display);
            return found;
        }

        private static string Text(JsonElement value) { return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText(); }

        // The biggest and fastest first, one row for each size and rate as she'd show them. A screen with no mode on is off.
        private static void Add(List<Display> found, Display display)
        {
            if (display.Now == null) return;
            display.Modes.Sort(delegate(DisplayMode a, DisplayMode b)
            {
                int size = (b.Width * b.Height).CompareTo(a.Width * a.Height);
                if (size == 0) size = b.Width.CompareTo(a.Width);
                return size != 0 ? size : b.Rate.CompareTo(a.Rate);
            });
            HashSet<string> seen = new HashSet<string> { display.Now.Label };
            display.Modes.RemoveAll(delegate(DisplayMode mode) { return mode != display.Now && !seen.Add(mode.Label); });
            found.Add(display);
        }
    }
}
