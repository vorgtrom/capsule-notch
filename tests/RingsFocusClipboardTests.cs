using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Capsule
{
    // v1.7.0: ring switches, each session's model and tokens, the focus timer, clipboard history, and the capsule left
    // alone when nothing on it changed.
    public static class RingsFocusClipboardTests
    {
        static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
        const long Now = 1792000000000;

        public static void Run()
        {
            Rings();
            Tally();
            Focus();
            Clipboard();
            Panel();
            Cells();
        }

        static void Rings()
        {
            var c = new Config();
            foreach (string name in Config.RingNames) TestRunner.Check(c.RingOn(name), "a new config has every ring on: " + name);
            TestRunner.Check(c.ClipboardHistory, "clipboard history is on by default");
            TestRunner.Eq(25, c.FocusMinutes, "25 min focus by default");

            string path = Path.Combine(TestRunner.NewTempDir(), "config.json");
            c.Rings["codex"] = false;
            c.Rings["music"] = false;
            c.ClipboardHistory = false;
            c.FocusMinutes = 50;
            c.LongBreakMinutes = 20;
            TestRunner.Check(c.Save(path), "saved");
            Config back = Config.Load(path);
            TestRunner.Check(!back.RingOn("codex") && !back.RingOn("music") && back.RingOn("claude") && back.RingOn("calendar"), "the switches survive a restart");
            TestRunner.Check(!back.ClipboardHistory, "clipboard history off survives a restart");
            TestRunner.Eq(50, back.FocusMinutes, "focus length survives a restart");
            TestRunner.Eq(20, back.LongBreakMinutes, "long break survives a restart");
            TestRunner.Eq(5, back.ShortBreakMinutes, "an untouched length keeps its default");

            var one = new Config();
            foreach (string name in Config.RingNames) if (name != "focus") one.Rings[name] = false;
            TestRunner.Check(!one.CanTurnOff("focus"), "the last ring on can't be switched off");
            TestRunner.Check(one.CanTurnOff("claude"), "a ring already off can always be asked about");
            one.Rings["claude"] = true;
            TestRunner.Check(one.CanTurnOff("focus"), "with another ring on, it can");

            File.WriteAllText(path, "{\"rings\":{\"claude\":false,\"codex\":false,\"calendar\":false,\"music\":false,\"focus\":false},\"focus_minutes\":999,\"short_break_minutes\":0}");
            Config all = Config.Load(path);
            TestRunner.Check(all.RingOn("claude"), "every ring off by hand: Claude comes back on");
            TestRunner.Eq(Config.MaxFocusMinutes, all.FocusMinutes, "a focus length past the limit is cut to it");
            TestRunner.Eq(1, all.ShortBreakMinutes, "a break shorter than a minute becomes one");
        }

        static string Reply(string id, string request, string model, int input, int created, int output, int read)
        {
            return "{\"type\":\"assistant\",\"timestamp\":\"2026-10-19T10:00:00.000Z\",\"requestId\":\"" + request + "\",\"message\":{\"id\":\"" + id
                + "\",\"model\":\"" + model + "\",\"content\":[{\"type\":\"text\",\"text\":\"never kept\"}],\"usage\":{\"input_tokens\":" + input
                + ",\"cache_creation_input_tokens\":" + created + ",\"output_tokens\":" + output + ",\"cache_read_input_tokens\":" + read + "}}}";
        }

        static void Tally()
        {
            string claudeDir = TestRunner.NewTempDir();
            string project = Path.Combine(claudeDir, "projects", "C--work-capsule");
            Directory.CreateDirectory(project);
            const string id = "0f6c2a8e-1111-4b2c-9d3e-abcdefabcdef";
            string transcript = Path.Combine(project, id + ".jsonl");
            File.WriteAllText(transcript,
                "{\"type\":\"user\",\"message\":{\"content\":\"fix the bug\"}}\n" +
                Reply("msg_1", "req_1", "claude-sonnet-4-5-20250929", 100, 2000, 300, 50000) + "\n" +
                Reply("msg_1", "req_1", "claude-sonnet-4-5-20250929", 100, 2000, 300, 50000) + "\n" +   // the same reply, written twice
                Reply("msg_2", "req_2", "claude-opus-4-8", 10, 0, 90, 1000) + "\n");
            var tally = new SessionTally(claudeDir);
            var s = new SessionStatus { SessionId = id, State = States.Working, At = Now };
            var list = new List<SessionStatus> { s };
            TestRunner.Check(tally.Update(list, Now), "a new session's transcript is read");
            SessionUse use = tally.For(id);
            TestRunner.Eq(2500L, use.Tokens, "input, cache writes and output; no cache reads; a reply counted once");
            TestRunner.Eq("claude-opus-4-8", use.Model, "the latest reply's model");
            TestRunner.Eq("Opus 4.8 · 2.5K tokens", use.Text, "the row's text");

            File.AppendAllText(transcript, Reply("msg_3", "req_3", "claude-opus-4-8", 0, 0, 500, 0) + "\n");
            s.At = Now + 1000;
            TestRunner.Check(!tally.Update(list, Now + 1000), "while it works, not read again within 10 s");
            TestRunner.Check(tally.Update(list, Now + SessionTally.EveryMs + 1000), "read again once 10 s have passed");
            TestRunner.Eq(3000L, tally.For(id).Tokens, "only the new line is added");
            TestRunner.Check(!tally.Update(list, Now + 3 * SessionTally.EveryMs), "no event since: not read again");

            File.AppendAllText(transcript, Reply("msg_4", "req_4", "claude-opus-4-8", 0, 0, 1000, 0) + "\n");
            s.State = States.Idle;
            s.At = Now + 3 * SessionTally.EveryMs + 1;
            TestRunner.Check(tally.Update(list, Now + 3 * SessionTally.EveryMs + 2), "a session that stopped working is read at once");
            TestRunner.Eq(4000L, tally.For(id).Tokens, "with its last reply");

            TestRunner.Check(!tally.Update(new List<SessionStatus>(), Now), "nothing to read");
            TestRunner.Check(tally.For(id) == null, "a session that is gone is forgotten");
            TestRunner.Check(!SessionTally.SafeId("..\\..\\secrets"), "a session id can't climb out of the projects folder");
            TestRunner.Check(!SessionTally.SafeId(""), "an empty id names nothing");
            TestRunner.Check(SessionTally.SafeId(id), "a real session id is fine");
            var missing = new SessionStatus { SessionId = "no-such-session", State = States.Idle, At = Now };
            TestRunner.Check(!tally.Update(new List<SessionStatus> { missing }, Now), "no transcript: nothing, and no throw");
            TestRunner.Check(tally.For("no-such-session").Text == "", "and its row shows nothing extra");

            int more;
            List<SessionRow> rows = SessionRow.ListFor(new List<SessionStatus> { new SessionStatus { SessionId = "a", State = States.Working, Project = "web", At = Now, Since = Now } }, Now, out more,
                sid => new SessionUse { Model = "claude-opus-4-8", Tokens = 1200000 });
            TestRunner.Eq("Opus 4.8 · 1.2M tokens", rows[0].Usage, "a row carries its model and tokens");
            var panel = new StackPanel();
            panel.Children.Add(CardView.SessionLine(rows[0]));
            Lay(panel);
            TestRunner.Check(Texts(panel).Contains("Opus 4.8 · 1.2M tokens"), "the row draws them");
        }

        static void Focus()
        {
            var t = new FocusTimer();
            t.SetLengths(25, 5, 15);
            TestRunner.Check(!t.Active, "a new timer is stopped");
            t.Start(Now);
            TestRunner.Check(t.Running && t.Phase == FocusPhase.Focus && t.Round == 1, "Start: focus 1");
            TestRunner.Eq(25 * 60000L, t.LeftMs(Now), "25 minutes left");
            TestRunner.Check(t.Tick(Now + 60000) == null, "nothing ends early");
            TestRunner.Eq(FocusPhase.Focus, t.Tick(Now + 25 * 60000).Value, "focus ends on time");
            TestRunner.Check(t.Phase == FocusPhase.ShortBreak && t.Running, "and the break starts by itself");
            TestRunner.Eq("Focus done. Take a 5 min break.", t.EndedText(FocusPhase.Focus), "what the notification says");
            long at = Now + 25 * 60000;
            at += 5 * 60000;
            TestRunner.Eq(FocusPhase.ShortBreak, t.Tick(at).Value, "the break ends");
            TestRunner.Check(t.Phase == FocusPhase.Focus && t.Round == 2, "focus 2 of 4");
            TestRunner.Eq("Break's over. Focus 2 of 4 has started.", t.EndedText(FocusPhase.ShortBreak), "what that notification says");
            t.Skip(at);
            t.Skip(at);
            t.Skip(at);
            t.Skip(at);
            TestRunner.Check(t.Phase == FocusPhase.Focus && t.Round == 4, "skipping moves on a part at a time");
            t.Skip(at);
            TestRunner.Eq(FocusPhase.LongBreak, t.Phase, "after the 4th focus, a long break");
            TestRunner.Eq(15 * 60000L, t.LeftMs(at), "15 minutes of it");
            t.Skip(at);
            TestRunner.Check(t.Phase == FocusPhase.Focus && t.Round == 1, "then a new set");

            t.Pause(at + 60000);
            TestRunner.Check(t.Paused && !t.Running, "paused");
            TestRunner.Eq(24 * 60000L, t.LeftMs(at + 10 * 60000), "the time left stands still while paused");
            TestRunner.Check(t.Tick(at + 60 * 60000) == null, "a paused timer never ends");
            t.Resume(at + 10 * 60000);
            TestRunner.Eq(24 * 60000L, t.LeftMs(at + 10 * 60000), "resumed with what was left");
            t.Pause(at + 10 * 60000);
            t.Skip(at + 10 * 60000);
            TestRunner.Check(t.Paused && t.Phase == FocusPhase.ShortBreak, "skipping while paused stays paused");
            TestRunner.Eq(5 * 60000L, t.LeftMs(at + 99 * 60000), "at the next part's full length");
            t.Stop();
            TestRunner.Check(!t.Active && t.Round == 1 && t.Phase == FocusPhase.Focus, "Stop: back to the start");

            TestRunner.Eq("18:42", FocusTimer.Clock(18 * 60000 + 42000), "minutes and seconds");
            TestRunner.Eq("0:01", FocusTimer.Clock(1), "a part of a second left shows as one");
            TestRunner.Eq("65m", FocusTimer.Clock(65 * 60000), "an hour or more: minutes only, to fit the ring");

            t.Start(Now);
            CellModel cell = t.Cell(Now);
            TestRunner.Eq("focus", cell.Provider, "the Focus ring");
            TestRunner.Eq(FocusTimer.Glyph, cell.Glyph, "with the stopwatch");
            TestRunner.Near(100, cell.Used, "a full arc at the start");
            TestRunner.Eq(Palette.Red, cell.ArcColor, "red while you focus");
            TestRunner.Near(50, t.Cell(Now + 750000).Used, "half the arc halfway");
            t.Skip(Now);
            TestRunner.Eq(Palette.Green, t.Cell(Now).ArcColor, "green on a break");
            t.Pause(Now);
            TestRunner.Check(t.Cell(Now).Dimmed, "dimmed while paused");

            var idle = new FocusTimer();
            FocusTile ready = FocusTile.From(idle, Now, En);
            TestRunner.Eq("Ready", ready.Heading, "a stopped timer's tile");
            TestRunner.Eq("25:00", ready.Time, "shows the focus length");
            idle.Start(Now);
            FocusTile running = FocusTile.From(idle, Now, En);
            TestRunner.Eq("Focus · 1 of 4", running.Heading, "a running timer's tile names the part and round");
            TestRunner.Check(running.Note.StartsWith("until ", StringComparison.Ordinal), "and when it ends");
        }

        static void Clipboard()
        {
            ClipItem link = ClipboardHistory.FromText("https://github.com/vorgtrom/capsule-notch/pulls?utm_source=x&ref=y", Now);
            TestRunner.Eq(ClipKind.Link, link.Kind, "a URL is a link");
            TestRunner.Eq("github.com/vorgtrom/capsule-notch/pulls", link.Preview, "shown by host and path, without the query");
            TestRunner.Check(link.Text.Contains("utm_source"), "but copied back whole");
            TestRunner.Eq(ClipKind.Text, ClipboardHistory.FromText("see https://x.com later", Now).Kind, "a sentence with a link in it is text");
            TestRunner.Eq(ClipKind.Text, ClipboardHistory.FromText("ftp://files.example.com/a", Now).Kind, "only web links count as links");

            ClipItem hex = ClipboardHistory.FromText(" #30d158 ", Now);
            TestRunner.Eq(ClipKind.Colour, hex.Kind, "a hex colour");
            TestRunner.Eq("#30D158", hex.Swatch, "and its swatch");
            TestRunner.Eq("#AABBCC", ClipboardHistory.Colour("#abc"), "short hex");
            TestRunner.Eq("#112233", ClipboardHistory.Colour("#11223380"), "hex with alpha: the colour part");
            TestRunner.Eq("#FF453A", ClipboardHistory.Colour("rgb(255, 69, 58)"), "rgb()");
            TestRunner.Eq("#FF453A", ClipboardHistory.Colour("rgba(255,69,58,0.5)"), "rgba()");
            TestRunner.Check(ClipboardHistory.Colour("rgb(300,0,0)") == null, "not a colour past 255");
            TestRunner.Check(ClipboardHistory.Colour("#12345") == null, "not a colour with 5 digits");

            ClipItem text = ClipboardHistory.FromText("line one\r\n\r\n   line two", Now);
            TestRunner.Eq("line one line two", text.Preview, "text is one line in the list");
            TestRunner.Check(ClipboardHistory.FromText("   ", Now) == null, "blank: not kept");
            TestRunner.Check(ClipboardHistory.FromText(new string('x', ClipboardHistory.MaxTextChars + 1), Now) == null, "too long: not kept");
            TestRunner.Check(ClipboardHistory.FromText(new string('x', 500), Now).Preview.Length <= ClipboardHistory.PreviewChars, "a long preview is cut");

            var h = new ClipboardHistory();
            TestRunner.Check(h.Add(ClipboardHistory.FromText("a", Now)), "added");
            h.Add(ClipboardHistory.FromText("b", Now));
            TestRunner.Check(!h.Add(ClipboardHistory.FromText("b", Now)), "the same copy twice in a row: no change");
            TestRunner.Check(h.Add(ClipboardHistory.FromText("a", Now)), "an older copy again");
            TestRunner.Eq("a", h.Items[0].Text, "moves to the top");
            TestRunner.Eq(2, h.Items.Count, "and isn't listed twice");
            for (int i = 0; i < 40; i++) h.Add(ClipboardHistory.FromText("n" + i, Now));
            TestRunner.Eq(ClipboardHistory.MaxItems, h.Items.Count, "at most 25 kept");
            TestRunner.Eq("n39", h.Items[0].Text, "newest first");
            for (int i = 0; i < 8; i++) h.Add(new ClipItem { Kind = ClipKind.Image, Key = "i:" + i, Preview = "img" });
            TestRunner.Eq(ClipboardHistory.MaxImages, h.Items.Count(i => i.Kind == ClipKind.Image), "at most 5 images");
            TestRunner.Eq("i:7", h.Items[0].Key, "the newest images stay");
            TestRunner.Eq(ClipboardHistory.MaxImages, h.Filtered(ClipKind.Image).Count, "filtered by kind");
            TestRunner.Eq(h.Items.Count, h.Filtered(null).Count, "or not at all");
            h.Clear();
            TestRunner.Eq(0, h.Items.Count, "Clear empties it");

            var none = new HashSet<string> { "UnicodeText" };
            TestRunner.Check(!ClipboardHistory.Private(none, f => null), "an ordinary copy is kept");
            TestRunner.Check(ClipboardHistory.Private(new HashSet<string> { "UnicodeText", "ExcludeClipboardContentFromMonitorProcessing" }, f => null), "a password manager's copy is skipped");
            TestRunner.Check(ClipboardHistory.Private(new HashSet<string> { "Clipboard Viewer Ignore" }, f => null), "so is one that asks viewers to ignore it");
            var marked = new HashSet<string> { "UnicodeText", "CanIncludeInClipboardHistory" };
            TestRunner.Check(ClipboardHistory.Private(marked, f => new MemoryStream(BitConverter.GetBytes(0))), "kept out of Windows' own history: skipped too");
            TestRunner.Check(!ClipboardHistory.Private(marked, f => new MemoryStream(BitConverter.GetBytes(1))), "allowed into it: kept");
            TestRunner.Check(ClipboardHistory.Private(marked, f => { throw new InvalidOperationException(); }), "can't tell: skipped");

            var pixels = new byte[8 * 6 * 4];
            for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 200; pixels[i + 3] = 255; }
            BitmapSource image = BitmapSource.Create(8, 6, 96, 96, PixelFormats.Bgra32, null, pixels, 8 * 4);
            ClipItem shot = ClipboardHistory.FromImage(image, Now);
            TestRunner.Eq(ClipKind.Image, shot.Kind, "an image");
            TestRunner.Eq("8 × 6", shot.Preview, "named by its size");
            TestRunner.Check(shot.Png.Length > 0 && shot.Thumb != null && shot.Thumb.IsFrozen, "kept as PNG with a frozen thumbnail");
            BitmapSource back = ClipboardHistory.Decode(shot.Png);
            TestRunner.Check(back.PixelWidth == 8 && back.PixelHeight == 6, "decodes to the same size");
            TestRunner.Eq(shot.Key, ClipboardHistory.FromImage(image, Now).Key, "the same image again has the same key");
        }

        static void Panel()
        {
            var codex = new Reading { Provider = "codex", Status = "ok", HeadlineId = "five_hour", DataAtMs = Now };
            codex.Windows.Add(new LimitWindow { Id = "five_hour", Label = "5-hour", Used = 8, ResetsAtMs = Now + 3600000 });
            var timer = new FocusTimer();
            timer.Start(Now);
            var clips = new ClipboardTile();
            clips.Items.Add(ClipboardHistory.FromText("#30D158", Now));
            clips.Items.Add(ClipboardHistory.FromText("https://example.com/a?b=c", Now));
            for (int i = 0; i < 6; i++) clips.Items.Add(ClipboardHistory.FromText("note " + i, Now));
            var view = new PanelView();
            view.Update(new PanelModel
            {
                Codex = UsageTile.From(codex, Now, En),
                Focus = FocusTile.From(timer, Now, En),
                Clipboard = clips,
                Ideas = new IdeasTile(),
            });
            Lay(view);
            List<string> texts = Texts(view);
            TestRunner.Check(!texts.Contains("Sessions"), "Claude off: no Sessions tile");
            TestRunner.Check(texts.Contains("Codex"), "Codex alone");
            TestRunner.Check(texts.Contains("Focus") && texts.Contains("25:00") && texts.Contains("Pause") && texts.Contains("Skip") && texts.Contains("Stop"), "a running Focus tile with its buttons");
            TestRunner.Check(texts.Contains("Clipboard") && texts.Contains("Colours") && texts.Contains("example.com/a"), "the Clipboard tile, its filter and a link by host");
            TestRunner.Check(texts.Contains("Show all 8") && !texts.Contains("note 5"), "5 rows, then Show all");
            Border codexTile = FindTile(view, "Codex");
            TestRunner.Eq(3, codexTile == null ? 0 : Grid.GetColumnSpan(codexTile), "Codex alone takes the whole row");

            string command = null;
            view.FocusCommand += delegate(string what) { command = what; };
            Click(view, "Pause");
            TestRunner.Eq("pause", command, "Pause asks the timer to pause");
            ClipItem copied = null;
            view.ClipCopyRequested += delegate(ClipItem item) { copied = item; };
            Click(view, "example.com/a");
            TestRunner.Check(copied != null && copied.Text == "https://example.com/a?b=c", "a click copies the whole link back");
            Click(view, "Links");
            Lay(view);
            texts = Texts(view);
            TestRunner.Check(texts.Contains("example.com/a") && !texts.Contains("note 0"), "the Links filter shows links only");

            view.Update(new PanelModel { Codex = UsageTile.From(codex, Now, En), Ideas = new IdeasTile() });
            Lay(view);
            texts = Texts(view);
            TestRunner.Check(!texts.Contains("Focus") && !texts.Contains("Clipboard"), "switched off: no Focus or Clipboard tile");

            var c = new Config();
            foreach (string name in Config.RingNames) if (name != "claude") c.Rings[name] = false;
            view.ShowRings(c);
            view.ShowSettings(false, "", HotkeyText.Default);
            Lay(view);
            texts = Texts(view);
            TestRunner.Check(texts.Contains("Rings") && texts.Contains("Focus timer") && texts.Contains("Clipboard history"), "Settings lists the rings");
            TestRunner.Check(!texts.Contains("Short break"), "no focus lengths while the Focus ring is off");
            string toggled = null;
            view.RingToggled += delegate(string name, bool on) { toggled = name + (on ? " on" : " off"); };
            ClickSwitch(view, "Claude, on");
            TestRunner.Check(toggled == null, "the last ring on can't be switched off");
            ClickSwitch(view, "Music, off");
            TestRunner.Eq("music on", toggled, "a switch turns its ring on");
        }

        static void Cells()
        {
            var a = new CellModel { Provider = "claude", Text = "73%", ShowArc = true, Used = 73, ArcColor = Palette.Yellow };
            var b = new CellModel { Provider = "claude", Text = "73%", ShowArc = true, Used = 73, ArcColor = Palette.Yellow };
            TestRunner.Check(a.SameAs(b), "the same reading draws the same");
            b.Used = 74;
            TestRunner.Check(!a.SameAs(b), "a new percent doesn't");
            b.Used = 73;
            b.Activity = States.Working;
            TestRunner.Check(!a.SameAs(b), "nor does a session starting work");
            TestRunner.Check(!a.SameAs(null), "nothing drawn yet");

            var view = new NotchView { Animate = false };
            view.Update(new List<CellModel> { a }, false);
            Lay(view);
            var arc = All(view).OfType<System.Windows.Shapes.Path>().FirstOrDefault(p => p.StrokeThickness == NotchView.RingStroke && p.Visibility == Visibility.Visible);
            Geometry before = arc == null ? null : arc.Data;
            view.Update(new List<CellModel> { new CellModel { Provider = "claude", Text = "73%", ShowArc = true, Used = 73, ArcColor = Palette.Yellow } }, false);
            TestRunner.Check(before != null && ReferenceEquals(before, arc.Data), "an unchanged ring isn't drawn again");
        }

        static void Click(DependencyObject root, string text)
        {
            TextBlock t = All(root).OfType<TextBlock>().FirstOrDefault(x => x.Text == text && Shown(x));
            if (t == null) { TestRunner.Check(false, "found " + text); return; }
            DependencyObject at = t;
            while (at != null && !(at is Border && ((Border)at).Cursor == System.Windows.Input.Cursors.Hand)) at = VisualTreeHelper.GetParent(at);
            var b = at as Border;
            if (b == null) { TestRunner.Check(false, text + " is in a button"); return; }
            b.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        }

        static void ClickSwitch(DependencyObject root, string name)
        {
            Border s = All(root).OfType<Border>().FirstOrDefault(x => System.Windows.Automation.AutomationProperties.GetName(x) == name);
            if (s == null) { TestRunner.Check(false, "found the switch " + name); return; }
            s.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        }

        static Border FindTile(DependencyObject root, string title)
        {
            foreach (Border b in All(root).OfType<Border>())
                if (VisualTreeHelper.GetParent(b) is Grid && All(b).OfType<TextBlock>().Any(t => t.Text == title)) return b;
            return null;
        }

        static void Lay(FrameworkElement e)
        {
            e.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            e.Arrange(new Rect(e.DesiredSize));
            e.UpdateLayout();
        }

        // Every visible TextBlock's text: collapsed parts of the tree are skipped.
        static List<string> Texts(DependencyObject root)
        {
            return All(root).OfType<TextBlock>().Where(t => t.IsVisible || Shown(t)).Select(t => t.Text).ToList();
        }

        static bool Shown(DependencyObject d)
        {
            for (DependencyObject at = d; at != null; at = VisualTreeHelper.GetParent(at))
            {
                var e = at as UIElement;
                if (e != null && e.Visibility != Visibility.Visible) return false;
            }
            return true;
        }

        static IEnumerable<DependencyObject> All(DependencyObject root)
        {
            yield return root;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                foreach (DependencyObject d in All(VisualTreeHelper.GetChild(root, i))) yield return d;
        }
    }
}
