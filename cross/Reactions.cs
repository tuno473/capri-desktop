using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;

namespace CapriDesktop
{
    // Her live reactions and a small game panel, by looking at the picture: a sudden flash, the screen going dark or red, or the
    // scene changing. Windows also reads kills and health bars for each game, which this doesn't.
    internal sealed class Moments
    {
        internal static readonly string[] Flashes = { "my EYES", "who turned the sun on??", "flashbang??", "whoa whoa whoa", "bright!!", "I'm blind, choom" };
        internal static readonly string[] Hits = { "ow ow ow", "careful!!", "you're getting cooked", "heal up!!", "behind you??", "get out of there", "that stung", "they're on you" };
        internal static readonly string[] Darks = { "uh oh", "lights out", "is that bad", "who turned off the lights", "don't you dare jumpscare me", "hello??" };
        internal static readonly string[] Scenes = { "ooh where are we", "new area??", "wait what's this", "plot twist", "here we go", "ooh fancy", "where'd you take us" };
        private double[] was;
        private double lastLight;

        // What just happened between this little picture and the last, or null. BGRA, as Eyes.Small gives it.
        internal string See(byte[] pixels)
        {
            if (pixels == null || pixels.Length < 4) return null;
            double r = 0, g = 0, b = 0;
            int count = pixels.Length / 4;
            for (int i = 0; i < count; i++) { b += pixels[i * 4]; g += pixels[i * 4 + 1]; r += pixels[i * 4 + 2]; }
            r /= count; g /= count; b /= count;
            double light = (r + g + b) / 3, change = 0;
            double[] now = new double[64];
            int side = 8, wide = (int)Math.Sqrt(count * 16 / 9.0), tall = Math.Max(1, count / Math.Max(1, wide));
            int[] seen = new int[64];
            for (int i = 0; i < count; i++)
            {
                int cell = Math.Min(side - 1, (i % Math.Max(1, wide)) * side / Math.Max(1, wide)) + Math.Min(side - 1, i / Math.Max(1, wide) * side / tall) * side;
                now[cell] += (pixels[i * 4] + pixels[i * 4 + 1] + pixels[i * 4 + 2]) / 3.0;
                seen[cell]++;
            }
            for (int i = 0; i < 64; i++) now[i] /= Math.Max(1, seen[i]);
            double[] before = was;
            double lightWas = lastLight;
            was = now;
            lastLight = light;
            if (before == null) return null;
            for (int i = 0; i < 64; i++) change += Math.Abs(now[i] - before[i]);
            change /= 64;
            if (light - lightWas > 70 && light > 170) return "flash";
            if (lightWas - light > 50 && light < 25) return "dark";
            if (r > g * 1.7 && r > b * 1.7 && r > 70) return "hit";
            return change > 45 ? "scene" : null;
        }

        internal static string[] Pool(string kind) { return kind == "flash" ? Flashes : kind == "hit" ? Hits : kind == "dark" ? Darks : Scenes; }
    }

    // The panel over her game: Gaming Mode's status and her latest lines. Clicks go through it.
    internal sealed class GamePanel : Window
    {
        internal const double Wide = 320, Tall = 150, LineFor = 5;
        private readonly List<KeyValuePair<double, string>> lines = new List<KeyValuePair<double, string>>();
        private readonly Sheet sheet;
        private double now;

        internal GamePanel()
        {
            Look.Overlay(this, "Capri game panel");
            Width = Wide;
            Height = Tall;
            IsHitTestVisible = false;
            sheet = new Sheet(this);
            Content = sheet;
            Opened += delegate { Look.ClickThrough(this); };
        }

        internal int Count { get { return lines.Count; } }

        internal void Say(string line, double at)
        {
            if (lines.Count > 0 && lines[lines.Count - 1].Value == line) return;
            lines.Add(new KeyValuePair<double, string>(at, line));
            if (lines.Count > 4) lines.RemoveAt(0);
            Step(at);
        }

        internal void Step(double at)
        {
            now = at;
            lines.RemoveAll(delegate(KeyValuePair<double, string> line) { return at - line.Key > LineFor; });
            sheet.InvalidateVisual();
        }

        private sealed class Sheet : Control
        {
            private readonly GamePanel owner;

            internal Sheet(GamePanel panel) { owner = panel; }

            public override void Render(DrawingContext graphics)
            {
                graphics.DrawRectangle(new SolidColorBrush(Color.FromArgb(150, 13, 12, 20)), null, new Rect(0, 0, 150, 24));
                graphics.DrawText(Look.Text("GAMING MODE", Look.Bold, 13, Look.Pink), new Point(8, 4));
                double y = 28;
                foreach (KeyValuePair<double, string> line in owner.lines)
                {
                    byte alpha = (byte)Math.Max(60, Math.Min(255, 255 * (1 - (owner.now - line.Key) / LineFor) + 80));
                    FormattedText text = Look.Text(Look.Cut(line.Value, 38), Look.Regular, 15, Color.FromArgb(alpha, 244, 244, 248));
                    graphics.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(alpha / 2), 13, 12, 20)), null, new Rect(0, y, text.Width + 16, text.Height + 6));
                    graphics.DrawText(text, new Point(8, y + 3));
                    y += text.Height + 8;
                }
            }
        }
    }

    internal sealed partial class Pet
    {
        private readonly Moments moments = new Moments();
        private GamePanel gamePanel;
        private double nextMoment, nextMomentLine;

        internal GamePanel GamePanelShown { get { return gamePanel; } }

        internal void OpenGamePanel()
        {
            if (gamePanel != null) return;
            PixelRect work = Area;
            double scale = RenderScaling > 0 ? RenderScaling : 1;
            gamePanel = new GamePanel { Position = new PixelPoint(work.Right - (int)(GamePanel.Wide * scale) - 12, work.Y + 12) };
            gamePanel.Show();
            Look.OnTopAgain(gamePanel);
        }

        internal void CloseGamePanel()
        {
            if (gamePanel != null) gamePanel.Close();
            gamePanel = null;
        }

        // A quick line for what the picture just did, up in her panel at once. A beat apart, so a flickering game can't fill it.
        internal string Moment(byte[] small, double now)
        {
            string kind = moments.See(small);
            if (kind == null || now < nextMomentLine) return null;
            nextMomentLine = now + (kind == "scene" ? 12 : 4);
            string[] pool = Moments.Pool(kind);
            string line = pool[cloneDice.Next(pool.Length)];
            if (gamePanel != null) gamePanel.Say(line, now);
            return line;
        }

        private void CheckGamePanel(double now)
        {
            if (gamePanel != null) gamePanel.Step(MoveClock);
        }

        private void MomentsLoop(int turn)
        {
            while (turn == watchTurn)
            {
                Thread.Sleep(2500);
                if (turn != watchTurn || Saved.NoLiveReactions) continue;
                try
                {
                    byte[] shot = Eyes.Shoot();
                    byte[] small = shot == null ? null : Eyes.Small(shot, 96, 54);
                    if (small != null) Dispatcher.UIThread.Post(delegate { if (turn == watchTurn) Moment(small, MoveClock); });
                }
                catch (Exception problem) { Brain.Log("live reactions", -1, problem.Message); }
            }
        }
    }
}
