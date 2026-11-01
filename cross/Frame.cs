using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace CapriDesktop
{
    // Capri Mainframe on Mac and Linux: Windows' panel in Windows' colours and bands, holding what these systems have. It's
    // another face on the same settings as her right-click menu, so every row calls what the menu calls and nothing here
    // keeps a setting of its own. Anything Windows-only, like voice chat, Gaming Mode or clips, isn't shown here at all.
    internal sealed class Frame : Window
    {
        internal static readonly Color Ground = Color.FromRgb(10, 10, 16);
        internal static readonly Color Band = Color.FromRgb(16, 12, 20);
        internal static readonly Color Rule = Color.FromRgb(44, 122, 134);
        internal static readonly Color Rose = Color.FromRgb(255, 93, 102);
        internal static readonly Color White = Color.FromRgb(236, 236, 241);
        internal static readonly Color Faint = Color.FromRgb(146, 130, 140);
        internal static readonly Color Field = Color.FromRgb(17, 16, 28);
        internal static readonly Color Edge = Color.FromRgb(92, 24, 36);
        internal static readonly Color Hot = Color.FromRgb(232, 66, 70);
        internal static readonly Color Cyan = Color.FromRgb(94, 240, 246);
        internal static readonly Color Deep = Color.FromRgb(9, 30, 33);
        private const double ColumnWide = 380, Name = 150;
        private readonly Pet pet;
        private readonly StackPanel left = new StackPanel { Width = ColumnWide, Margin = new Thickness(0, 0, 20, 0) };
        private readonly StackPanel right = new StackPanel { Width = ColumnWide };
        private readonly List<Action> refresh = new List<Action>();
        private readonly TextBlock brainNow = new TextBlock();
        private readonly ComboBox models = new ComboBox();
        private bool syncing;
        private string listedFor;

        internal Frame(Pet pet)
        {
            this.pet = pet;
            Title = "Capri Mainframe";
            Width = 840;
            Height = 760;
            MinWidth = 840;
            MinHeight = 360;
            Background = new SolidColorBrush(Ground);
            FontFamily = Look.Mono;
            FontSize = 13;
            Foreground = new SolidColorBrush(Rose);
            RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
            StackPanel page = new StackPanel { Margin = new Thickness(20, 16, 20, 20) };
            page.Children.Add(new TextBlock { Text = "CAPRI MAINFRAME", FontSize = 24, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Hot), Margin = new Thickness(0, 0, 0, 2) });
            page.Children.Add(new TextBlock { Text = Status, FontSize = 11, Foreground = new SolidColorBrush(Faint), Margin = new Thickness(0, 0, 0, 12) });
            StackPanel columns = new StackPanel { Orientation = Orientation.Horizontal };
            columns.Children.Add(left);
            columns.Children.Add(right);
            page.Children.Add(columns);
            Content = new ScrollViewer { Content = page, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
            BrainRows();
            VoiceRows();
            MoveRows();
            CapriRows();
            WatchRows();
            HabitRows();
            SecurityRows();
            // Closing it only hides it, so it opens again where they left it.
            Closing += delegate(object sender, WindowClosingEventArgs e) { if (!quitting) { e.Cancel = true; Hide(); } };
            Opened += delegate { Refresh(); };
        }

        private static string Status { get { return "v" + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(3) + " · " + (OperatingSystem.IsMacOS() ? "Mac" : "Linux"); } }

        private bool quitting;
        internal void Quit() { quitting = true; Close(); }

        // Every row reads its setting back from her, so the menu and this panel always agree.
        internal void Refresh()
        {
            syncing = true;
            foreach (Action row in refresh) row();
            syncing = false;
            ListModels();
        }

        // A section's band and title.
        private void Section(StackPanel column, string title)
        {
            Border band = new Border { Background = new SolidColorBrush(Band), BorderBrush = new SolidColorBrush(Rule), BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, column.Children.Count == 0 ? 0 : 16, 0, 6), Padding = new Thickness(8, 3) };
            band.Child = new TextBlock { Text = title + " >", FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(White) };
            column.Children.Add(band);
        }

        private Grid Row(StackPanel column, string name)
        {
            Grid row = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*"), Margin = new Thickness(8, 2), MinHeight = 26 };
            TextBlock label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTip.SetTip(label, name);
            row.Children.Add(label);
            column.Children.Add(row);
            return row;
        }

        private static void Second(Grid row, Control control)
        {
            Grid.SetColumn(control, 1);
            row.Children.Add(control);
        }

        // A setting with named steps.
        private void Stepped(StackPanel column, string name, string[] steps, Func<int> now, Action<int> picked)
        {
            Grid row = Row(column, name);
            Steps slider = new Steps(steps);
            slider.Picked += delegate(int at) { if (!syncing) picked(at); };
            refresh.Add(delegate { slider.At = now(); });
            Second(row, slider);
        }

        // An on or off setting's whole row: its name, then OFF and ON.
        private void Switch(StackPanel column, string name, Func<bool> now, Action<bool> changed)
        {
            Grid row = Row(column, name);
            OffOn toggle = new OffOn();
            toggle.Flipped += delegate(bool on) { if (!syncing) changed(on); };
            refresh.Add(delegate { toggle.On = now(); });
            Second(row, toggle);
        }

        private void Pressing(StackPanel column, string name, Action pressed)
        {
            Border key = Key(name, pressed);
            key.Margin = new Thickness(8, 3);
            column.Children.Add(key);
        }

        private static Border Key(string name, Action pressed)
        {
            Border key = new Border { Background = new SolidColorBrush(Field), BorderBrush = new SolidColorBrush(Edge), BorderThickness = new Thickness(1), Padding = new Thickness(8, 5), Cursor = new Cursor(StandardCursorType.Hand) };
            key.Child = new TextBlock { Text = name, Foreground = new SolidColorBrush(White), TextTrimming = TextTrimming.CharacterEllipsis };
            key.PointerEntered += delegate { key.BorderBrush = new SolidColorBrush(Hot); };
            key.PointerExited += delegate { key.BorderBrush = new SolidColorBrush(Edge); };
            key.PointerReleased += delegate(object sender, PointerReleasedEventArgs e) { if (e.InitialPressMouseButton == MouseButton.Left) pressed(); };
            return key;
        }

        private void Readout(StackPanel column, Func<string> now)
        {
            TextBlock line = new TextBlock { Foreground = new SolidColorBrush(Cyan), FontSize = 12, Margin = new Thickness(8, 2, 8, 4), TextWrapping = TextWrapping.Wrap };
            refresh.Add(delegate { line.Text = now(); });
            column.Children.Add(line);
        }

        private static int Nearest(int[] list, int value)
        {
            int best = 0;
            for (int i = 1; i < list.Length; i++) if (Math.Abs(list[i] - value) < Math.Abs(list[best] - value)) best = i;
            return best;
        }

        private void BrainRows()
        {
            Section(left, "BRAIN");
            brainNow.Foreground = new SolidColorBrush(Cyan);
            brainNow.FontSize = 12;
            brainNow.Margin = new Thickness(8, 2, 8, 4);
            brainNow.TextWrapping = TextWrapping.Wrap;
            refresh.Add(delegate { brainNow.Text = pet.BrainNow; });
            left.Children.Add(brainNow);
            Grid modelRow = Row(left, "Model");
            models.HorizontalAlignment = HorizontalAlignment.Stretch;
            models.Background = new SolidColorBrush(Field);
            models.BorderBrush = new SolidColorBrush(Edge);
            models.Foreground = new SolidColorBrush(White);
            foreach (string look in new[] { "ComboBoxBackground", "ComboBoxBackgroundPointerOver", "ComboBoxBackgroundPressed", "ComboBoxBackgroundUnfocused", "ComboBoxDropDownBackground" }) models.Resources[look] = new SolidColorBrush(Field);
            foreach (string look in new[] { "ComboBoxForeground", "ComboBoxForegroundFocused", "ComboBoxItemForeground" }) models.Resources[look] = new SolidColorBrush(White);
            models.SelectionChanged += delegate
            {
                string model = models.SelectedItem as string;
                if (!syncing && model != null) pet.PickModel(model);
            };
            Second(modelRow, models);
            Pressing(left, Pet.EndpointChoice, delegate { pet.AskEndpoint(); });
            Pressing(left, Pet.KeyChoice, delegate { pet.AskBrainKey(); });
            Pressing(left, Pet.KiloChoice, delegate { pet.AskKiloKey(); });
            Pressing(left, "Get a free NVIDIA key", delegate { pet.GetFreeKey(); });
            Pressing(left, "Download a model (Ollama or Hugging Face)...", delegate { pet.AskPull(); });
            Pressing(left, "Forget the key and use Ollama", delegate { if (pet.Brain.Remote != null) pet.ForgetBrainKey(); else if (pet.Brain.Server != null) pet.LeaveServer(); });
            string[] languages = new string[Brain.Languages.GetLength(0)];
            for (int i = 0; i < languages.Length; i++) languages[i] = Brain.Languages[i, 0];
            Stepped(left, "Language", languages, delegate
            {
                string code = Brain.LanguageCode(pet.Saved.Language);
                for (int i = 0; i < languages.Length; i++) if (Brain.Languages[i, 2] == code) return i;
                return 0;
            }, delegate(int at) { pet.SetLanguage(Brain.Languages[at, 1]); });
            Stepped(left, "Replies", Pet.Lengths, delegate { return pet.Saved.Replies; }, delegate(int at) { pet.SetReplies(at); });
            Stepped(left, "Memory", Pet.Lengths, delegate { return pet.Saved.Recall == 32 ? 2 : pet.Saved.Recall == 16 ? 1 : 0; }, delegate(int at) { pet.SetRecall(new[] { 8, 16, 32 }[at]); });
            Switch(left, "Stay on my model", delegate { return pet.Saved.StayOnModel; }, delegate(bool on) { pet.SetStay(on); });
            Section(left, "HANDS");
            Readout(left, delegate { return Laya.Url == null ? "Her brain picks its own steps." : "Her brain gives the orders. Laya, at " + Laya.Url + ", picks each step."; });
            Switch(left, "Laya", delegate { return Laya.Url != null; }, delegate(bool on) { pet.SetHands(on ? Laya.Address : null); Refresh(); });
            Pressing(left, "Laya's address...", delegate { pet.AskHands(); });
        }

        private void VoiceRows()
        {
            Section(left, "VOICE");
            Readout(left, delegate { return pet.VoiceStatus; });
            string[] names = { "ElevenLabs", "Local server", "Off" };
            string[] providers = { Speakers.ElevenLabs, Speakers.Local, Speakers.Off };
            Stepped(left, "Provider", names, delegate { return Array.IndexOf(providers, Speakers.Known(pet.Saved.VoiceProvider)); }, delegate(int at) { pet.SetVoiceProvider(providers[at]); Refresh(); });
            Switch(left, "Speak replies", delegate { return pet.Speaks; }, delegate(bool on) { pet.SetSpeak(on); Refresh(); });
            Pressing(left, "Local server's address...", delegate { pet.AskLocalVoice("url"); });
            Pressing(left, "Local server's model...", delegate { pet.AskLocalVoice("model"); });
            Pressing(left, "Local server's voice...", delegate { pet.AskLocalVoice("voice"); });
            Pressing(left, "Add ElevenLabs key...", delegate { pet.AskVoiceKey(); });
            Pressing(left, "Test voice", delegate { pet.HearStyle(); });
            Stepped(left, "Volume", Pet.VolumeNames, delegate { return Nearest(Pet.Volumes, Voice.Loudness); }, delegate(int at) { pet.SetVolume(Pet.Volumes[at]); });
            Stepped(left, "Pitch", Pet.PitchNames, delegate { return VoiceFx.Pitch + 6; }, delegate(int at) { pet.SetPitch(at - 6); });
            Stepped(left, "Speed", Pet.PaceNames, delegate { return Nearest(Pet.Paces, VoiceFx.Speed); }, delegate(int at) { pet.SetPace(Pet.Paces[at]); });
            Stepped(left, "Robot", Pet.AmountNames, delegate { return Nearest(Pet.Amounts, VoiceFx.Robot); }, delegate(int at) { pet.SetRobot(Pet.Amounts[at]); });
            Stepped(left, "Glitch", Pet.AmountNames, delegate { return Nearest(Pet.Amounts, VoiceFx.Glitch); }, delegate(int at) { pet.SetGlitch(Pet.Amounts[at]); });
            Stepped(left, "Sounds", Pet.SoundNames, delegate { return Nearest(Pet.SoundLevels, Sfx.Level); }, delegate(int at) { pet.SetSounds(Pet.SoundLevels[at]); });
            Switch(left, "Typing sound", delegate { return pet.TypingSound; }, delegate(bool on) { pet.SetTyping(on); });
        }

        private void MoveRows()
        {
            Section(right, "MOVES");
            Stepped(right, "Wander", Pet.WanderNames, delegate { return pet.Saved.Wander; }, delegate(int at) { pet.SetWander(at); });
            Stepped(right, "Sleep", Pet.SleepNames, delegate { return Math.Max(0, Array.IndexOf(Pet.Sleeps, pet.SleepAfter)); }, delegate(int at) { pet.SetSleepAfter(Pet.Sleeps[at]); });
            Stepped(right, "Pop-ins", Pet.PopNames, delegate { return pet.PopPace; }, delegate(int at) { pet.SetPopPace(at); });
            Switch(right, "Dance to music", delegate { return !pet.Saved.NoDance; }, delegate(bool on) { pet.SetDanceToMusic(on); });
            Switch(right, "Glitch in at startup", delegate { return !pet.Saved.NoIntro; }, delegate(bool on) { pet.SetIntro(on); });
        }

        private void CapriRows()
        {
            Section(right, "CAPRI");
            Stepped(right, "Size", Pet.SizeNames, delegate { int best = 0; for (int i = 1; i < Pet.Sizes.Length; i++) if (Math.Abs(Pet.Sizes[i] - pet.Saved.Size) < Math.Abs(Pet.Sizes[best] - pet.Saved.Size)) best = i; return best; }, delegate(int at) { pet.Resize(Pet.Sizes[at]); });
            Switch(right, "Check in on me", delegate { return !pet.Saved.NoCheckIns; }, delegate(bool on) { pet.SetCheckIns(on); });
            Switch(right, "Always on top", delegate { return pet.OnTop; }, delegate(bool on) { pet.SetOnTop(on); });
            Stepped(right, "Bubble", Pet.BubbleNames, delegate { return pet.BubbleTime; }, delegate(int at) { pet.SetBubbleTime(at); });
            Pressing(right, "What should she call you?", delegate { pet.AskNickname(); });
            Pressing(right, "Get to know me", delegate { pet.GetToKnow(); });
        }

        private void WatchRows()
        {
            Section(right, "WATCH");
            Switch(right, "Watch my screen", delegate { return pet.Saved.Watch; }, delegate(bool on) { pet.SetWatch(on); });
            Switch(right, "Remember what she sees", delegate { return !pet.Saved.NoCaughtUp; }, delegate(bool on) { pet.SetCaughtUp(on); });
            Stepped(right, "Every", Pet.GlanceGapNames, delegate { return Math.Max(0, Array.IndexOf(Pet.GlanceGaps, pet.GlanceGap)); }, delegate(int at) { pet.SetGlanceGap(Pet.GlanceGaps[at]); });
        }

        private void HabitRows()
        {
            Section(right, "HABITS");
            Switch(right, OperatingSystem.IsMacOS() ? "Start with my Mac" : "Start with my PC", delegate { return pet.StartsAtLogin; }, delegate(bool on) { pet.SetLogin(on); });
            Switch(right, "Show on Discord", delegate { return !pet.Saved.NoDiscord; }, delegate(bool on) { pet.SetDiscord(on); });
            Switch(right, "Remember our chats", delegate { return !pet.Saved.NoMemories; }, delegate(bool on) { pet.SetRemembering(on); });
            Pressing(right, "Open her notes", delegate { pet.OpenNotes(); });
        }

        private void SecurityRows()
        {
            Section(right, "SECURITY");
            Readout(right, delegate { return Security.PresetLine(Security.Preset); });
            string[] presets = new string[Security.Custom];
            Array.Copy(Security.Presets, presets, presets.Length);
            Stepped(right, "Preset", presets, delegate { return Math.Min(Security.Preset, presets.Length - 1); }, delegate(int at) { Security.Pick(at); Refresh(); });
            for (int i = 0; i < Security.Count; i++)
            {
                if (!Security.Has(i)) continue;
                int toggle = i;
                Switch(right, Security.Names[i], delegate { return Security.On(toggle); }, delegate(bool on) { Security.Set(toggle, on); Refresh(); });
            }
        }

        // Her brain's models go in the box: Ollama's, a key's or an endpoint's. It only lists them again when the brain changes.
        private void ListModels()
        {
            Cloud remote = pet.Brain.Remote;
            string server = pet.Brain.Server;
            string source = remote != null ? remote.Key : server ?? "ollama";
            string model = pet.Brain.Model;
            syncing = true;
            if (listedFor == source)
            {
                models.SelectedItem = model;
                syncing = false;
                return;
            }
            models.ItemsSource = model == null ? new List<string>() : new List<string> { model };
            models.SelectedItem = model;
            syncing = false;
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<string> found = null;
                try { found = remote != null ? Brain.CloudModels(remote.Key) : server != null ? new List<string>(Brain.ServerModels(server)) : new List<string>(Brain.InstalledModels()); }
                catch (Exception) { }
                Dispatcher.UIThread.Post(delegate
                {
                    if (found == null) return;
                    listedFor = source;
                    syncing = true;
                    models.ItemsSource = found;
                    models.SelectedItem = pet.Brain.Model;
                    syncing = false;
                });
            });
        }

        // Windows' slider with a value box: a click anywhere picks that step, the wheel moves one, and the step's name shows.
        private sealed class Steps : Control
        {
            private readonly string[] names;
            private int at;
            internal event Action<int> Picked;

            internal Steps(string[] names)
            {
                this.names = names;
                Height = 22;
                Cursor = new Cursor(StandardCursorType.Hand);
                PointerPressed += delegate(object sender, PointerPressedEventArgs e) { Choose(e.GetPosition(this).X); };
                PointerMoved += delegate(object sender, PointerEventArgs e) { if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) Choose(e.GetPosition(this).X); };
                PointerWheelChanged += delegate(object sender, PointerWheelEventArgs e) { Set(at + (e.Delta.Y > 0 ? 1 : -1)); e.Handled = true; };
            }

            internal int At { get { return at; } set { at = Math.Max(0, Math.Min(names.Length - 1, value)); InvalidateVisual(); } }

            private void Choose(double x)
            {
                double track = Math.Max(1, Bounds.Width - 86);
                Set(names.Length < 2 ? 0 : (int)Math.Round(Math.Max(0, Math.Min(1, x / track)) * (names.Length - 1)));
            }

            private void Set(int step)
            {
                step = Math.Max(0, Math.Min(names.Length - 1, step));
                if (step == at) return;
                at = step;
                InvalidateVisual();
                if (Picked != null) Picked(step);
            }

            public override void Render(DrawingContext graphics)
            {
                double track = Math.Max(1, Bounds.Width - 86), middle = Bounds.Height / 2;
                double done = names.Length < 2 ? 0 : track * at / (names.Length - 1);
                graphics.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
                graphics.FillRectangle(new SolidColorBrush(Edge), new Rect(0, middle - 2, track, 4));
                graphics.FillRectangle(new SolidColorBrush(Hot), new Rect(0, middle - 2, done, 4));
                graphics.FillRectangle(new SolidColorBrush(Rose), new Rect(Math.Max(0, done - 3), middle - 8, 6, 16));
                Rect box = new Rect(track + 8, 0, Bounds.Width - track - 8, Bounds.Height);
                graphics.DrawRectangle(new SolidColorBrush(Field), new Pen(new SolidColorBrush(Edge)), box);
                FormattedText shown = new FormattedText(names[at], System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Look.Regular, 12, new SolidColorBrush(White)) { MaxTextWidth = box.Width - 4, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                graphics.DrawText(shown, new Point(box.X + Math.Max(2, (box.Width - shown.Width) / 2), (Bounds.Height - shown.Height) / 2));
            }
        }

        // Windows' OFF and ON: the lit word is where it is now, and a click anywhere flips it.
        private sealed class OffOn : Control
        {
            private bool on;
            internal event Action<bool> Flipped;

            internal OffOn()
            {
                Height = 22;
                Cursor = new Cursor(StandardCursorType.Hand);
                PointerPressed += delegate
                {
                    On = !on;
                    if (Flipped != null) Flipped(on);
                };
            }

            internal bool On { get { return on; } set { on = value; InvalidateVisual(); } }

            public override void Render(DrawingContext graphics)
            {
                graphics.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
                Word(graphics, "OFF", 0, !on, Rose);
                Word(graphics, "ON", 50, on, Cyan);
            }

            private void Word(DrawingContext graphics, string word, double x, bool lit, Color colour)
            {
                Rect box = new Rect(x, 0, 46, Bounds.Height);
                graphics.DrawRectangle(new SolidColorBrush(lit ? (word == "ON" ? Deep : Color.FromRgb(46, 22, 38)) : Field), new Pen(new SolidColorBrush(lit ? colour : Edge)), box);
                FormattedText text = new FormattedText(word, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Look.Regular, 12, new SolidColorBrush(lit ? colour : Faint));
                graphics.DrawText(text, new Point(x + (46 - text.Width) / 2, (Bounds.Height - text.Height) / 2));
            }
        }
    }
}
