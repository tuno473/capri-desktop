using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;

namespace CapriDesktop
{
    // Her desktop boards with Avalonia: the same games as Windows, in a little window beside her that drags by its top.
    internal abstract class BoardWindow : Window
    {
        internal const double TitleBar = 44;
        protected readonly Sheet Face;
        protected static readonly SolidColorBrush Panel = new SolidColorBrush(Color.FromRgb(13, 12, 20)), Well = new SolidColorBrush(Color.FromRgb(28, 26, 38)), Pink = new SolidColorBrush(Look.Pink), Cyan = new SolidColorBrush(Look.Cyan), White = new SolidColorBrush(Color.FromRgb(236, 236, 242));
        protected static readonly Pen Edge = new Pen(Pink, 2), Thin = new Pen(new SolidColorBrush(Color.FromRgb(70, 73, 82)), 1);

        protected BoardWindow(string title, double wide, double tall)
        {
            Title = title;
            Width = wide;
            Height = tall;
            SystemDecorations = SystemDecorations.None;
            CanResize = false;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Background = Panel;
            Face = new Sheet(this);
            Content = Face;
            KeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) Close(); };
        }

        // Beside her, on whichever side has room.
        internal void Open(PixelRect her, PixelRect area, double scale)
        {
            int wide = (int)(Width * scale), tall = (int)(Height * scale);
            int x = her.Right + 8 + wide <= area.Right ? her.Right + 8 : Math.Max(area.X, her.X - wide - 8);
            Position = new PixelPoint(x, Math.Max(area.Y, Math.Min(area.Bottom - tall, her.Y)));
            Show();
        }

        protected abstract void Paint(DrawingContext graphics);
        protected abstract int PartAt(Point at);
        protected abstract void Press(int part);

        protected static void Words(DrawingContext graphics, string words, double size, Brush ink, Rect box, bool middle)
        {
            Color colour = ((SolidColorBrush)ink).Color;
            FormattedText text = Look.Text(words ?? "", Look.Regular, size, colour);
            text.MaxTextWidth = Math.Max(1, box.Width);
            graphics.DrawText(text, middle ? new Point(box.X + (box.Width - text.Width) / 2, box.Y + (box.Height - text.Height) / 2) : box.TopLeft);
        }

        protected static void Button(DrawingContext graphics, string words, Rect box, bool on = false)
        {
            graphics.DrawRectangle(on ? Pink : Well, Thin, box);
            Words(graphics, words, 13, on ? Panel : White, box, true);
        }

        internal void ClickAt(Point at) { int part = PartAt(at); if (part >= 0) Press(part); Face.InvalidateVisual(); }

        protected sealed class Sheet : Control
        {
            private readonly BoardWindow owner;

            internal Sheet(BoardWindow window)
            {
                owner = window;
                Focusable = true;
                PointerPressed += delegate(object sender, PointerPressedEventArgs e)
                {
                    Point at = e.GetPosition(this);
                    int part = owner.PartAt(at);
                    if (part >= 0) { owner.Press(part); InvalidateVisual(); return; }
                    if (at.Y < TitleBar) owner.BeginMoveDrag(e);
                };
            }

            public override void Render(DrawingContext graphics)
            {
                graphics.DrawRectangle(Panel, Edge, new Rect(1, 1, Bounds.Width - 2, Bounds.Height - 2));
                owner.Paint(graphics);
            }
        }
    }

    internal sealed class MiniPane : BoardWindow
    {
        private static readonly string[] HandNames = { "ROCK", "PAPER", "SCISSORS" };
        internal readonly MiniGame Game;
        internal event Action Played;

        internal MiniPane(MiniGame game) : base("Capri " + game.Title.ToLowerInvariant(), 380, 490) { Game = game; }

        private static Rect Part(string kind, int pick)
        {
            if (pick == 9) return new Rect(326, 14, 30, 30);
            if (pick == 10) return new Rect(24, 450, 170, 24);
            if (kind == "tic_tac_toe") return new Rect(48 + pick % 3 * 92, 138 + pick / 3 * 92, 92, 92);
            if (kind == "connect_four") return new Rect(25 + pick * 46, 138, 46, 276);
            return new Rect(24 + pick * 108, 171, 104, 130);
        }

        protected override int PartAt(Point at)
        {
            if (Part(Game.Kind, 9).Contains(at)) return 9;
            if (Part(Game.Kind, 10).Contains(at)) return 10;
            int count = Game.Kind == "tic_tac_toe" ? 9 : Game.Kind == "connect_four" ? 7 : 3;
            for (int pick = 0; pick < count; pick++) if (Part(Game.Kind, pick).Contains(at)) return pick;
            return -1;
        }

        protected override void Press(int part)
        {
            if (part == 9) { Close(); return; }
            if (part == 10) { Again(); return; }
            PlayMove(part);
        }

        internal bool PlayMove(int move)
        {
            bool played = Game.Move(move);
            Face.InvalidateVisual();
            if (played && Played != null) Played();
            return played;
        }

        internal void Again()
        {
            Game.Again();
            Face.InvalidateVisual();
            if (Played != null) Played();
        }

        protected override void Paint(DrawingContext graphics)
        {
            Words(graphics, Game.Title, 18, Pink, new Rect(24, 16, 280, 26), false);
            Button(graphics, "X", Part(Game.Kind, 9));
            Words(graphics, Game.Score.Text, 13, Cyan, new Rect(24, 54, 332, 20), false);
            Words(graphics, Game.Last, 14, White, new Rect(24, 80, 332, 50), false);
            if (Game.Kind == "tic_tac_toe")
                for (int cell = 0; cell < 9; cell++)
                {
                    Rect box = Part(Game.Kind, cell);
                    graphics.DrawRectangle(Well, Thin, box);
                    if (Game.Cells[cell] != 0) Words(graphics, Game.Cells[cell] == 1 ? "X" : "O", 46, Game.Cells[cell] == 1 ? Cyan : Pink, box, true);
                }
            else if (Game.Kind == "connect_four")
                for (int row = 0; row < 6; row++)
                    for (int column = 0; column < 7; column++)
                    {
                        Rect box = new Rect(25 + column * 46, 138 + row * 46, 46, 46);
                        graphics.DrawRectangle(Well, Thin, box);
                        int cell = Game.Cells[row * 7 + column];
                        if (cell != 0) graphics.DrawEllipse(cell == 1 ? Cyan : Pink, null, box.Center, 18, 18);
                    }
            else
            {
                for (int hand = 0; hand < 3; hand++) Button(graphics, HandNames[hand], Part(Game.Kind, hand), Game.YourHand == hand);
                if (Game.HerHand >= 0) Words(graphics, "CAPRI: " + HandNames[Game.HerHand], 18, Pink, new Rect(24, 330, 332, 40), true);
            }
            Button(graphics, "NEW ROUND", Part(Game.Kind, 10));
        }
    }

    internal sealed class ChessPane : BoardWindow
    {
        private const double Tile = 54, Left = 24, Top = 96;
        private const int CloseIt = 128, Undo = 129, Levels = 133, Promote = 140;
        private static readonly string[] Actions = { "UNDO", "NEW", "HINT", "RESIGN" }, LevelNames = { "easy", "normal", "hard" };
        private static readonly double[][] Shapes =
        {
            new double[] { 13, 43, 16, 37, 21, 34, 22, 24, 30, 24, 31, 34, 36, 37, 39, 43 },
            new double[] { 18, 32, 20, 24, 14, 25, 12, 21, 21, 12, 23, 7, 30, 12, 35, 22, 33, 34 },
            new double[] { 26, 7, 35, 19, 32, 27, 20, 27, 17, 19 },
            new double[] { 15, 11, 21, 11, 21, 16, 24, 16, 24, 11, 29, 11, 29, 16, 32, 16, 32, 11, 37, 11, 35, 25, 17, 25 },
            new double[] { 14, 13, 21, 19, 26, 9, 31, 19, 38, 13, 33, 28, 19, 28 },
            new double[] { 13, 43, 16, 37, 21, 34, 22, 28, 18, 24, 18, 19, 23, 19, 23, 15, 18, 15, 18, 10, 23, 10, 23, 6, 29, 6, 29, 10, 34, 10, 34, 15, 29, 15, 29, 19, 34, 19, 34, 24, 30, 28, 31, 34, 36, 37, 39, 43 },
        };
        private readonly DispatcherTimer ticks = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        private readonly Random dice = new Random();
        private ChessWork work;
        private bool hintWork, hintAfter;
        private int selected = -1, promotionFrom = -1, promotionTo = -1;
        private ChessMove hint = new ChessMove(-1, -1, 0);
        private List<ChessMove> legal;
        private string note = "";
        internal readonly ChessGame Game;
        internal event Action Played;

        internal ChessPane(ChessGame game) : base("Capri chess", 480, 634)
        {
            Game = game;
            legal = game.Board.Legal();
            ticks.Tick += delegate { FinishWork(); };
            ticks.Start();
            Opened += delegate { Think(false); };
        }

        internal bool Busy { get { return work != null; } }

        // Her timer's job, for anything that can't wait for it.
        internal void Poll() { FinishWork(); }

        protected override void OnClosed(EventArgs e)
        {
            Cancel();
            ticks.Stop();
            base.OnClosed(e);
        }

        private Rect TileFor(int square)
        {
            int file = square & 7, rank = square / 16;
            return Game.You == 1 ? new Rect(Left + file * Tile, Top + (7 - rank) * Tile, Tile, Tile) : new Rect(Left + (7 - file) * Tile, Top + rank * Tile, Tile, Tile);
        }

        private static Rect ButtonFor(int part)
        {
            if (part == CloseIt) return new Rect(426, 14, 30, 30);
            if (part >= Undo && part < Undo + 4) return new Rect(24 + (part - Undo) * 110, 540, 102, 30);
            if (part >= Levels && part < Levels + 3) return new Rect(24 + (part - Levels) * 148, 582, 136, 30);
            return new Rect(132 + (part - Promote) * 54, 300, 54, 54);
        }

        protected override int PartAt(Point at)
        {
            if (promotionFrom >= 0) for (int part = Promote; part < Promote + 4; part++) if (ButtonFor(part).Contains(at)) return part;
            if (ButtonFor(CloseIt).Contains(at)) return CloseIt;
            for (int part = Undo; part < Levels + 3; part++) if (ButtonFor(part).Contains(at)) return part;
            for (int rank = 0; rank < 8; rank++) for (int file = 0; file < 8; file++) if (TileFor(rank * 16 + file).Contains(at)) return rank * 16 + file;
            return -1;
        }

        protected override void Press(int part)
        {
            if (part == CloseIt) { Close(); return; }
            if (part >= Undo && part < Undo + 4) { Command(new[] { "undo", "again", "hint", "resign" }[part - Undo], null, null); return; }
            if (part >= Levels && part < Levels + 3) { Command("level", LevelNames[part - Levels], null); return; }
            if (part >= Promote && part < Promote + 4 && promotionFrom >= 0) { Play(promotionFrom, promotionTo, new[] { 5, 4, 3, 2 }[part - Promote]); return; }
            if (part < 0 || part >= 128 || Game.Result != 0 || Game.Board.Turn != Game.You) return;
            if (Game.Board.Cells[part] * Game.You > 0) selected = part;
            else if (selected >= 0) Play(selected, part, 0);
        }

        internal void Command(string action, string level, string side)
        {
            if (action == "close") { Close(); return; }
            if (action == "hint")
            {
                if (Game.Result != 0) { note = Game.Status; Face.InvalidateVisual(); return; }
                if (Game.Board.Turn != Game.You) { hintAfter = true; note = "Hint after Capri's move."; Face.InvalidateVisual(); return; }
                Cancel();
                Think(true);
                return;
            }
            Cancel();
            if (action == "undo") Game.Undo();
            else if (action == "resign") Game.Resign();
            else if (action == "level") { if (!string.IsNullOrEmpty(level)) Game.Level = level; Game.Moment = "reset"; }
            else if (action == "again") Game.Again(level, side);
            selected = promotionFrom = promotionTo = -1;
            hint = new ChessMove(-1, -1, 0);
            note = "";
            Changed();
            Think(false);
        }

        private void Cancel()
        {
            if (work != null) work.Cancelled = true;
            work = null;
            hintAfter = false;
        }

        private void Think(bool help)
        {
            if (work != null || Game.Result != 0 || (!help && Game.Board.Turn == Game.You)) return;
            hintWork = help;
            work = ChessWork.Start(Game.Board, help ? "hard" : Game.Level, dice.Next());
            if (help) note = "Looking for a hint...";
            Face.InvalidateVisual();
        }

        private void FinishWork()
        {
            if (work == null || !work.Done) return;
            ChessWork finished = work;
            bool helping = hintWork, nextHint = hintAfter;
            work = null;
            hintAfter = false;
            if (finished.Cancelled) return;
            if (finished.Error != null || finished.Choice == null || finished.Choice.Move.From < 0)
            {
                note = "Couldn't finish that search. Try a level or new game.";
                Face.InvalidateVisual();
                return;
            }
            if (helping)
            {
                hint = finished.Choice.Move;
                selected = hint.From;
                note = "Try " + ChessPosition.Square(hint.From) + " - " + ChessPosition.Square(hint.To) + ".";
                Face.InvalidateVisual();
                return;
            }
            note = "";
            Game.Move(finished.Choice.Move, finished.Choice);
            Changed();
            if (nextHint) Think(true);
        }

        private void Changed()
        {
            legal = Game.Board.Legal();
            Face.InvalidateVisual();
            if (Played != null) Played();
        }

        internal void Play(int from, int to, int promotion)
        {
            if (Game.Result != 0 || Game.Board.Turn != Game.You) return;
            foreach (ChessMove move in legal)
            {
                if (move.From != from || move.To != to) continue;
                if (move.Promotion != 0 && promotion == 0)
                {
                    Cancel();
                    promotionFrom = from;
                    promotionTo = to;
                    Face.InvalidateVisual();
                    return;
                }
                if (move.Promotion != promotion) continue;
                Cancel();
                if (!Game.Move(move, null)) return;
                selected = promotionFrom = promotionTo = -1;
                hint = new ChessMove(-1, -1, 0);
                note = "";
                Changed();
                Think(false);
                return;
            }
        }

        private static readonly SolidColorBrush Dark = new SolidColorBrush(Color.FromRgb(36, 32, 52)), Light = new SolidColorBrush(Color.FromRgb(74, 68, 96)), Picked = new SolidColorBrush(Color.FromArgb(150, 255, 46, 136)), Hinted = new SolidColorBrush(Color.FromArgb(130, 58, 214, 235));

        private static Geometry Shape(int kind)
        {
            StreamGeometry made = new StreamGeometry();
            using (StreamGeometryContext pen = made.Open())
            {
                pen.SetFillRule(FillRule.NonZero);
                foreach (double[] points in kind == 6 ? new[] { Shapes[5] } : kind == 1 ? new[] { Shapes[0] } : new[] { Shapes[0], Shapes[kind - 1] })
                {
                    pen.BeginFigure(new Point(points[0], points[1]), true);
                    for (int i = 2; i < points.Length; i += 2) pen.LineTo(new Point(points[i], points[i + 1]));
                    pen.EndFigure(true);
                }
            }
            return made;
        }

        private static void Piece(DrawingContext graphics, int piece, Rect box)
        {
            using (graphics.PushTransform(Matrix.CreateScale(box.Width / 52, box.Height / 52) * Matrix.CreateTranslation(box.X, box.Y)))
            {
                Brush fill = piece > 0 ? White : Pink;
                Pen edge = new Pen(piece > 0 ? Panel : Well, 1.5);
                graphics.DrawGeometry(fill, edge, Shape(Math.Abs(piece)));
                if (Math.Abs(piece) == 1) graphics.DrawEllipse(fill, edge, new Point(26, 19), 7, 7);
            }
        }

        protected override void Paint(DrawingContext graphics)
        {
            Words(graphics, "CHESS", 18, Pink, new Rect(24, 16, 200, 26), false);
            Button(graphics, "X", ButtonFor(CloseIt));
            Words(graphics, Game.Score.Text, 12, Cyan, new Rect(24, 46, 432, 18), false);
            Words(graphics, note.Length > 0 ? note : work != null ? "Capri's thinking..." : Game.Status, 13, White, new Rect(24, 68, 432, 24), false);
            for (int rank = 0; rank < 8; rank++)
                for (int file = 0; file < 8; file++)
                {
                    int square = rank * 16 + file;
                    Rect box = TileFor(square);
                    graphics.FillRectangle((rank + file) % 2 == 0 ? Dark : Light, box);
                    if (square == selected) graphics.FillRectangle(Picked, box);
                    else if (square == hint.To || square == Game.LastMove.To || square == Game.LastMove.From) graphics.FillRectangle(Hinted, box);
                    if (selected >= 0 && legal.Exists(delegate(ChessMove move) { return move.From == selected && move.To == square; })) graphics.DrawEllipse(Cyan, null, box.Center, 6, 6);
                    int piece = Game.Board.Cells[square];
                    if (piece != 0) Piece(graphics, piece, box);
                }
            for (int action = 0; action < 4; action++) Button(graphics, Actions[action], ButtonFor(Undo + action));
            for (int level = 0; level < 3; level++) Button(graphics, LevelNames[level].ToUpperInvariant(), ButtonFor(Levels + level), Game.Level == LevelNames[level]);
            if (promotionFrom >= 0)
                for (int choice = 0; choice < 4; choice++)
                {
                    Rect box = ButtonFor(Promote + choice);
                    graphics.FillRectangle(Panel, box);
                    Piece(graphics, new[] { 5, 4, 3, 2 }[choice] * Game.You, box);
                }
        }
    }

    internal sealed partial class Pet
    {
        private readonly Dictionary<string, MiniScore> miniScores = new Dictionary<string, MiniScore>();
        private readonly Random miniRandom = new Random();
        private readonly MiniChat miniChat = new MiniChat();
        private readonly DispatcherTimer miniTicks = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        private MiniPane minis;
        private ChessPane chess;
        private volatile string miniContext = "", miniKind = "";
        private MiniLine miniLine;
        private int miniTicket;

        internal MiniPane Minis { get { return minis; } }
        internal ChessPane ChessShown { get { return chess; } }

        private void SetUpMinis()
        {
            Hands.Mini = delegate(ToolArguments args) { return Dispatcher.UIThread.Invoke(delegate { return MiniTool(args); }); };
            Hands.MiniInput = MiniInput;
            Hands.MiniBoard = delegate { return miniContext; };
            miniTicks.Tick += delegate { MiniTick(); };
        }

        private ToolArguments MiniInput(string text)
        {
            string kind = miniKind;
            if (kind.Length == 0) return null;
            if (kind == "chess") return ChessGame.Input(text);
            string action = System.Text.RegularExpressions.Regex.IsMatch(text, @"^(?:close|quit|exit|stop)(?: (?:the |our |your )?(?:board|mini ?game))?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? "close"
                : System.Text.RegularExpressions.Regex.IsMatch(text, @"^(?:again|play again|another round|new round|rematch)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? "again"
                : MiniGame.Input(kind, text) >= 0 ? "move" : null;
            return action == null ? null : new ToolArguments { Name = kind, Action = action, Text = text };
        }

        private void OpenBoard(BoardWindow board)
        {
            Screen screen = Screens.ScreenFromPoint(Position) ?? Screens.Primary;
            PixelRect work = screen != null ? screen.WorkingArea : new PixelRect(0, 0, 1920, 1080);
            double scale = screen != null ? screen.Scaling : 1;
            board.Topmost = !Saved.NotOnTop;
            board.Open(new PixelRect(Position, new PixelSize((int)(Bounds.Width * RenderScaling), (int)(Bounds.Height * RenderScaling))), work, scale);
            miniTicks.Start();
        }

        internal string MiniTool(ToolArguments args)
        {
            if (!Hands.Valid("mini_game", args)) return "Couldn't find that desktop board.";
            if (args.Action == "close") { CloseMinis(); return "Board's away, choom."; }
            if (args.Name == "chess") return ChessTool(args);
            if (args.Action == "move" && (minis == null || minis.Game.Kind != args.Name)) return "That board isn't open.";
            if (minis == null || minis.Game.Kind != args.Name)
            {
                CloseMinis();
                MiniScore score;
                if (!miniScores.TryGetValue(args.Name, out score)) { score = new MiniScore(); miniScores.Add(args.Name, score); }
                MiniPane opened = new MiniPane(new MiniGame(args.Name, score, miniRandom));
                minis = opened;
                miniKind = args.Name;
                opened.Played += MiniPlayed;
                opened.Closed += delegate { if (minis == opened) { minis = null; ClearMini(); } };
                OpenBoard(opened);
            }
            if (args.Action == "move")
            {
                int move = MiniGame.Input(args.Name, args.Text);
                if (move < 0) return "Pick a square, column or hand on this board.";
                if (!minis.PlayMove(move)) return minis.Game.Last;
                return miniContext;
            }
            if (args.Action == "again") minis.Again();
            MiniContext();
            return "Board's up, choom. " + minis.Game.Status;
        }

        private string ChessTool(ToolArguments args)
        {
            bool openedNow = chess == null;
            if (openedNow)
            {
                CloseMinis();
                MiniScore score;
                if (!miniScores.TryGetValue("chess", out score)) { score = new MiniScore(); miniScores.Add("chess", score); }
                ChessPane opened = new ChessPane(new ChessGame(score, args.Level, args.Side));
                chess = opened;
                miniKind = "chess";
                opened.Played += ChessPlayed;
                opened.Closed += delegate { if (chess == opened) { chess = null; ClearMini(); } };
                OpenBoard(opened);
            }
            string action = args.Action ?? "open";
            if (action == "open" && !openedNow)
            {
                if (!string.IsNullOrEmpty(args.Side) && (args.Side == "white" ? 1 : -1) != chess.Game.You) action = "again";
                else if (!string.IsNullOrEmpty(args.Level)) action = "level";
            }
            if (action != "open" && !(openedNow && action == "again"))
            {
                if (action == "undo" || action == "again" || action == "level") { miniTicket++; miniLine = null; miniChat.Clear(); }
                chess.Command(action, args.Level, args.Side);
            }
            MiniContext();
            return action == "hint" ? "Looking for a hint on the board." : chess.Game.Status;
        }

        private void MiniContext() { miniContext = chess != null ? chess.Game.BoardText() : minis == null ? "" : minis.Game.BoardText(); }

        private void ChessPlayed()
        {
            MiniContext();
            if (chess == null || chess.Game.Moment == null) return;
            if (chess.Game.Moment == "reset")
            {
                miniTicket++;
                miniLine = null;
                miniChat.Clear();
                chess.Game.Moment = null;
                return;
            }
            if (miniLine != null) { miniTicket++; miniLine = null; }
            if (chess.Game.Result != 0 && Program.Smoke == null) Hands.Animate(chess.Game.Result == 2 ? "grin" : "glitch");
            miniChat.Observe(chess.Game, miniRandom);
            chess.Game.Moment = null;
        }

        private void MiniPlayed()
        {
            MiniContext();
            if (minis == null) return;
            if (miniLine != null && miniLine.Result == 0) { miniTicket++; miniLine = null; }
            if (minis.Game.Last == "Your move, choom.") { miniChat.NewRound(); return; }
            if (minis.Game.Result != 0 && Program.Smoke == null) Hands.Animate(minis.Game.Result == 2 ? "grin" : "glitch");
            miniChat.Observe(minis.Game, MoveClock, miniRandom);
            if (miniChat.Count > 0 && miniLine != null) { miniTicket++; miniLine = null; }
        }

        private void MiniTick()
        {
            if (minis == null && chess == null) return;
            double now = MoveClock;
            if (miniLine == null)
            {
                MiniLine next = miniChat.Take(now);
                if (next == null) return;
                miniLine = next;
                next.Began = now;
                int ticket = ++miniTicket;
                if (next.Local || Program.Smoke != null || Brain.Model == null) { next.Said = next.Fallback; return; }
                ThreadPool.QueueUserWorkItem(delegate
                {
                    string said = null;
                    try { said = Brain.Clean(Brain.Aside(Brain.Character + "\nYou're playing this little board game with them. React to it in one short line, under twelve words.", next.Board, 40)); }
                    catch (Exception) { }
                    Dispatcher.UIThread.Post(delegate { if (ticket == miniTicket && miniLine == next && !string.IsNullOrWhiteSpace(said)) next.Said = said; else if (ticket == miniTicket && miniLine == next) next.Said = next.Fallback; });
                });
                return;
            }
            if (miniLine.Said == null || !Talk.Quiet || now < voiceDoneAt) return;
            MiniLine spoken = miniLine;
            miniLine = null;
            miniTicket++;
            miniChat.Last = now;
            string line = spoken.Said ?? spoken.Fallback;
            Brain.Remember("(Our desktop game: " + spoken.Board + ")", line);
            Reply(++asking, "", line);
        }

        private void ClearMini()
        {
            miniKind = "";
            miniTicket++;
            miniLine = null;
            miniChat.Clear();
            miniTicks.Stop();
            MiniContext();
        }

        internal void CloseMinis()
        {
            MiniPane closing = minis;
            minis = null;
            if (closing != null) closing.Close();
            ChessPane chessClosing = chess;
            chess = null;
            if (chessClosing != null) chessClosing.Close();
            ClearMini();
        }

        private bool HearMini(string text)
        {
            string tool;
            ToolArguments args;
            if (!Hands.Heard(text, out tool, out args) || tool != "mini_game") return false;
            int ticket = ++asking;
            string done = MiniTool(args);
            if (args.Action != "move")
            {
                Brain.Remember(text, done);
                Reply(ticket, text, done);
            }
            return true;
        }
    }
}
