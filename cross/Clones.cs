using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;

namespace CapriDesktop
{
    // Esc from anywhere. Keyboards they can read tell her straight away and leave the key alone; otherwise whatever the desk has.
    internal static class EscKey
    {
        private const uint GetKeys = 0x80604518;
        [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags);
        [DllImport("libc", SetLastError = true)] private static extern int ioctl(int fd, uint request, byte[] state);

        internal static Func<bool> Find(Desk desk)
        {
            Func<bool> keyboards = OperatingSystem.IsLinux() ? Keyboards() : null;
            return keyboards ?? (desk == null ? null : desk.EscDown);
        }

        private static Func<bool> Keyboards()
        {
            List<int> found = new List<int>();
            try
            {
                const string folder = "/dev/input/by-path";
                if (Directory.Exists(folder))
                    foreach (string path in Directory.GetFiles(folder, "*-event-kbd"))
                    {
                        int fd = open(path, 0x800);
                        if (fd >= 0) found.Add(fd);
                    }
            }
            catch (Exception) { }
            if (found.Count == 0) return null;
            byte[] keys = new byte[96];
            return delegate
            {
                foreach (int fd in found)
                    if (ioctl(fd, GetKeys, keys) >= 0 && (keys[0] & 2) != 0) return true;
                return false;
            };
        }
    }

