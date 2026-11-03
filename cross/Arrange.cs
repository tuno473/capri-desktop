using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace CapriDesktop
{
    // Moving, resizing and snapping an app's window: KWin's scripting on KDE, Hyprland's own dispatch, wmctrl on other X11
    // desktops and System Events on a Mac.
    internal static class Arrange
    {
        private static int N(string text, int fallback)
        {
            int value;
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        private static string Quoted(string text) { return (text ?? "").Replace("\\", "\\\\").Replace("'", "\\'"); }

        // The script for KWin: the window whose app or title matches, top one first, and what to do to it.
        internal static string KdeScript(string want, string action, ToolArguments a, string tag)
        {
            string act;
            switch (action)
            {
                case "maximize": act = "w.setMaximize(true, true);"; break;
                case "restore": act = "w.setMaximize(false, false); w.minimized = false;"; break;
                case "pin_on_top": act = "w.keepAbove = true;"; break;
                case "unpin": act = "w.keepAbove = false;"; break;
                case "move_to_screen": act = "var s = workspace.screens, i = s.indexOf(w.output); workspace.sendClientToScreen(w, s[(i + 1) % s.length]);"; break;
                case "snap_left": act = "var r = workspace.clientArea(KWin.MaximizeArea, w); w.setMaximize(false, false); w.frameGeometry = { x: r.x, y: r.y, width: r.width / 2, height: r.height };"; break;
                case "snap_right": act = "var r = workspace.clientArea(KWin.MaximizeArea, w); w.setMaximize(false, false); w.frameGeometry = { x: r.x + r.width / 2, y: r.y, width: r.width / 2, height: r.height };"; break;
                case "snap_top": act = "var r = workspace.clientArea(KWin.MaximizeArea, w); w.setMaximize(false, false); w.frameGeometry = { x: r.x, y: r.y, width: r.width, height: r.height / 2 };"; break;
                case "snap_bottom": act = "var r = workspace.clientArea(KWin.MaximizeArea, w); w.setMaximize(false, false); w.frameGeometry = { x: r.x, y: r.y + r.height / 2, width: r.width, height: r.height / 2 };"; break;
                case "move":
                    act = "var g = w.frameGeometry, r = workspace.clientArea(KWin.MaximizeArea, w); w.frameGeometry = { x: " + (a != null && a.Position == "center" ? "r.x + (r.width - g.width) / 2" : (a == null ? "g.x" : N(a.Left, int.MinValue) == int.MinValue ? "g.x" : a.Left)) + ", y: " + (a != null && a.Position == "center" ? "r.y + (r.height - g.height) / 2" : (a == null ? "g.y" : N(a.Top, int.MinValue) == int.MinValue ? "g.y" : a.Top)) + ", width: g.width, height: g.height };";
                    break;
                case "resize":
                    act = "var g = w.frameGeometry; w.frameGeometry = { x: g.x, y: g.y, width: " + (a == null || N(a.Width, -1) <= 0 ? "g.width" : a.Width) + ", height: " + (a == null || N(a.Height, -1) <= 0 ? "g.height" : a.Height) + " };";
                    break;
                default: return null;
            }
            return "var want = '" + Quoted((want ?? "").ToLowerInvariant()) + "', w = null, list = workspace.stackingOrder;\n" +
                "for (var i = list.length - 1; i >= 0 && !w; i--) { var c = list[i]; if ((c.normalWindow || c.dialog) && !c.deleted && (String(c.resourceClass).toLowerCase().indexOf(want) >= 0 || String(c.caption).toLowerCase().indexOf(want) >= 0)) w = c; }\n" +
                "if (w) { " + act + " print('" + tag + " ok'); } else print('" + tag + " none');\n";
        }

        internal static string[] WmctrlFor(string handle, string action, ToolArguments a)
        {
            switch (action)
            {
                case "maximize": return new[] { "-i", "-r", handle, "-b", "add,maximized_vert,maximized_horz" };
                case "restore": return new[] { "-i", "-r", handle, "-b", "remove,maximized_vert,maximized_horz" };
                case "pin_on_top": return new[] { "-i", "-r", handle, "-b", "add,above" };
                case "unpin": return new[] { "-i", "-r", handle, "-b", "remove,above" };
                case "move": return new[] { "-i", "-r", handle, "-e", "0," + N(a == null ? null : a.Left, -1) + "," + N(a == null ? null : a.Top, -1) + ",-1,-1" };
                case "resize": return new[] { "-i", "-r", handle, "-e", "0,-1,-1," + N(a == null ? null : a.Width, -1) + "," + N(a == null ? null : a.Height, -1) };
                default: return null;
            }
        }

        // Her words for how it went, or null when this desktop can't do it.
        internal static string Do(OpenWindow window, string wanted, ToolArguments args, Desk desk, PixelArea work)
        {
            string action = args.Action, done = Past(action, wanted);
            if (desk is KdeDesk)
            {
                bool ok = KWinOnce(KdeScript(string.IsNullOrEmpty(window.Program) ? wanted : window.Program, action, args, "CAPRIARRANGE" + Guid.NewGuid().ToString("N").Substring(0, 10)));
                return ok ? done : "Couldn't find " + wanted + "'s window to move it.";
            }
            string handle = "0x" + window.Handle.ToInt64().ToString("x8");
            if (OperatingSystem.IsMacOS())
            {
                string bounds = action == "maximize" ? work.X + "," + work.Y + "," + work.Right + "," + work.Bottom
                    : action == "snap_left" ? work.X + "," + work.Y + "," + (work.X + work.Width / 2) + "," + work.Bottom
                    : action == "snap_right" ? (work.X + work.Width / 2) + "," + work.Y + "," + work.Right + "," + work.Bottom
                    : action == "snap_top" ? work.X + "," + work.Y + "," + work.Right + "," + (work.Y + work.Height / 2)
                    : action == "snap_bottom" ? work.X + "," + (work.Y + work.Height / 2) + "," + work.Right + "," + work.Bottom : null;
                if (bounds == null) return null;
                string[] edge = bounds.Split(',');
                string script = "tell application \"System Events\" to tell process \"" + window.Program.Replace("\"", "") + "\" to tell window 1\nset position to {" + edge[0] + ", " + edge[1] + "}\nset size to {" + (int.Parse(edge[2]) - int.Parse(edge[0])) + ", " + (int.Parse(edge[3]) - int.Parse(edge[1])) + "}\nend tell";
                return Music.Run("osascript", new[] { "-e", script }) != null ? done : "macOS didn't let me move it. Allow Capri under System Settings > Privacy & Security > Accessibility.";
            }
            if (action.StartsWith("snap_", StringComparison.Ordinal))
            {
                string half = action == "snap_left" ? work.X + "," + work.Y + "," + work.Width / 2 + "," + work.Height
                    : action == "snap_right" ? (work.X + work.Width / 2) + "," + work.Y + "," + work.Width / 2 + "," + work.Height
                    : action == "snap_top" ? work.X + "," + work.Y + "," + work.Width + "," + work.Height / 2
                    : work.X + "," + (work.Y + work.Height / 2) + "," + work.Width + "," + work.Height / 2;
                Music.Run("wmctrl", new[] { "-i", "-r", handle, "-b", "remove,maximized_vert,maximized_horz" });
                return Music.Run("wmctrl", new[] { "-i", "-r", handle, "-e", "0," + half }) != null ? done : "Couldn't move " + wanted + ". Moving windows here needs wmctrl.";
            }
            string[] words = WmctrlFor(handle, action, args);
            if (words == null) return null;
            return Music.Run("wmctrl", words) != null ? done : "Couldn't move " + wanted + ". Moving windows here needs wmctrl.";
        }

        private static string Past(string action, string wanted)
        {
            switch (action)
            {
                case "maximize": return "Maximized " + wanted + ".";
                case "restore": return "Restored " + wanted + ".";
                case "pin_on_top": return "Pinned " + wanted + " on top.";
                case "unpin": return "Unpinned " + wanted + ".";
                case "move": return "Moved " + wanted + ".";
                case "resize": return "Resized " + wanted + ".";
                case "move_to_screen": return "Moved " + wanted + " to your other screen.";
                default: return "Snapped " + wanted + ".";
            }
        }

        // Runs a script once and reads whether it found the window.
        private static bool KWinOnce(string source)
        {
            Match tagged = Regex.Match(source, @"print\('(CAPRIARRANGE\w+) ok'\)");
            if (!tagged.Success) return false;
            string tag = tagged.Groups[1].Value, path = Path.Combine(Path.GetTempPath(), tag.ToLowerInvariant() + ".js");
            try
            {
                File.WriteAllText(path, source);
                string loaded = Music.Run("dbus-send", new[] { "--session", "--print-reply", "--dest=org.kde.KWin", "/Scripting", "org.kde.kwin.Scripting.loadScript", "string:" + path, "string:" + tag });
                Match id = Regex.Match(loaded ?? "", @"int32\s+(\d+)");
                if (!id.Success) return false;
                Music.Run("dbus-send", new[] { "--session", "--print-reply", "--dest=org.kde.KWin", "/Scripting/Script" + id.Groups[1].Value, "org.kde.kwin.Script.run" });
                bool found = false;
                for (int i = 0; i < 12 && !found; i++)
                {
                    System.Threading.Thread.Sleep(60);
                    found = (Music.Run("journalctl", new[] { "--user", "--since", "-10s", "-o", "cat", "--grep", tag + " ok" }) ?? "").IndexOf(tag, StringComparison.Ordinal) >= 0;
                }
                Music.Run("dbus-send", new[] { "--session", "--print-reply", "--dest=org.kde.KWin", "/Scripting", "org.kde.kwin.Scripting.unloadScript", "string:" + tag });
                return found;
            }
            finally
            {
                try { File.Delete(path); }
                catch (IOException) { }
            }
        }
    }

    internal struct PixelArea
    {
        internal int X, Y, Width, Height;
        internal int Right { get { return X + Width; } }
        internal int Bottom { get { return Y + Height; } }
    }

    // Her voice in their mic for Discord or games: a virtual mic made of a null sink and their real mic looped into it, removed
    // when it's turned off. Mac needs an audio driver for this, which she never installs.
    internal sealed class VirtualMic
    {
        internal const string Sink = "capri_mic";
        internal const string NoMac = "On a Mac that takes a virtual audio driver like BlackHole, which I never install.";
        private readonly List<string> modules = new List<string>();

        internal bool On { get { return modules.Count > 0; } }

        internal static string[][] Commands(string sink)
        {
            return new[]
            {
                new[] { "load-module", "module-null-sink", "sink_name=" + sink, "sink_properties=device.description=Capri_Mix" },
                new[] { "load-module", "module-remap-source", "master=" + sink + ".monitor", "source_name=" + sink + "_out", "source_properties=device.description=Capri_Mic" },
                new[] { "load-module", "module-loopback", "source=@DEFAULT_SOURCE@", "sink=" + sink, "latency_msec=30", "source_dont_move=false" }
            };
        }

        // Null when it's up; otherwise why not.
        internal string Start()
        {
            if (On) return null;
            if (OperatingSystem.IsMacOS()) return NoMac;
            foreach (string[] command in Commands(Sink))
            {
                string id = Music.Run("pactl", command);
                if (id == null || !int.TryParse(id.Trim(), out _)) { Stop(); return "My virtual mic wouldn't start: this needs pactl and PipeWire or PulseAudio."; }
                modules.Add(id.Trim());
            }
            return null;
        }

        internal void Stop()
        {
            for (int i = modules.Count - 1; i >= 0; i--) Music.Run("pactl", new[] { "unload-module", modules[i] });
            modules.Clear();
        }

        internal bool Say(string wavFile)
        {
            return On && Music.RunFor("paplay", new[] { "--device=" + Sink, wavFile }, 120000) != null;
        }
    }
}
