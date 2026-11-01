using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;

namespace CapriDesktop
{
    // Windows' pink ink drawn with Avalonia: each stroke draws itself on in about half a second, in glass only as big as what she drew.
    internal sealed class InkLine
    {
        internal const double Seconds = 0.55;
        internal readonly double Born, Length;
        internal readonly Rect Bounds;
        internal readonly string Text;
        internal readonly Point At;
        // Each run is a pen stroke of its own, like the two halves of an arrow.
        private readonly List<Point[]> runs = new List<Point[]>();

        internal InkLine(InkShape shape, double born)
        {
            Born = born;
            Point first = new Point(shape.Points[0].X, shape.Points[0].Y);
            At = first;
            if (shape.Kind == "text")
            {
                Text = shape.Text;
                FormattedText measured = Look.Text(Text, Look.Regular, 28, Look.Pink);
                Bounds = new Rect(first.X - 12, first.Y - 12, measured.Width + 24, measured.Height + 24);
                Length = measured.Width;
                return;
            }
            if (shape.Kind == "circle" || shape.Kind == "box")
            {
                Point corner = new Point(shape.Points[1].X, shape.Points[1].Y);
                double left = Math.Min(first.X, corner.X), top = Math.Min(first.Y, corner.Y), right = Math.Max(first.X, corner.X), bottom = Math.Max(first.Y, corner.Y);
                if (shape.Kind == "box") runs.Add(new[] { new Point(left, top), new Point(right, top), new Point(right, bottom), new Point(left, bottom), new Point(left, top) });
                else
                {
                    Point[] round = new Point[73];
                    for (int i = 0; i <= 72; i++) round[i] = new Point((left + right) / 2 + (right - left) / 2 * Math.Cos(i * Math.PI / 36), (top + bottom) / 2 + (bottom - top) / 2 * Math.Sin(i * Math.PI / 36));
                    runs.Add(round);
                }
            }
            else
            {
                Point[] line = new Point[shape.Points.Length];
                for (int i = 0; i < line.Length; i++) line[i] = new Point(shape.Points[i].X, shape.Points[i].Y);
                runs.Add(line);
                if (shape.Kind == "arrow" && line.Length >= 2)
                {
                    Point tip = line[1];
                    double angle = Math.Atan2(tip.Y - first.Y, tip.X - first.X), head = Math.Min(18, Far(first, tip) / 3);
                    runs.Add(new[] { new Point(tip.X - head * Math.Cos(angle - 0.5), tip.Y - head * Math.Sin(angle - 0.5)), tip, new Point(tip.X - head * Math.Cos(angle + 0.5), tip.Y - head * Math.Sin(angle + 0.5)) });
                }
            }
            double lowX = double.MaxValue, lowY = double.MaxValue, highX = double.MinValue, highY = double.MinValue;
            foreach (Point[] run in runs)
                for (int i = 0; i < run.Length; i++)
                {
                    if (i > 0) Length += Far(run[i - 1], run[i]);
                    lowX = Math.Min(lowX, run[i].X);
                    lowY = Math.Min(lowY, run[i].Y);
                    highX = Math.Max(highX, run[i].X);
                    highY = Math.Max(highY, run[i].Y);
                }
            Bounds = new Rect(lowX - 12, lowY - 12, highX - lowX + 24, highY - lowY + 24);
        }