    // One see-through, click-through layer over her screen draws them all, like Windows' CloneWindow.
    internal sealed class CloneRoom : Window
    {
        internal const double EscHold = 0.7;
        private const int Fades = 6, SlowTick = 110, CardWidth = 250, Tick = 66;
        internal const string Hint = "hold esc to clear us";
        internal const string HintWithoutEsc = "right-click me to clear us";
        private readonly PixelRect room;
        private readonly Stopwatch clock;
        private readonly DispatcherTimer ticks = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Tick) };
        private readonly Clip idle, walk, grin, glitch;
        private readonly double size;
        private readonly Desk desk;
        private readonly Face face;
        private readonly Dictionary<string, FormattedText> cards = new Dictionary<string, FormattedText>();
        private readonly Dictionary<long, PixelPoint> homes = new Dictionary<long, PixelPoint>();
        private readonly Dictionary<long, PixelRect> reach = new Dictionary<long, PixelRect>();
        private readonly HashSet<long> placed = new HashSet<long>(), stuck = new HashSet<long>(), held = new HashSet<long>();
        private Thread escWatch;
        private volatile bool closing, tapped;
        private volatile int heldMs = -1;
        private double last, hintUntil = -1, allowedAt, tickedAt;
        private long took;
        private bool cleared;
        internal readonly Swarm Swarm;
        internal Action Cleared, Failed;
        internal Action<double> Ticked;
        internal Func<bool> HandsFree = delegate { return false; };
        internal Func<bool> EscDown;
        internal Func<PixelPoint?> Cursor = delegate { return null; };
        internal Action<PixelPoint> Warp = delegate { };
        // Windows they may grab with their frames, or null for none, like over a game.
        internal Func<List<Spot>> Grabbable = delegate { return null; };
        internal Func<bool> Allowed = delegate { return true; };
        internal int Frames;

        internal CloneRoom(PixelRect area, double scale, Stopwatch time, Clip idling, Clip walking, Clip grinning, Clip glitching, double herSize, PixelRect work, Desk theDesk, int seed)
        {
            room = area;
            clock = time;
            idle = idling;
            walk = walking;
            grin = grinning;
            glitch = glitching;
            size = herSize * scale;
            desk = theDesk;
            Look.Overlay(this, "Capri clones");
            Position = area.Position;
            Width = area.Width / scale;
            Height = area.Height / scale;
            IsHitTestVisible = false;
            face = new Face(this);
            Content = face;
            Swarm = new Swarm(area.Width, area.Height, seed, time.Elapsed.TotalSeconds);
            Swarm.Windows = Offered;
            Swarm.WorkLeft = Math.Max(0, work.X - area.X);
            Swarm.WorkTop = Math.Max(0, work.Y - area.Y);
            Swarm.WorkRight = Math.Min(area.Width, work.Right - area.X);
            Swarm.WorkBottom = Math.Min(area.Height, work.Bottom - area.Y);
            ticks.Tick += delegate
            {
                try { Advance(clock.Elapsed.TotalSeconds); }
                catch (Exception problem) { ticks.Stop(); Brain.Log("clones", -1, problem.Message); Action failed = Failed; if (failed != null) failed(); }
            };
            Opened += delegate { Look.ClickThrough(this); };
        }

        internal bool Hinting { get { return hintUntil > last; } }

        internal void Begin(double now)
        {
            last = tickedAt = now;
            Show();
            Look.OnTopAgain(this);
            ticks.Start();
            escWatch = new Thread(WatchEsc);
            escWatch.IsBackground = true;
            escWatch.Name = "Capri clone Esc";
            escWatch.Start();
        }

        private void WatchEsc()
        {
            Stopwatch down = new Stopwatch();
            while (!closing)
            {
                bool pressed;
                try { Func<bool> esc = EscDown; pressed = esc != null && esc(); }
                catch (Exception) { pressed = false; }
                if (pressed)
                {
                    if (!down.IsRunning) down.Start();
                    heldMs = (int)down.ElapsedMilliseconds;
                    if (down.ElapsedMilliseconds >= EscHold * 1000)
                    {
                        Dispatcher.UIThread.Post(Clear);
                        return;
                    }
                }
                else if (down.IsRunning)
                {
                    tapped = true;
                    down.Reset();
                    heldMs = -1;
                }
                Thread.Sleep(30);
            }
        }

        private void Clear()
        {
            if (cleared) return;
            cleared = true;
            ticks.Stop();
            Action done = Cleared;
            if (done != null) done();
        }

        internal void Spawn(int count, PixelPoint at, bool multiplying)
        {
            Swarm.SpawnX = at.X - room.X;
            Swarm.SpawnY = at.Y - room.Y;
            Swarm.Pending = (int)Math.Min(int.MaxValue, (long)Swarm.Pending + count);
            Swarm.Multiplying |= multiplying;
        }

        internal void Pin(string text, PixelPoint at)
        {
            Swarm.Notes.Add(new CloneNote { X = Math.Max(160, Math.Min(room.Width - 160, at.X - room.X)), Y = Math.Max(40, at.Y - room.Y), Text = text });
        }

        internal void Say(CloneBody body, string line, double until)
        {
            body.Line = line;
            body.LineUntil = until;
        }

        internal void Advance(double now)
        {
            double passed = Math.Max(0, Math.Min(0.25, now - last));
            last = now;
            if (tapped) { tapped = false; hintUntil = now + 3; }
            Stopwatch busy = Stopwatch.StartNew();
            PixelPoint? seen = Cursor();
            System.Drawing.Point cursor = seen == null ? new System.Drawing.Point(-1, -1) : new System.Drawing.Point(seen.Value.X - room.X, seen.Value.Y - room.Y);
            bool handsFree = HandsFree();
            if (Swarm.Hauls.Count > 0 && now >= allowedAt)
            {
                allowedAt = now + 3;
                if (!Allowed()) foreach (CloneHaul dropped in Swarm.Hauls) dropped.Until = 0;
            }
            foreach (CloneHaul haul in Swarm.Hauls) if (!haul.Holding) Track(haul);
            // A cursor she can't see or can't move can't be stolen, so one of them never runs for it.
            System.Drawing.Point? dragged = Swarm.Tick(now, passed, took < SlowTick, handsFree && seen != null, cursor, handsFree);
            if (dragged != null) Warp(new PixelPoint(room.X + dragged.Value.X, room.Y + dragged.Value.Y));
            foreach (CloneHaul haul in Swarm.Hauls) if (haul.Holding) Carry(haul);
            LetGo();
            face.InvalidateVisual();
            // How late this tick came counts too: Avalonia draws after this returns, so a slow frame shows up as a late tick.
            long late = (long)((now - tickedAt) * 1000) - Tick;
            tickedAt = now;
            took = Math.Max(busy.ElapsedMilliseconds, late);
            Frames++;
            Action<double> ticked = Ticked;
            if (ticked != null) ticked(now);
        }

        private List<CloneHaul> Offered()
        {
            List<CloneHaul> offered = new List<CloneHaul>();
            List<Spot> windows = Grabbable();
            if (windows == null || desk == null) return offered;
            foreach (Spot window in windows)
            {
                PixelRect frame = window.Bounds;
                if (!window.Seat || stuck.Contains(window.Window) || !room.Contains(new PixelPoint(frame.X + frame.Width / 2, frame.Y + frame.Height / 2))) continue;
                offered.Add(new CloneHaul { Window = new IntPtr(window.Window), X = frame.X - room.X, Y = frame.Y - room.Y, Width = frame.Width, Height = frame.Height, Keep = Swarm.KeepShowing * RenderScaling });
            }
            return offered;
        }

        private void Track(CloneHaul haul)
        {
            Spot spot = desk.Find(haul.Window.ToInt64());
            if (spot == null || !spot.Seat) { haul.Until = 0; return; }
            haul.X = spot.Bounds.X - room.X;
            haul.Y = spot.Bounds.Y - room.Y;
            haul.Width = spot.Bounds.Width;
            haul.Height = spot.Bounds.Height;
        }

        private void Carry(CloneHaul haul)
        {
            long window = haul.Window.ToInt64();
            Spot spot = desk.Find(window);
            if (spot == null || !spot.Seat) { haul.Until = 0; return; }
            PixelPoint to = new PixelPoint(room.X + (int)Math.Round(haul.X), room.Y + (int)Math.Round(haul.Y));
            PixelPoint goal = new PixelPoint(room.X + (int)Math.Round(haul.By.ToX - haul.GrabX), room.Y + (int)Math.Round(haul.By.ToY - haul.GrabY));
            if (!haul.Raised && !Theirs(window, spot.Bounds.Position))
            {
                homes[window] = spot.Bounds.Position;
                reach[window] = new PixelRect(spot.Bounds.Position, new PixelSize(1, 1));
            }
            double far = Math.Sqrt((goal.X - to.X) * (double)(goal.X - to.X) + (goal.Y - to.Y) * (double)(goal.Y - to.Y));
            if (!desk.Carry(window, to, goal, far / Swarm.Heavy))
            {
                stuck.Add(window);
                if (!placed.Contains(window)) { homes.Remove(window); reach.Remove(window); }
                haul.Until = 0;
                return;
            }
            haul.Raised = true;
            placed.Add(window);
            held.Add(window);
            // A desk that glides by itself gets there on its own clock, so the whole way counts as hers.
            PixelRect path;
            reach.TryGetValue(window, out path);
            reach[window] = path.Union(new PixelRect(to, new PixelSize(1, 1))).Union(new PixelRect(goal, new PixelSize(1, 1)));
        }

        private void LetGo()
        {
            if (held.Count == 0) return;
            foreach (long window in new List<long>(held))
            {
                if (Swarm.Hauls.Exists(delegate(CloneHaul haul) { return haul.Window.ToInt64() == window; })) continue;
                held.Remove(window);
                desk.Halt(window);
            }
        }

        private bool Theirs(long window, PixelPoint at)
        {
            PixelRect path;
            if (!reach.TryGetValue(window, out path)) return false;
            int slack = (int)(24 * RenderScaling);
            return new PixelRect(path.X - slack, path.Y - slack, path.Width + slack * 2, path.Height + slack * 2).Contains(at);
        }

        // Every window they moved goes back where it was, unless it's since been moved by hand, closed or made full size.
        internal void PutBack()
        {
            if (desk == null) return;
            foreach (long window in held) desk.Halt(window);
            held.Clear();
            foreach (KeyValuePair<long, PixelPoint> home in homes)
            {
                Spot spot = desk.Find(home.Key);
                if (!placed.Contains(home.Key) || spot == null || !spot.Seat || !Theirs(home.Key, spot.Bounds.Position)) continue;
                desk.Place(home.Key, home.Value);
            }
            homes.Clear();
            reach.Clear();
            placed.Clear();
        }

        protected override void OnClosed(EventArgs e)
        {
            closing = true;
            ticks.Stop();
            base.OnClosed(e);
        }

        private FormattedText Card(string text)
        {
            FormattedText card;
            if (cards.TryGetValue(text, out card)) return card;
            if (cards.Count > 400) cards.Clear();
            card = Look.Text(text, Look.Bold, 15, Color.FromRgb(244, 244, 248));
            card.MaxTextWidth = CardWidth;
            cards[text] = card;
            return card;
        }

        private static readonly SolidColorBrush Back = new SolidColorBrush(Color.FromArgb(238, 16, 14, 24)), Ink = new SolidColorBrush(Color.FromArgb(255, 255, 45, 120));
        private static readonly Pen CardEdge = new Pen(Ink, 2);
        private static readonly SolidColorBrush[] PrintInk = MakePrints();

        private static SolidColorBrush[] MakePrints()
        {
            SolidColorBrush[] made = new SolidColorBrush[Fades * 2];
            for (int i = 0; i < Fades; i++)
            {
                byte alpha = (byte)(200 - i * 200 / Fades);
                made[i] = new SolidColorBrush(Color.FromArgb(alpha, 255, 45, 120));
                made[Fades + i] = new SolidColorBrush(Color.FromArgb(alpha, 60, 235, 235));
            }
            return made;
        }

        private Rect DrawCard(DrawingContext graphics, string text, double middleX, double bottomY)
        {
            FormattedText card = Card(text);
            double broad = card.Width + 22, high = card.Height + 16;
            double x = Math.Max(4, Math.Min(room.Width - broad - 4, middleX - broad / 2)), y = Math.Max(4, bottomY - high);
            Rect box = new Rect(x, y, broad, high);
            graphics.DrawRectangle(Back, CardEdge, box);
            graphics.DrawText(card, new Point(x + 11, y + 8));
            return box;
        }

        private void DrawBody(DrawingContext graphics, CloneBody body, double now)
        {
            double age = now - body.Born;
            bool grinning = (body == Swarm.Nabber && Swarm.Holding) || (body.Hauling != null && body.Hauling.Holding);
            Clip clip = age < Swarm.GlitchIn ? glitch : grinning ? grin : Swarm.Walking(body, now) ? walk : idle;
            int cell = clip == glitch ? Math.Min(clip.Count - 1, (int)(age * clip.Fps)) : (int)((now + body.Phase) * clip.Fps) % clip.Count;
            double tall = size * clip.Height, wide = tall * clip.Wide / clip.Strip.PixelSize.Height;
            Rect source = new Rect(cell * clip.Wide, 0, clip.Wide, clip.Strip.PixelSize.Height);
            Rect place = new Rect(body.X - wide / 2 + clip.ShiftX * size, body.Y - tall / 2 + clip.ShiftY * size, wide, tall);
            if (!body.Left) { graphics.DrawImage(clip.Strip, source, place); return; }
            using (graphics.PushTransform(Matrix.CreateTranslation(-body.X, 0) * Matrix.CreateScale(-1, 1) * Matrix.CreateTranslation(body.X, 0)))
                graphics.DrawImage(clip.Strip, source, place);
        }

        private void Paint(DrawingContext graphics)
        {
            double now = last;
            foreach (ClonePrint print in Swarm.Prints)
            {
                int fade = Math.Max(0, Math.Min(Fades - 1, (int)((now - print.Born) / Swarm.PrintLife * Fades)));
                graphics.FillRectangle(PrintInk[(print.Cyan ? Fades : 0) + fade], new Rect((int)print.X, (int)print.Y, 8, 3));
            }
            foreach (CloneNote note in Swarm.Notes)
            {
                Rect box = DrawCard(graphics, note.Text, note.X, note.Y + Card(note.Text).Height / 2 + 8);
                note.Stale = false;
                if (note.BarFrom < 0) continue;
                graphics.FillRectangle(Back, new Rect(box.X, box.Bottom, box.Width, 10));
                graphics.FillRectangle(Ink, new Rect(box.X + 2, box.Bottom + 2, (box.Width - 4) * Math.Min(0.99, (now - note.BarFrom) / note.BarFor), 6));
            }
            foreach (CloneBody body in Swarm.Bodies) DrawBody(graphics, body, now);
            foreach (CloneBody body in Swarm.Bodies)
            {
                if (body.Line == null) continue;
                if (now >= body.LineUntil) { body.Line = null; continue; }
                DrawCard(graphics, body.Line, body.X, body.Y - size / 2);
            }
            int holding = heldMs;
            if (holding >= 0)
            {
                graphics.FillRectangle(Back, new Rect(room.Width / 2 - 124, 20, 248, 14));
                graphics.FillRectangle(Ink, new Rect(room.Width / 2 - 120, 24, 240 * Math.Min(1, holding / (EscHold * 1000)), 6));
            }
            if (now < hintUntil) DrawCard(graphics, EscDown == null ? HintWithoutEsc : Hint, room.Width / 2.0, 90);
        }

        private sealed class Face : Control
        {
            private readonly CloneRoom owner;

            internal Face(CloneRoom room) { owner = room; }

            public override void Render(DrawingContext graphics)
            {
                double scale = owner.RenderScaling > 0 ? owner.RenderScaling : 1;
                using (graphics.PushTransform(Matrix.CreateScale(1 / scale, 1 / scale))) owner.Paint(graphics);
            }
        }
    }

    internal sealed partial class Pet
    {
        internal const string CloneAsk = "You've cloned yourself on their desktop, and there are {0} of you on screen right now. Write what you all say to each other next, the way you really talk: funny, a bit rude, arguing over who's the real one or what to do with their PC. Two to four lines, each under ten words. Put each line on its own row as its speaker's number, a colon, then the words, like 1: words, with numbers from 1 to {0}. Nothing else.";
        private readonly Stopwatch cloneClock = Stopwatch.StartNew();
        private readonly Random cloneDice = new Random();
        private CloneRoom cloneRoom;
        private MenuItem clearClonesItem, upsetItem;
        private bool cloneTalking;
        private double cloneTalkAt;
        private int cloneTurn, upsets;
        private string cloneLast;
        private volatile bool warping;

        internal CloneRoom Clones { get { return cloneRoom; } }
        internal double CloneNow { get { return cloneClock.Elapsed.TotalSeconds; } }
        private double MoveClock { get { return cloneClock.Elapsed.TotalSeconds; } }

        // A window's process for a by-hand test run on a real desktop: her smoke test then has KWin drag only that window.
        internal static int RealChaos;

        private void SetUpClones()
        {
            Hands.Clone = delegate(string action, int count) { return Dispatcher.UIThread.Invoke(delegate { return Clone(action, count); }); };
            Hands.Upset = delegate { return Program.Smoke == null && Dispatcher.UIThread.Invoke(delegate { return Upset(cloneDice.Next(3)); }); };
            Hands.Cloning = delegate { return cloneRoom != null; };
            Screens.Changed += delegate { Dispatcher.UIThread.Post(ClonesOff); };
        }

        internal void ToggleUpsetChaos()
        {
            Saved.NoUpsetChaos = !Saved.NoUpsetChaos;
            upsetItem.IsChecked = !Saved.NoUpsetChaos;
            Save();
        }

        // Full screen windows mean a game or a film, and they're left alone.
        private bool GameUp
        {
            get
            {
                try { return Desk.Covering(Desk.Windows(new long[0])); }
                catch (Exception) { return false; }
            }
        }

        // An insult sets her clones off 1 time in 3, then 2 in 3, and always by the third. Never over a job, a game or a stream.
        internal bool Upset(int roll)
        {
            if (Saved.NoUpsetChaos || Chores.Running || Away || cloneRoom != null || Hands.Streaming || GameUp) return false;
            upsets++;
            if (roll >= upsets) return false;
            upsets = 0;
            Clone("spawn", 0);
            if (cloneRoom != null) cloneRoom.Pin(Swarm.Sorry, new PixelPoint(Position.X + (int)(Bounds.Width * RenderScaling / 2), Position.Y - 24));
            return true;
        }

        private string ClearWords { get { return cloneRoom != null && cloneRoom.EscDown == null ? "right-click me and pick Clear her clones" : "hold Esc"; } }

        // A number makes exactly that many. No number makes one that keeps multiplying until they stop it.
        internal string Clone(string action, int count)
        {
            if (action == "stop")
            {
                int gone = cloneRoom == null ? 0 : cloneRoom.Swarm.Bodies.Count + cloneRoom.Swarm.Pending;
                ClonesOff();
                return gone == 0 ? "I don't have any clones out." : gone == 1 ? "Got rid of my clone." : "Got rid of all " + gone + " of my clones.";
            }
            double now = cloneClock.Elapsed.TotalSeconds;
            PixelPoint centre = new PixelPoint(Position.X + (int)(Bounds.Width * RenderScaling / 2), Position.Y + (int)(Bounds.Height * RenderScaling / 2));
            if (cloneRoom == null) OpenClones(now, centre);
            bool freeForAll = count <= 0;
            cloneRoom.Spawn(freeForAll ? 1 : count, centre, freeForAll);
            if (Program.Smoke == null) Hands.Animate("glitch");
            return freeForAll ? "One of me wasn't enough, choom. We'll keep multiplying, so " + ClearWords + " when you've had enough."
                : count == 1 ? "There's two of me now, choom. To clear her, " + ClearWords + "."
                : "There's " + count + " more of me now, choom. To clear them, " + ClearWords + ".";
        }

        private void OpenClones(double now, PixelPoint centre)
        {
            Screen screen = Screens.ScreenFromPoint(centre) ?? Screens.Primary;
            PixelRect area = screen != null ? screen.Bounds : new PixelRect(0, 0, 1920, 1080), work = screen != null ? screen.WorkingArea : area;
            if (Program.Smoke == null || RealChaos != 0) Desk.Chaos(true);
            CloneRoom opened = new CloneRoom(area, screen != null ? screen.Scaling : 1, cloneClock, idle, walk, grin, glitch, Saved.Size, work, Desk, cloneDice.Next());
            opened.Failed = ClonesOff;
            opened.Cleared = ClonesCleared;
            opened.EscDown = EscKey.Find(Desk);
            opened.HandsFree = delegate { return Program.Smoke == null && !Chores.Running && !Busy; };
            opened.Cursor = Mouse;
            opened.Warp = WarpCursor;
            opened.Grabbable = delegate { return Hands.Streaming || GameUp ? null : Desk.Windows(new long[0]); };
            opened.Allowed = delegate { return !Hands.Streaming && !GameUp; };
            opened.Ticked = CloneTalk;
            cloneRoom = opened;
            cloneTalkAt = now + 3;
            clearClonesItem.IsVisible = true;
            opened.Begin(now);
        }

        // Mac and X11 move the cursor themselves. Anywhere else it takes her virtual mouse, which waits, so it's off this thread.
        private void WarpCursor(PixelPoint at)
        {
            if (Desk.Warp(at) || warping || Fingers.Problem != null) return;
            double[] share = Share(at);
            if (share == null) return;
            warping = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { Fingers.MoveTo(share[0], share[1]); }
                catch (Exception problem) { Brain.Log("clones", -1, problem.Message); }
                finally { warping = false; }
            });
        }

        private double[] Share(PixelPoint at)
        {
            int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
            foreach (Screen screen in Screens.All)
            {
                left = Math.Min(left, screen.Bounds.X);
                top = Math.Min(top, screen.Bounds.Y);
                right = Math.Max(right, screen.Bounds.Right);
                bottom = Math.Max(bottom, screen.Bounds.Bottom);
            }
            return right <= left || bottom <= top ? null : new[] { (at.X - left) / (double)(right - left - 1), (at.Y - top) / (double)(bottom - top - 1) };
        }

        internal void ClonesOff()
        {
            cloneTurn++;
            cloneTalking = false;
            CloneRoom closing = cloneRoom;
            cloneRoom = null;
            if (clearClonesItem != null) clearClonesItem.IsVisible = false;
            if (closing == null) return;
            try { closing.PutBack(); }
            catch (Exception problem) { Brain.Log("clones", -1, problem.Message); }
            try { if (Program.Smoke == null || RealChaos != 0) Desk.Chaos(false); }
            catch (Exception problem) { Brain.Log("clones", -1, problem.Message); }
            closing.Close();
        }

        private void ClonesCleared()
        {
            ClonesOff();
            if (Program.Smoke != null || Away || Chores.Running || !Talk.Quiet) return;
            Reply(++asking, "holds Esc", "Fine. Just me again.");
        }

        // One brain call writes a few lines for all of them, however many there are, and each is said in turn.
        private void CloneTalk(double now)
        {
            if (Away) { ClonesOff(); return; }
            if (Program.Smoke != null || cloneTalking || now < cloneTalkAt || cloneRoom == null || cloneRoom.Swarm.Bodies.Count == 0) return;
            if (Chores.Running || !Talk.Quiet || QuietShown || Brain.Model == null) return;
            cloneTalking = true;
            int turn = ++cloneTurn;
            int copies = cloneRoom.Swarm.Bodies.Count + 1;
            string ask = string.Format(CloneAsk, copies) + (cloneLast == null ? "" : " Last time: " + cloneLast);
            ThreadPool.QueueUserWorkItem(delegate
            {
                string said = null;
                try { said = Brain.Aside(Brain.Character, ask, 200); }
                catch (Exception problem) { Brain.Log("clones", -1, problem.Message); }
                Dispatcher.UIThread.Post(delegate
                {
                    if (turn != cloneTurn) return;
                    List<KeyValuePair<int, string>> lines = CloneScript(said);
                    cloneLast = lines.Count == 0 ? cloneLast : lines[lines.Count - 1].Value;
                    CloneLine(lines, 0, turn);
                });
            });
        }

        internal static List<KeyValuePair<int, string>> CloneScript(string said)
        {
            List<KeyValuePair<int, string>> lines = new List<KeyValuePair<int, string>>();
            foreach (string row in (said ?? "").Split('\n'))
            {
                Match line = Regex.Match(row.Trim(), @"^(?:capri\s*)?(\d{1,6})\s*[:.)\-]\s*(.+)$", RegexOptions.IgnoreCase);
                if (line.Success) lines.Add(new KeyValuePair<int, string>(int.Parse(line.Groups[1].Value), line.Groups[2].Value.Trim()));
            }
            return lines;
        }

        private void CloneLater(int wait, List<KeyValuePair<int, string>> lines, int at, int turn)
        {
            DispatcherTimer.RunOnce(delegate { CloneLine(lines, at, turn); }, TimeSpan.FromMilliseconds(wait));
        }

        private void CloneLine(List<KeyValuePair<int, string>> lines, int at, int turn)
        {
            if (turn != cloneTurn || cloneRoom == null) return;
            if (Chores.Running || !Talk.Quiet) { CloneLater(400, lines, at, turn); return; }
            if (at >= lines.Count)
            {
                cloneTalking = false;
                cloneTalkAt = cloneClock.Elapsed.TotalSeconds + 5 + cloneDice.NextDouble() * 6;
                return;
            }
            string line = lines[at].Value;
            int who = Math.Max(0, lines[at].Key - 1) % (cloneRoom.Swarm.Bodies.Count + 1);
            if (who == 0)
            {
                Reply(++asking, "her clones", line);
                CloneLater(500, lines, at + 1, turn);
                return;
            }
            ISpeaker speaker = Speaks ? Speaker : null;
            ThreadPool.QueueUserWorkItem(delegate
            {
                byte[] wav = null;
                if (speaker != null)
                {
                    try { wav = VoiceFx.Styled(speaker.Speak(line), line); }
                    catch (Exception problem) { Brain.Log("clones", -1, problem.Message); }
                }
                Dispatcher.UIThread.Post(delegate
                {
                    if (turn != cloneTurn || cloneRoom == null) return;
                    double now = cloneClock.Elapsed.TotalSeconds;
                    double seconds = wav != null ? Voice.Seconds(wav) : 1 + line.Length * 0.06;
                    if (who - 1 < cloneRoom.Swarm.Bodies.Count) cloneRoom.Say(cloneRoom.Swarm.Bodies[who - 1], line, now + seconds + 1);
                    if (wav != null) voice.Play(wav);
                    CloneLater((int)(seconds * 1000) + 300, lines, at + 1, turn);
                });
            });
        }
    }
}
