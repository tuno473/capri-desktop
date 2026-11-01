using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;

namespace CapriDesktop
{
    // Saving the last stretch of a game: gpu-screen-recorder keeps it in memory and writes it out when she signals, the way
    // Windows' own buffer does. Mac has no such recorder, and without one she says what to install.
    internal sealed class ClipRecorder : IDisposable
    {
        internal const string Missing = "I need gpu-screen-recorder to clip games on Linux. Install it, then ask me again.";
        internal const string NoMac = "I can't clip games on a Mac yet: nothing there keeps the last stretch of the screen ready to save.";
        internal const int Keeps = 120;
        private readonly string folder;
        private Process recorder;

        internal ClipRecorder(string saveTo) { folder = saveTo; }

        internal static string[] Arguments(string folder, string session)
        {
            return new[] { "-w", session == "wayland" ? "portal" : "screen", "-f", "60", "-r", Keeps.ToString(), "-c", "mp4", "-a", "default_output", "-o", folder };
        }

        internal bool Running { get { return recorder != null && !recorder.HasExited; } }

        internal string Start(string tool)
        {
            if (Running) return null;
            if (OperatingSystem.IsMacOS()) return NoMac;
            try
            {
                Directory.CreateDirectory(folder);
                ProcessStartInfo start = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                foreach (string word in Arguments(folder, Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"))) start.ArgumentList.Add(word);
                recorder = Process.Start(start);
                recorder.OutputDataReceived += delegate { };
                recorder.ErrorDataReceived += delegate { };
                recorder.BeginOutputReadLine();
                recorder.BeginErrorReadLine();
                return null;
            }
            catch (System.ComponentModel.Win32Exception) { return Missing; }
        }

        // The newest file it writes after the signal, or null if none came in time.
        internal string Save(int waitMs)
        {
            if (!Running) return null;
            DateTime asked = DateTime.UtcNow;
            Music.Run("kill", new[] { "-USR1", recorder.Id.ToString() });
            Stopwatch wait = Stopwatch.StartNew();
            while (wait.ElapsedMilliseconds < waitMs)
            {
                Thread.Sleep(200);
                string newest = null;
                DateTime when = asked;
                foreach (string file in Directory.GetFiles(folder, "*.mp4"))
                    if (File.GetLastWriteTimeUtc(file) > when) { when = File.GetLastWriteTimeUtc(file); newest = file; }
                if (newest != null) { Thread.Sleep(500); return newest; }
            }
            return null;
        }

        public void Dispose()
        {
            try { if (Running) recorder.Kill(); }
            catch (Exception) { }
            if (recorder != null) recorder.Dispose();
            recorder = null;
        }
    }

    // Her mini player: what's playing, with back, play and next. It sits by her while music plays.
    internal sealed class MiniTunes : Window
    {
        internal const double Wide = 300, Tall = 58;
        private readonly Pane pane;
        internal string Song = "";
        internal Action<int> Pressed;
        internal Action Dismissed;

        internal MiniTunes()
        {
            Look.Overlay(this, "Capri mini player");
            Width = Wide;
            Height = Tall;
            Background = new SolidColorBrush(Color.FromRgb(13, 12, 20));
            pane = new Pane(this);
            Content = pane;
        }

        internal void Show(string song)
        {
            Song = song;
            pane.InvalidateVisual();
            if (IsVisible) return;
            Show();
            Look.OnTopAgain(this);
        }

        internal static int PartAt(Point at)
        {
            if (at.Y < 28) return at.X >= Wide - 26 ? 3 : -1;
            return at.X < Wide / 3 ? 0 : at.X < Wide * 2 / 3 ? 1 : 2;
        }

        private sealed class Pane : Control
        {
            private static readonly string[] Marks = { "<<", "PLAY / PAUSE", ">>" };
            private readonly MiniTunes owner;

            internal Pane(MiniTunes tunes)
            {
                owner = tunes;
                PointerPressed += delegate(object sender, PointerPressedEventArgs e)
                {
                    int part = PartAt(e.GetPosition(this));
                    if (part == 3) { if (owner.Dismissed != null) owner.Dismissed(); }
                    else if (part >= 0 && owner.Pressed != null) owner.Pressed(part);
                };
            }

            public override void Render(DrawingContext graphics)
            {
                graphics.DrawRectangle(new SolidColorBrush(Color.FromRgb(13, 12, 20)), new Pen(new SolidColorBrush(Look.Pink), 1.5), new Rect(0.75, 0.75, Wide - 1.5, Tall - 1.5));
                graphics.DrawText(Look.Text(Look.Cut(owner.Song, 36), Look.Regular, 13, Look.Ink), new Point(8, 6));
                graphics.DrawText(Look.Text("x", Look.Regular, 13, Look.Pink), new Point(Wide - 18, 6));
                for (int part = 0; part < 3; part++)
                {
                    FormattedText mark = Look.Text(Marks[part], Look.Bold, 12, Look.Cyan);
                    graphics.DrawText(mark, new Point(part * Wide / 3 + (Wide / 3 - mark.Width) / 2, 34));
                }
            }
        }
    }

    internal sealed partial class Pet
    {
        private readonly List<string> watchWords = new List<string>();
        private ClipRecorder clips;
        private MiniTunes miniTunes;
        private MenuItem miniTunesItem;
        private bool watchingAlong, gamingMode, tunesChecking;
        private int watchTurn;
        private double nextTunes;

        internal bool WatchingAlong { get { return watchingAlong; } }
        internal bool GamingOn { get { return gamingMode; } }
        internal MiniTunes Tunes { get { return miniTunes; } }

        private void SetUpTogether()
        {
            WatchAlong.Active = delegate { return watchingAlong; };
            Hands.WatchAlong = delegate(string action) { return Dispatcher.UIThread.Invoke(delegate { return Together(action); }); };
            Hands.GamingMode = delegate(string action) { return Dispatcher.UIThread.Invoke(delegate { return GamingTool(action); }); };
            Hands.SaveClip = ClipTool;
            Hands.GameOpen = delegate { return Dispatcher.UIThread.Invoke(delegate { return GameUp ? "game" : null; }); };
        }

        // Watch along: every 20 seconds she looks and listens, and says something only when a moment is worth it.
        internal string Together(string action)
        {
            if (action == "stop")
            {
                if (!watchingAlong) return "We're not watching anything.";
                watchingAlong = false;
                watchTurn++;
                PcEars.Stop();
                return "Okay, I'll stop watching.";
            }
            if (watchingAlong) return "I'm already watching with you.";
            string blind = Unseeing();
            bool canHear = OperatingSystem.IsLinux();
            if (blind != null && !canHear) return blind;
            watchingAlong = true;
            lock (watchWords) watchWords.Clear();
            int turn = ++watchTurn;
            if (Program.Smoke == null) { Thread looking = new Thread(delegate() { WatchLoop(turn, blind == null, canHear); }) { IsBackground = true, Name = "Capri watch-along" }; looking.Start(); }
            return blind != null ? WatchAlong.NoEyes : !canHear ? "I'll watch with you. A Mac won't let me hear what it's playing, so I'm going by the picture." : "Okay, I'm watching with you. Say stop watching when you're done.";
        }

        private void WatchLoop(int turn, bool sees, bool hears)
        {
            while (turn == watchTurn)
            {
                Stopwatch took = Stopwatch.StartNew();
                if (hears && Parakeet.Installed)
                {
                    string words = PcEars.Text(WatchAlong.AudioSeconds * 2, null);
                    if (words.StartsWith("These are the words", StringComparison.Ordinal))
                        lock (watchWords)
                        {
                            watchWords.Add(words.Substring(words.IndexOf('\n') + 1));
                            while (watchWords.Count > WatchAlong.DefaultLines) watchWords.RemoveAt(0);
                        }
                }
                while (turn == watchTurn && took.Elapsed.TotalSeconds < WatchAlong.AskSeconds) Thread.Sleep(200);
                if (turn != watchTurn) return;
                bool free = Dispatcher.UIThread.Invoke(delegate { return !Hushed && !Busy && Talk.Quiet && MoveClock >= voiceDoneAt && !Chores.Running; });
                if (!free || Brain.Model == null) continue;
                try
                {
                    string heard;
                    lock (watchWords) heard = string.Join("\n", watchWords);
                    byte[] picture = sees ? Eyes.Jpeg(Eyes.Shoot(), WatchAlong.PictureSize) : null;
                    if (picture == null && heard.Length == 0) continue;
                    string ask = WatchAlong.Ask(new List<string>(), null, heard, picture != null);
                    string reply = picture != null ? Brain.See(picture, Brain.Persona + Brain.Speech(), ask, WatchAlong.ReplyTokens) : Brain.Aside(Brain.Persona + Brain.Speech(), ask, WatchAlong.ReplyTokens);
                    string saw;
                    string said = Glances.Watched(reply, out saw);
                    if (!string.IsNullOrWhiteSpace(said)) Dispatcher.UIThread.Post(delegate { if (turn == watchTurn && Talk.Quiet) Reply(++asking, "", said); });
                }
                catch (Exception problem) { Brain.Log("watch along", -1, problem.Message); }
            }
        }

        // Gaming Mode here: she goes to a corner, watches the game now and then and reacts. Windows also has her game HUD.
        internal string GamingTool(string action)
        {
            if (action == "off")
            {
                if (!gamingMode) return "Gaming Mode's already off.";
                gamingMode = false;
                watchTurn++;
                return "Gaming Mode's off, choom.";
            }
            if (action == "on" || string.IsNullOrEmpty(action))
            {
                if (gamingMode) return "Gaming Mode's already on.";
                string blind = Unseeing();
                if (blind != null) return blind;
                gamingMode = true;
                int turn = ++watchTurn;
                if (Program.Smoke == null)
                {
                    PixelRect area = Area;
                    Teleport(new PixelPoint(area.Right - (int)(Bounds.Width * RenderScaling / 2) - 8, area.Bottom - 8));
                    Thread looking = new Thread(delegate() { GamingLoop(turn); }) { IsBackground = true, Name = "Capri gaming mode" };
                    looking.Start();
                }
                return "Gaming Mode's on. I'll watch from the corner and keep it short.";
            }
            PixelRect work = Area;
            int step = (int)(work.Height * 0.12);
            if (Program.Smoke == null && action == "up") Teleport(new PixelPoint(Hip.X, Hip.Y - step));
            else if (Program.Smoke == null && action == "down") Teleport(new PixelPoint(Hip.X, Hip.Y + step));
            else if (Program.Smoke == null && action == "left") Teleport(new PixelPoint(Hip.X - step, Hip.Y));
            else if (Program.Smoke == null && action == "right") Teleport(new PixelPoint(Hip.X + step, Hip.Y));
            return "Moved.";
        }

        private void GamingLoop(int turn)
        {
            const string Rules = "\nThey're playing a game and you're watching in Gaming Mode. If something in this picture is worth a reaction, react in under twelve words, about what's actually happening. If not, reply with exactly: skip.";
            while (turn == watchTurn)
            {
                for (int wait = 0; wait < 100 && turn == watchTurn; wait++) Thread.Sleep(200);
                if (turn != watchTurn) return;
                bool free = Dispatcher.UIThread.Invoke(delegate { return !Hushed && Talk.Quiet && MoveClock >= voiceDoneAt && !Chores.Running; });
                if (!free || Brain.Model == null) continue;
                try
                {
                    byte[] picture = Eyes.Jpeg(Eyes.Shoot(), 1280);
                    if (picture == null) continue;
                    string said = Brain.Clean(Brain.See(picture, Brain.Persona + Brain.Speech() + Rules, "React or skip.", 60));
                    if (string.IsNullOrWhiteSpace(said) || said.Trim().TrimEnd('.').Equals("skip", StringComparison.OrdinalIgnoreCase)) continue;
                    Dispatcher.UIThread.Post(delegate { if (turn == watchTurn && Talk.Quiet) Reply(++asking, "", said); });
                }
                catch (Exception problem) { Brain.Log("gaming mode", -1, problem.Message); }
            }
        }

        private string ClipsFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) is string videos && videos.Length > 0 ? videos : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos"), "Capri Clips"); }
        }