        private static double Far(Point a, Point b) { return Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y)); }

        internal bool Moving(double now) { return now < Born + Seconds; }

        private static readonly Pen Glow = new Pen(new SolidColorBrush(Color.FromArgb(24, Look.Pink.R, Look.Pink.G, Look.Pink.B)), 10, null, PenLineCap.Round, PenLineJoin.Round);
        private static readonly Pen Soft = new Pen(new SolidColorBrush(Color.FromArgb(65, Look.Pink.R, Look.Pink.G, Look.Pink.B)), 6, null, PenLineCap.Round, PenLineJoin.Round);
        private static readonly Pen Core = new Pen(new SolidColorBrush(Look.Pink), 2.5, null, PenLineCap.Round, PenLineJoin.Round);

        internal void Paint(DrawingContext graphics, double now)
        {
            double share = Math.Max(0, Math.Min(1, (now - Born) / Seconds));
            if (Text != null)
            {
                // Words wipe on left to right, since a font's outlines aren't one pen line.
                using (graphics.PushClip(new Rect(Bounds.X, Bounds.Y, Bounds.Width * share, Bounds.Height)))
                    graphics.DrawText(Look.Text(Text, Look.Regular, 28, Look.Pink), At);
                return;
            }
            double left = Length * share;
            StreamGeometry shown = new StreamGeometry();
            using (StreamGeometryContext pen = shown.Open())
                foreach (Point[] run in runs)
                {
                    if (left <= 0) break;
                    pen.BeginFigure(run[0], false);
                    for (int i = 1; i < run.Length && left > 0; i++)
                    {
                        double step = Far(run[i - 1], run[i]);
                        Point to = run[i];
                        if (left < step && step > 0) to = new Point(run[i - 1].X + (run[i].X - run[i - 1].X) * left / step, run[i - 1].Y + (run[i].Y - run[i - 1].Y) * left / step);
                        pen.LineTo(to);
                        left -= step;
                    }
                    pen.EndFigure(false);
                }
            foreach (Pen pen in new[] { Glow, Soft, Core }) graphics.DrawGeometry(null, pen, shown);
        }
    }

    internal sealed class InkPane : Window
    {
        private readonly PixelRect room;
        private readonly List<InkLine> lines;
        private readonly Stopwatch clock;
        private readonly DispatcherTimer ticks = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        private readonly Sheet sheet;

        internal InkPane(PixelRect area, double scale, List<InkLine> drawing, Stopwatch time)
        {
            room = area;
            clock = time;
            lines = drawing.FindAll(delegate(InkLine line) { return line.Bounds.Intersects(new Rect(area.X, area.Y, area.Width, area.Height)); });
            Look.Overlay(this, "Capri drawing");
            Position = area.Position;
            Width = area.Width / scale;
            Height = area.Height / scale;
            IsHitTestVisible = false;
            sheet = new Sheet(this);
            Content = sheet;
            ticks.Tick += delegate
            {
                sheet.InvalidateVisual();
                if (!lines.Exists(delegate(InkLine line) { return line.Moving(clock.Elapsed.TotalSeconds); })) ticks.Stop();
            };
            Opened += delegate { Look.ClickThrough(this); ticks.Start(); };
        }

        protected override void OnClosed(EventArgs e)
        {
            ticks.Stop();
            base.OnClosed(e);
        }

        private sealed class Sheet : Control
        {
            private readonly InkPane owner;

            internal Sheet(InkPane pane) { owner = pane; }

            public override void Render(DrawingContext graphics)
            {
                double scale = owner.RenderScaling > 0 ? owner.RenderScaling : 1, now = owner.clock.Elapsed.TotalSeconds;
                using (graphics.PushTransform(Matrix.CreateTranslation(-owner.room.X, -owner.room.Y) * Matrix.CreateScale(1 / scale, 1 / scale)))
                    foreach (InkLine line in owner.lines) line.Paint(graphics, now);
            }
        }
    }

    internal sealed partial class Pet
    {
        private readonly Stopwatch inkClock = Stopwatch.StartNew();
        private readonly List<InkLine> inkLines = new List<InkLine>();
        private readonly List<InkPane> inkPanes = new List<InkPane>();
        private MenuItem clearInkItem;
        private readonly Lookout lookout = new Lookout();
        private double nextMonitors;
        private volatile System.Drawing.Rectangle[] monitors = new System.Drawing.Rectangle[0];
        private System.Drawing.Point? mouseSeen;

        internal int InkPanes { get { return inkPanes.Count; } }

        private void SetUpInk()
        {
            RememberMonitors();
            Ink.Monitors = delegate { return monitors; };
            Ink.Mouse = delegate { return mouseSeen; };
            Ink.Active = delegate { return inkLines.Count > 0; };
            Hands.Draw = DrawTool;
            Hands.Websites = new Websites(Program.Smoke ?? folder);
            Hands.ReadFile = delegate(string path, int? page, long? offset)
            {
                return Files.Read(path, offset, delegate(byte[] picture) { return Brain.See(picture, "Describe this picture and read out any text in it.", "What's in this picture?", 400); });
            };
            lookout.Unseeing = delegate { return Dispatcher.UIThread.Invoke(delegate { return Program.Smoke != null ? "Testing." : Unseeing(); }); };
            lookout.Done = delegate { return quitting; };
            lookout.Shot = delegate { return Eyes.Jpeg(Eyes.Shoot(), 1280); };
            lookout.See = delegate(string system, string instruction, byte[] jpeg) { return Brain.See(jpeg, system, instruction, 120); };
            lookout.Free = delegate { return Dispatcher.UIThread.Invoke(delegate { return !Hushed && !quitting && !Busy && Talk.Quiet; }); };
            lookout.Speak = delegate(string line) { Dispatcher.UIThread.Post(delegate { Reply(++asking, "lookout", line); }); };
            Hands.WatchFor = lookout.Run;
        }

        // Her brain can ask from any thread, and Avalonia's screens answer only on hers.
        private void RememberMonitors()
        {
            List<System.Drawing.Rectangle> found = new List<System.Drawing.Rectangle>();
            foreach (Screen screen in Screens.All) found.Add(new System.Drawing.Rectangle(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height));
            monitors = found.ToArray();
            PixelPoint? at = Mouse();
            mouseSeen = at == null ? (System.Drawing.Point?)null : new System.Drawing.Point(at.Value.X, at.Value.Y);
        }

        internal string DrawTool(ToolArguments args)
        {
            if (args == null) args = new ToolArguments();
            if (!Ink.Valid(args)) return "Couldn't read that drawing action.";
            if (string.Equals(args.Action, "clear", StringComparison.OrdinalIgnoreCase)) return Dispatcher.UIThread.Invoke(ClearInk);
            if (args.Shapes == null && args.DrawingShapes == null && !string.IsNullOrWhiteSpace(args.Description))
            {
                if (Ink.ScreenRequest(args.Description)) return Hands.Work(args.Description, args.Asked);
                if (Ink.Desktop().IsEmpty) return Ink.NoSpace;
                bool teaching = Ink.Teaches(args.Description);
                List<KeyValuePair<string, ToolArguments>> calls = new List<KeyValuePair<string, ToolArguments>>();
                string composed = Brain.Step((teaching ? Ink.Teaching : "") + "Compose their requested screen drawing with draw. Supply shapes, not description. Use paths for freehand art. " + Ink.Description + Ink.Context(), args.Description, Ink.Tools, Ink.ClaudeTools, Ink.Listing, calls, teaching ? Ink.TeachingBudget : 400);
                if (calls.Count == 0) calls.AddRange(Hands.Written(composed));
                List<InkShape> drawing = new List<InkShape>();
                foreach (KeyValuePair<string, ToolArguments> call in calls) if (call.Key == "draw" && call.Value != null) drawing.AddRange(Ink.Parse(call.Value.Shapes));
                ToolArguments ready = args.Copy();
                ready.Description = null;
                ready.DrawingShapes = drawing;
                string made = DrawTool(ready);
                return teaching ? Ink.Explained(made, drawing) : made;
            }
            if (args.Shapes == null && args.DrawingShapes == null) return Ink.Question;
            List<InkShape> shapes = args.DrawingShapes ?? Ink.Parse(args.Shapes);
            bool add = string.Equals(args.Action, "add", StringComparison.OrdinalIgnoreCase);
            return Dispatcher.UIThread.Invoke(delegate { return InkDraw(shapes, add); });
        }

        private string InkDraw(List<InkShape> shapes, bool add)
        {
            if (shapes.Count == 0) return "Couldn't read any shapes to draw.";
            List<InkLine> fresh = new List<InkLine>();
            double born = inkClock.Elapsed.TotalSeconds;
            foreach (InkShape shape in shapes)
            {
                try { InkLine line = new InkLine(shape, born); if (line.Length > 0) fresh.Add(line); }
                catch (ArgumentException) { }
            }
            if (fresh.Count == 0) return "Couldn't read any shapes to draw.";
            List<InkLine> drawing = add ? new List<InkLine>(inkLines) : new List<InkLine>();
            drawing.AddRange(fresh);
            List<InkPane> opened = new List<InkPane>();
            bool freshShows = false;
            try
            {
                foreach (Screen screen in Screens.All)
                {
                    PixelRect monitor = screen.Bounds, room = default(PixelRect);
                    bool any = false;
                    foreach (InkLine line in drawing)
                    {
                        PixelRect part = new PixelRect((int)Math.Floor(line.Bounds.X), (int)Math.Floor(line.Bounds.Y), (int)Math.Ceiling(line.Bounds.Width), (int)Math.Ceiling(line.Bounds.Height)).Intersect(monitor);
                        if (part.Width <= 0 || part.Height <= 0) continue;
                        room = any ? room.Union(part) : part;
                        any = true;
                        freshShows |= fresh.Contains(line);
                    }
                    if (!any) continue;
                    InkPane pane = new InkPane(room, screen.Scaling, drawing, inkClock);
                    opened.Add(pane);
                }
                if (!freshShows) { foreach (InkPane unused in opened) unused.Close(); return Ink.NoSpace; }
                foreach (InkPane pane in opened) pane.Show();
            }
            catch (Exception problem)
            {
                Brain.Log("ink", -1, problem.Message);
                foreach (InkPane failed in opened) failed.Close();
                return Ink.NoOverlay;
            }
            foreach (InkPane previous in inkPanes) previous.Close();
            inkPanes.Clear();
            inkPanes.AddRange(opened);
            inkLines.Clear();
            inkLines.AddRange(drawing);
            clearInkItem.IsVisible = true;
            return add ? "Added to my drawing." : "Drew on your screen.";
        }

        internal string ClearInk()
        {
            foreach (InkPane pane in inkPanes) pane.Close();
            inkPanes.Clear();
            inkLines.Clear();
            if (clearInkItem != null) clearInkItem.IsVisible = false;
            return "Cleared my drawing.";
        }
    }
}
