using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;

namespace CapriDesktop
{
    // Her fake blue screen: five seconds, then it owns up. It touches nothing, and a click or Esc ends it.
    internal sealed class FakeCrash : Window
    {
        internal const double Seconds = 5, RevealAt = 3.7;
        internal const string StopCode = "CHOOM_OVERFLOW", Failed = "capri.sys", Punchline = "jk. you're fine, choom";
        private static readonly string[] Message = { "Your PC ran into a problem and needs to restart. We're", "just collecting some error info, and then we'll restart for", "you." };
        private readonly DispatcherTimer ticks = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        private readonly Sheet sheet;

        internal FakeCrash(PixelRect area, double scale)
        {
            Title = "Capri fake crash";
            SystemDecorations = SystemDecorations.None;
            Background = new SolidColorBrush(Color.FromRgb(0, 120, 215));
            Topmost = true;
            ShowInTaskbar = false;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = area.Position;
            Width = area.Width / scale;
            Height = area.Height / scale;
            sheet = new Sheet(this);
            Content = sheet;
            PointerPressed += delegate { Close(); };
            KeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) Close(); };
            ticks.Tick += delegate
            {
                if (clock.Elapsed.TotalSeconds >= Seconds) { Close(); return; }
                sheet.InvalidateVisual();
            };
            Opened += delegate { ticks.Start(); Activate(); };
        }

        internal bool Revealed { get { return clock.Elapsed.TotalSeconds >= RevealAt; } }

        protected override void OnClosed(EventArgs e)
        {
            ticks.Stop();
            base.OnClosed(e);
        }

        private sealed class Sheet : Control
        {
            private readonly FakeCrash owner;

            internal Sheet(FakeCrash crash) { owner = crash; }

            public override void Render(DrawingContext graphics)
            {
                double s = Math.Max(0.5, owner.Bounds.Height / 1080), left = owner.Bounds.Width * 0.16, top = owner.Bounds.Height * 0.14;
                Color white = Colors.White;
                graphics.DrawText(Look.Text(owner.Revealed ? ":)" : ":(", Look.Regular, 150 * s, owner.Revealed ? Look.Pink : white), new Point(left, top));
                double y = top + 220 * s;
                foreach (string line in Message) { graphics.DrawText(Look.Text(line, Look.Regular, 28 * s, white), new Point(left, y)); y += 40 * s; }
                y += 30 * s;
                graphics.DrawText(Look.Text(owner.Revealed ? Punchline : (int)Math.Min(100, owner.clock.Elapsed.TotalSeconds / RevealAt * 100) + "% complete", Look.Regular, 28 * s, white), new Point(left, y));
                y += 90 * s;
                graphics.DrawText(Look.Text("Stop code: " + StopCode, Look.Regular, 18 * s, white), new Point(left, y));
                graphics.DrawText(Look.Text("What failed: " + Failed, Look.Regular, 18 * s, white), new Point(left, y + 28 * s));
            }
        }
    }

    internal sealed partial class Pet
    {
        private const double ThrowFrom = 1300, ThrowMost = 7000, ThrowDrag = 0.6, WallBounce = 0.55;
        private static readonly string[] ThrowLines = { "WHEEEE!", "Put me DOWN!", "Choom, I'm not a frisbee!" };
        private FakeCrash crash;
        private bool hologram;
        private readonly Queue<KeyValuePair<double, PixelPoint>> dragTrail = new Queue<KeyValuePair<double, PixelPoint>>();
        private double thrownX, thrownStep = -1;

        internal bool Hologram { get { return hologram; } }
        internal FakeCrash CrashShown { get { return crash; } }

        private readonly VirtualMic virtualMic = new VirtualMic();
        private bool riding;
        private PixelPoint? rideWas;

        internal VirtualMic VoiceMic { get { return virtualMic; } }

        // Their mic stays theirs: this adds a "Capri_Mic" to pick in Discord or a game, carrying their mic and her voice.
        internal string SetVoiceInMic(bool on)
        {
            if (!on) { virtualMic.Stop(); Limbs.Mic = null; Saved.VoiceInMic = false; Save(); return "My voice is out of your mic."; }
            string problem = virtualMic.Start();
            if (problem != null) return problem;
            Limbs.Mic = virtualMic;
            Saved.VoiceInMic = true;
            Save();
            return "Pick Capri_Mic as your mic in Discord or your game, and you'll both be on it.";
        }

        // Riding their cursor: she keeps to the mouse until they say hop off or drag her.
        internal string RideTool(bool on)
        {
            if (riding == on) return on ? "Already on your cursor." : "I'm not on your cursor.";
            riding = on;
            rideWas = null;
            if (Desk is KdeDesk) Desk.Chaos(on && cloneRoom == null || !on && cloneRoom != null);
            return on ? "Holding on to your cursor." : "Hopped off.";
        }

        private void CheckRide()
        {
            if (!riding || dragging) { if (riding && dragging) RideTool(false); return; }
            PixelPoint? at = Mouse();
            if (at == null || at == rideWas) return;
            rideWas = at;
            Position = new PixelPoint(at.Value.X - (int)(Bounds.Width * RenderScaling / 2), at.Value.Y + 8);
        }

        private void SetUpHerself()
        {
            Hands.Arranged = delegate(OpenWindow window, string wanted, ToolArguments args)
            {
                PixelRect area = Dispatcher.UIThread.Invoke(delegate { return Area; });
                return CapriDesktop.Arrange.Do(window, wanted, args, Desk, new PixelArea { X = area.X, Y = area.Y, Width = area.Width, Height = area.Height });
            };
            if (Saved.VoiceInMic && Program.Smoke == null) SetVoiceInMic(true);
            Hands.Hologram = delegate(string action) { return Dispatcher.UIThread.Invoke(delegate { return SetHologram(action == "on"); }); };
            Hands.Crash = delegate(string action) { return Dispatcher.UIThread.Invoke(delegate { return CrashTool(action); }); };
            Hands.VoiceChat = delegate(ToolArguments args) { return Dispatcher.UIThread.Invoke(delegate { return VoiceChatTool(args.Action); }); };
            Hands.StopSpeech = delegate { Dispatcher.UIThread.Post(delegate { voice.Stop(); voiceDoneAt = 0; }); return "Stopped talking."; };
            Hands.JobControl = JobTool;
            Hands.Quit = delegate { DispatcherTimer.RunOnce(delegate { Quit(); }, TimeSpan.FromSeconds(3)); return "Quitting once I've said this."; };
            Hands.Mainframe = delegate { Dispatcher.UIThread.Post(delegate { if (Program.Smoke == null) ShowFrame(); }); return "Mainframe's open."; };
            Hands.Updates = delegate(ToolArguments args) { return Dispatcher.UIThread.Invoke(delegate { return UpdatesTool(args.Action); }); };
            Hands.MiniPlayer = delegate(string action) { return Dispatcher.UIThread.Invoke(delegate { return MiniPlayerTool(action); }); };
            Hands.OpenClip = delegate(string action)
            {
                string where = ClipsFolder;
                Directory.CreateDirectory(where);
                string open = where;
                if (action == "open_last")
                {
                    DateTime newest = DateTime.MinValue;
                    foreach (string file in Directory.GetFiles(where, "*.mp4")) if (File.GetLastWriteTimeUtc(file) > newest) { newest = File.GetLastWriteTimeUtc(file); open = file; }
                    if (open == where) return "You don't have any clips yet.";
                }
                if (Program.Smoke == null) Limbs.Launch(OperatingSystem.IsMacOS() ? "open" : "xdg-open", new[] { open });
                return action == "open_last" ? "Opened your last clip." : "Opened your clips.";
            };
        }

        // See-through, with clicks passing through her. The menu, or saying hologram off, brings her back.
        internal string SetHologram(bool on)
        {
            if (hologram == on) return "Hologram mode is already " + (on ? "on" : "off") + ".";
            hologram = on;
            Opacity = on ? 0.55 : 1;
            foreach (Window window in new Window[] { this, Talk.Bubble, Talk.Pill })
            {
                if (on) Look.ClickThrough(window);
                else Look.ClickBack(window);
            }
            if (hologramItem != null) { hologramItem.IsChecked = on; }
            return on ? "Hologram mode on. Clicks go right through me. Say hologram off, or right-click my tray icon, to get me back." : "Hologram mode off.";
        }

        internal string CrashTool(string action)
        {
            if (action == "stop")
            {
                if (crash == null) return "There's no fake blue screen up.";
                crash.Close();
                return "Fake blue screen's gone.";
            }
            if (crash != null) return "It's already up.";
            Screen screen = Screens.ScreenFromPoint(Position) ?? Screens.Primary;
            crash = new FakeCrash(screen != null ? screen.Bounds : new PixelRect(0, 0, 1920, 1080), screen != null ? screen.Scaling : 1);
            crash.Closed += delegate { crash = null; };
            crash.Show();
            return "Showed my fake blue screen. It's gone in five seconds, and nothing on the PC changed.";
        }

        private string VoiceChatTool(string action)
        {
            if (action == "status") return chatting ? (micMuted ? "Voice chat is on with the mic muted." : "Voice chat is on.") : "Voice chat is off.";
            if ((action == "on") == chatting) return "Voice chat is already " + (chatting ? "on" : "off") + ".";
            ToggleChat();
            return action == "on" ? "Voice chat's opening." : "Voice chat is off.";
        }

        private string JobTool(ToolArguments args)
        {
            if (!Chores.Running) return "I'm not on a job right now.";
            if (args.Action == "status") return "Still on it. Last step: " + (Chores.LastStep ?? "just started") + ".";
            if (args.Action == "stop") { Chores.StopAsked = true; Brain.Cancel(); return "Stopping that job."; }
            Chores.Changed = args.Task;
            return "Changed the job: " + args.Task;
        }

        private string UpdatesTool(string action)
        {
            if (action == "install")
            {
                if (updateTag == null) return "There's nothing newer to install.";
                UpdateNow(updateTag, updatePage);
                return "Updating now.";
            }
            if (action == "check") { nextUpdate = 0; return updateItem != null && updateTag != null ? "Capri " + updateTag + " is out." : "Checking for an update."; }
            return updateTag != null ? "Capri " + updateTag + " is out. Ask me to install it." : "I haven't seen anything newer.";
        }

        private string MiniPlayerTool(string action)
        {
            if (action == "hide") { if (miniTunes != null) miniTunes.Hide(); tunesHidden = true; return "Hid my mini player for now."; }
            tunesHidden = false;
            nextTunes = 0;
            return Saved.NoMiniPlayer ? "My mini player is turned off in Settings." : "My mini player shows while something's playing.";
        }

        // Thrown: let go while the cursor's still moving fast and she sails, bounces off the sides and lands. Under the speed she just drops.
        internal void DragSeen(PixelPoint at, double now)
        {
            dragTrail.Enqueue(new KeyValuePair<double, PixelPoint>(now, at));
            while (dragTrail.Count > 2 && now - dragTrail.Peek().Key > 0.08) dragTrail.Dequeue();
        }

        internal static double ThrowSpeed(double fromX, double fromSeconds, double toX, double toSeconds, double scale)
        {
            double seconds = toSeconds - fromSeconds;
            return seconds <= 0 ? 0 : (toX - fromX) / seconds / Math.Max(0.5, scale);
        }

        internal bool LetGoThrow(double now)
        {
            bool thrown = false;
            if (dragTrail.Count >= 2)
            {
                KeyValuePair<double, PixelPoint> first = dragTrail.Peek(), last = first;
                foreach (KeyValuePair<double, PixelPoint> mark in dragTrail) last = mark;
                double speed = ThrowSpeed(first.Value.X, first.Key, last.Value.X, last.Key, RenderScaling);
                if (Math.Abs(speed) >= ThrowFrom)
                {
                    thrownX = Math.Max(-ThrowMost, Math.Min(ThrowMost, speed)) * RenderScaling;
                    thrownStep = now;
                    thrown = true;
                    if (Program.Smoke == null) Reply(++asking, "", ThrowLines[cloneDice.Next(ThrowLines.Length)]);
                }
            }
            dragTrail.Clear();
            return thrown;
        }

        // Sideways while she's in the air; falling itself is her usual drop.
        private void CheckThrown(double now)
        {
            if (thrownStep < 0) return;
            double passed = Math.Min(0.1, now - thrownStep);
            thrownStep = now;
            thrownX *= Math.Pow(ThrowDrag, passed);
            PixelRect area = Area;
            int x = Position.X + (int)(thrownX * passed), wide = (int)(Bounds.Width * RenderScaling);
            if (x < area.X) { x = area.X; thrownX = -thrownX * WallBounce; }
            else if (x + wide > area.Right) { x = area.Right - wide; thrownX = -thrownX * WallBounce; }
            Position = new PixelPoint(x, Position.Y);
            if (!falling || Math.Abs(thrownX) < 40) thrownStep = -1;
        }
    }
}