        // The first ask starts her buffer, since there's nothing to save until it has been running.
        private string ClipTool(int seconds)
        {
            if (clips == null) clips = new ClipRecorder(ClipsFolder);
            if (!clips.Running)
            {
                string problem = clips.Start("gpu-screen-recorder");
                return problem ?? "I've started keeping the last " + ClipRecorder.Keeps + " seconds. Ask me to clip it any time from now on.";
            }
            string saved = clips.Save(15000);
            return saved == null ? "Clipping that didn't work: the recorder didn't save anything." : "Saved it to " + saved;
        }

        // Windows' mini player: on until they turn it off.
        private void CheckTunes(double now)
        {
            if (Program.Smoke != null || Saved.NoMiniPlayer || tunesChecking || now < nextTunes) return;
            nextTunes = now + 3;
            tunesChecking = true;
            bool mac = OperatingSystem.IsMacOS();
            ThreadPool.QueueUserWorkItem(delegate
            {
                string song = "";
                try { song = Limbs.Song(mac); }
                catch (Exception) { }
                Dispatcher.UIThread.Post(delegate { tunesChecking = false; ShowTunes(song); });
            });
        }

        internal void ShowTunes(string song)
        {
            if (string.IsNullOrEmpty(song) || Away || Saved.NoMiniPlayer) { if (miniTunes != null && miniTunes.IsVisible) miniTunes.Hide(); return; }
            if (miniTunes == null)
            {
                miniTunes = new MiniTunes();
                bool mac = OperatingSystem.IsMacOS();
                miniTunes.Pressed = delegate(int part) { byte[] keys = { 0xB1, 0xB3, 0xB0 }; Limbs.Key(keys[part], mac); nextTunes = 0; };
                miniTunes.Dismissed = ToggleMiniPlayer;
            }
            PixelRect work = Area;
            double scale = RenderScaling > 0 ? RenderScaling : 1;
            int x = Math.Max(work.X, Math.Min(work.Right - (int)(MiniTunes.Wide * scale), Position.X));
            int y = Position.Y - (int)(MiniTunes.Tall * scale) - 4;
            miniTunes.Position = new PixelPoint(x, Math.Max(work.Y, y));
            miniTunes.Show(song);
        }

        internal void ToggleMiniPlayer()
        {
            Saved.NoMiniPlayer = !Saved.NoMiniPlayer;
            if (miniTunesItem != null) miniTunesItem.IsChecked = !Saved.NoMiniPlayer;
            if (Saved.NoMiniPlayer && miniTunes != null) miniTunes.Hide();
            Save();
        }
    }
}
