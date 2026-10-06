using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WEllipse = System.Windows.Shapes.Ellipse;
using WPath = System.Windows.Shapes.Path;

namespace Capsule
{
    // The full panel's content (spec §2): a header and a glass board of tiles (Claude and Codex small, Sessions, Calendar
    // and Ideas wide), or the settings sheet in its place, laid out in DIPs with room around it for its shadow.
    // PanelWindow shows it; Preview renders it. Update rebuilds the tiles' contents but keeps the Ideas box, so
    // typing is never interrupted.
    public sealed class PanelView : Grid
    {
        public const double PanelWidth = 360, Pad = 12, Gap = 8, Radius = 22, TileRadius = 16, TilePad = 12;
        public const double ShadowMargin = 20;
        public const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";
        const string RefreshGlyph = "\uE72C", SettingsGlyph = "\uE713", BackGlyph = "\uE72B";   // U+E72C Refresh, U+E713 Settings, U+E72B Back: the icon fonts' glyphs, kept as escapes because the characters themselves are invisible and a rewrite can drop them
        const string SecretSavedHint = "Saved. Paste a new one to replace it.";
        const string ClientSecretSavedHint = "Secret saved. Paste a new one to replace it.";
        const string ClientHint = "From your own Google Cloud OAuth client (Desktop app). Or paste its downloaded JSON into Client ID: it fills both.";
        const string ClientSecretPendingHint = "Secret kept for the next sign-in. Paste a new one to replace it.";
        const string LinkHint = "In Google Calendar on the web: ⚙ Settings → your calendar → Integrate calendar → Secret address in iCal format. Capsule only reads it.";
        // A small tile's inside: half the board less the gap, its padding and its 1-px border.
        public const double SmallInner = (PanelWidth - 2 * Pad - Gap) / 2 - 2 * TilePad - 2;

        readonly GlassSurface glass = new GlassSurface();
        readonly StackPanel board = new StackPanel();
        readonly DockPanel header = new DockPanel { LastChildFill = false };
        readonly Grid tiles = new Grid();
        readonly Border claudeTile = new Border(), codexTile = new Border(), sessionsTile = new Border(), calendarTile = new Border(), ideasTile = new Border();
        readonly StackPanel ideasContent = new StackPanel();
        readonly DockPanel ideasHeader = new DockPanel { LastChildFill = false };
        readonly StackPanel ideasRows = new StackPanel();
        readonly StackPanel ideasFooter = new StackPanel();
        readonly Border ideaField = new Border();
        readonly TextBox ideaBox = new TextBox();
        readonly TextBlock ideaHint = new TextBlock();

        // The settings sheet (spec §3): the Notion secret (write-only), the database link, and the shortcut.
        readonly StackPanel settings = new StackPanel();
        readonly MonthView month = new MonthView();   // the month page (month spec §2), in the tiles' place like the settings
        readonly PasswordBox secretBox = new PasswordBox();
        readonly TextBox linkBox = new TextBox();
        readonly TextBlock secretHint = new TextBlock();
        readonly TextBlock notionResult = new TextBlock();
        readonly Border shortcutBox = new Border();
        readonly TextBlock shortcutText = new TextBlock();
        readonly TextBlock shortcutResult = new TextBlock();
        string shortcut = HotkeyText.Default;
        bool capturing;

        public event Action RefreshAllClicked;
        public event Action<string> RefreshClicked;        // "claude" or "codex"
        public event Action SignInClicked;
        public event Action<string> IdeaSubmitted;         // the text in the Ideas box, on Enter
        public event Action<string> IdeaOpened;            // an idea's Notion link
        public event Action DiscardClicked;
        public event Action SettingsClicked;               // the ⚙ button: the Controller calls ShowSettings
        public event Action<string, string> NotionSaved;   // a new secret ("" to keep the saved one) and the link; SecretSaved follows once it is kept
        public event Action<string> ShortcutChosen;        // a new shortcut, as text
        public event Action<string, string> GoogleSignInClicked;   // the client ID and a new client secret ("" keeps the saved one)
        public event Action GoogleSignInCancelled;         // the sign-in waiting for the browser is given up
        public event Action GoogleSignOutClicked;
        public event Action<string, bool> CalendarToggled; // a calendar's id, and whether it is now shown
        public event Action<string> GoogleLinkSaved;       // what was pasted as the calendar's iCal address; LinkKept follows once it is kept
        public event Action<string, string> GoogleClientDraft;   // what is in the client's fields as the settings close, or a client file pasted whole
        public event Action GoogleLinkRemoved;
        public event Action Resized;                       // the content changed size outside Update

        public PanelView()
        {
            UseLayoutRounding = true;
            Margin = new Thickness(ShadowMargin);
            board.Width = PanelWidth;
            board.HorizontalAlignment = HorizontalAlignment.Left;

            header.Margin = new Thickness(Pad + 4, Pad, Pad, 8);
            board.Children.Add(header);

            tiles.Margin = new Thickness(Pad, 0, Pad, Pad);
            tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Gap) });
            tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 7; i++) tiles.RowDefinitions.Add(new RowDefinition { Height = i % 2 == 1 ? new GridLength(Gap) : GridLength.Auto });
            Place(claudeTile, 0, 0, 1);
            Place(codexTile, 0, 2, 1);
            Place(sessionsTile, 2, 0, 3);
            Place(calendarTile, 4, 0, 3);   // below Sessions, above Ideas (calendar spec §2); only while there is a model for it
            Place(ideasTile, 6, 0, 3);
            calendarTile.Visibility = Visibility.Collapsed;
            tiles.RowDefinitions[5].Height = new GridLength(0);
            board.Children.Add(tiles);

            // The Ideas tile's header and box stay put (moving the box would take its keyboard focus); only the rows
            // and the footer below them are rebuilt on every update.
            ideaHint.Text = "Jot an idea…";
            ideaHint.IsHitTestVisible = false;
            ideaHint.VerticalAlignment = VerticalAlignment.Center;
            Plain(ideaBox);
            ideaBox.MaxLength = IdeasModule.MaxChars;
            DataObject.AddPastingHandler(ideaBox, OnIdeaPasting);
            ideaBox.TextChanged += delegate { ideaHint.Visibility = ideaBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; };
            ideaBox.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                if (ideaBox.Text.Trim() != "" && IdeaSubmitted != null) IdeaSubmitted(ideaBox.Text);
            };
            var field = new Grid();
            field.Children.Add(ideaHint);
            field.Children.Add(ideaBox);
            Field(ideaField, field);
            ideaField.Margin = new Thickness(0, 0, 0, 6);
            ideasContent.Children.Add(ideasHeader);
            ideasContent.Children.Add(ideaField);
            ideasContent.Children.Add(ideasRows);
            ideasContent.Children.Add(ideasFooter);
            ideasTile.Child = ideasContent;

            BuildSettings();

            Children.Add(glass);
            Children.Add(board);
            Children.Add(settings);
            month.Visibility = Visibility.Collapsed;
            month.BackClicked += ShowBoard;
            month.Resized += RaiseResized;
            Children.Add(month);
            Restyle();
        }

        void Place(Border tile, int row, int column, int span)
        {
            Grid.SetRow(tile, row);
            Grid.SetColumn(tile, column);
            Grid.SetColumnSpan(tile, span);
            tiles.Children.Add(tile);
        }

        public TextBox IdeaBox { get { return ideaBox; } }
        public bool ShowingSettings { get { return settings.Visibility == Visibility.Visible; } }
        public bool ShowingMonth { get { return month.Visibility == Visibility.Visible; } }
        public MonthView Month { get { return month; } }
        public event Action MonthClicked;      // the Calendar tile's month button: the Controller calls ShowMonth
        public event Action MonthClosed;       // the month page gave way to the tiles (←, or the panel closing)

        // The month page in the tiles' place, or redrawn in place when it already shows.
        public void ShowMonth(MonthModel m)
        {
            month.Update(m);
            board.Visibility = Visibility.Collapsed;
            settings.Visibility = Visibility.Collapsed;
            month.Visibility = Visibility.Visible;
            // Redrawn in place, the page can grow or shrink too (the day's list, its Add buttons once read): the glass and
            // the window follow it every time.
            Relayout();
            RaiseResized();
        }

        // The panel's glass, from the panel's top-left (inside the shadow margin).
        public Geometry Outline { get { return glass.Outline; } }

        // The theme changed: recolour what Update doesn't rebuild.
        public void Restyle()
        {
            Theme theme = Theme.Current;
            month.Restyle();
            foreach (Border tile in new[] { claudeTile, codexTile, sessionsTile, calendarTile, ideasTile })
            {
                tile.CornerRadius = new CornerRadius(TileRadius);
                tile.Padding = new Thickness(TilePad);
                tile.BorderThickness = new Thickness(1);
                tile.Background = theme.Brush(theme.Tile);
                tile.BorderBrush = theme.Brush(theme.TileRim);
            }
            foreach (Border box in new[] { ideaField, (Border)secretBox.Parent, (Border)linkBox.Parent, shortcutBox, (Border)clientIdBox.Parent, (Border)clientSecretBox.Parent, (Border)calendarLinkBox.Parent }) box.Background = NotchView.Brush(Palette.Track);
            foreach (Control box in new Control[] { ideaBox, secretBox, linkBox, clientIdBox, clientSecretBox, calendarLinkBox }) box.Foreground = NotchView.Brush(Palette.Text);
            calendarLinkBox.CaretBrush = NotchView.Brush(Palette.Text);
            ideaBox.CaretBrush = NotchView.Brush(Palette.Text);
            linkBox.CaretBrush = NotchView.Brush(Palette.Text);
            secretBox.CaretBrush = NotchView.Brush(Palette.Text);
            clientIdBox.CaretBrush = NotchView.Brush(Palette.Text);
            clientSecretBox.CaretBrush = NotchView.Brush(Palette.Text);
            ideaHint.Foreground = NotchView.Brush(Palette.Secondary);
            ideaHint.FontSize = 13;
            ideaHint.FontFamily = ideaBox.FontFamily;
            shortcutText.Foreground = NotchView.Brush(Palette.Text);
            foreach (TextBlock t in new[] { secretHint, shortcutResult, notionResult, clientSecretHint, calendarLinkHint }) t.Foreground = NotchView.Brush(Palette.Secondary);
            googleAccount.Foreground = NotchView.Brush(Palette.Text);

            header.Children.Clear();
            Border gear = IconButton(SettingsGlyph, "Settings", delegate { if (SettingsClicked != null) SettingsClicked(); });
            DockPanel.SetDock(gear, Dock.Right);
            header.Children.Add(gear);
            Border refreshAll = IconButton(RefreshGlyph, "Refresh", delegate { if (RefreshAllClicked != null) RefreshAllClicked(); });
            DockPanel.SetDock(refreshAll, Dock.Right);
            header.Children.Add(refreshAll);
            TextBlock title = CardView.MakeText("Capsule", 15, Palette.Text, FontWeights.SemiBold);
            title.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(title);

            ideasHeader.Children.Clear();
            TextBlock where = CardView.MakeText("Notion", 11.5, Palette.Secondary, FontWeights.Normal);
            DockPanel.SetDock(where, Dock.Right);
            ideasHeader.Children.Add(where);
            ideasHeader.Children.Add(TileHeader("Ideas"));

            settingsHeader.Children.Clear();
            Border back = IconButton(BackGlyph, "Back", ShowBoard);
            back.Margin = new Thickness(-6, 0, 4, 0);
            settingsHeader.Children.Add(back);
            TextBlock settingsTitle = CardView.MakeText("Settings", 15, Palette.Text, FontWeights.SemiBold);
            settingsTitle.VerticalAlignment = VerticalAlignment.Center;
            settingsHeader.Children.Add(settingsTitle);
            foreach (TextBlock label in settingsLabels) label.Foreground = NotchView.Brush(Palette.Secondary);
            saveButtonHost.Child = PillButton("Save and test", SaveNotion);
            RenderGoogle();
        }

        public void Update(PanelModel m)
        {
            claudeTile.Child = UsageContent(m.Claude);
            codexTile.Child = UsageContent(m.Codex);
            sessionsTile.Child = SessionsContent(m.Sessions);
            bool calendar = m.Calendar != null;
            calendarTile.Visibility = calendar ? Visibility.Visible : Visibility.Collapsed;
            tiles.RowDefinitions[5].Height = new GridLength(calendar ? Gap : 0);   // no gap left behind a hidden tile
            calendarTile.Child = calendar ? CalendarContent(m.Calendar) : null;
            BuildIdeas(m.Ideas);
            Relayout();
        }

        // Measures the shown page and fits the glass to it.
        public void Relayout()
        {
            UpdateLayout();   // a change deep in the page (a rebuilt list) reaches this panel's size only once laid out
            Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double height = Math.Max(DesiredSize.Height - 2 * ShadowMargin, 2 * Radius);
            glass.Apply(new RectangleGeometry(new Rect(0, 0, PanelWidth, height), Radius, Radius), null, ShadowMargin);
        }

        public void FocusIdeas()
        {
            ideaBox.Focus();
            Keyboard.Focus(ideaBox);
            ideaBox.CaretIndex = ideaBox.Text.Length;
        }

        public void ClearIdea() { ideaBox.Clear(); }

        // A single-line TextBox cuts a paste off at its first line break, which would silently drop the rest of a pasted
        // paragraph: the lines are joined into one first, the way an idea's lines are joined when it is queued.
        static void OnIdeaPasting(object sender, DataObjectPastingEventArgs e)
        {
            string text = e.DataObject.GetDataPresent(DataFormats.UnicodeText) ? e.DataObject.GetData(DataFormats.UnicodeText) as string : null;
            if (text == null || text.IndexOfAny(new[] { '\r', '\n' }) < 0) return;   // not text, or already one line: left as it is
            e.DataObject = new DataObject(DataFormats.UnicodeText, IdeasModule.Clean(text));
            e.FormatToApply = DataFormats.UnicodeText;
        }

        // ---- Settings ----

        readonly StackPanel settingsHeader = new StackPanel { Orientation = Orientation.Horizontal };
        readonly System.Collections.Generic.List<TextBlock> settingsLabels = new System.Collections.Generic.List<TextBlock>();
        readonly Border saveButtonHost = new Border();

        void BuildSettings()
        {
            settings.Width = PanelWidth;
            settings.HorizontalAlignment = HorizontalAlignment.Left;
            settings.Visibility = Visibility.Collapsed;
            settings.Margin = new Thickness(0, 0, 0, Pad);
            settingsHeader.Margin = new Thickness(Pad + 4, Pad, Pad, 10);
            settings.Children.Add(settingsHeader);

            settings.Children.Add(BuildGoogle());   // above Ideas → Notion, as the Calendar tile is above Ideas

            var notion = new StackPanel { Margin = new Thickness(Pad + 4, 0, Pad + 4, 0) };
            notion.Children.Add(Section("Ideas → Notion"));
            notion.Children.Add(Label("Notion secret"));
            Plain(secretBox);
            var secretField = new Border();
            Field(secretField, secretBox);
            notion.Children.Add(secretField);
            secretHint.FontSize = 11;
            secretHint.TextWrapping = TextWrapping.Wrap;
            secretHint.Margin = new Thickness(0, 3, 0, 8);
            notion.Children.Add(secretHint);
            notion.Children.Add(Label("Database link"));
            Plain(linkBox);
            var linkField = new Border();
            Field(linkField, linkBox);
            notion.Children.Add(linkField);
            TextBlock howTo = Hint("In Notion, open the database, then ••• → Connections and add your connection.");
            howTo.Margin = new Thickness(0, 3, 0, 8);
            notion.Children.Add(howTo);
            var save = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(saveButtonHost, Dock.Left);
            saveButtonHost.VerticalAlignment = VerticalAlignment.Top;   // keeps its size, and its place, when the result beside it wraps
            save.Children.Add(saveButtonHost);
            notionResult.FontSize = 11.5;
            notionResult.TextWrapping = TextWrapping.Wrap;
            notionResult.VerticalAlignment = VerticalAlignment.Center;
            notionResult.Margin = new Thickness(10, 0, 0, 0);
            save.Children.Add(notionResult);
            notion.Children.Add(save);

            notion.Children.Add(Section("Shortcut"));
            shortcutText.FontSize = 13;
            shortcutText.FontWeight = FontWeights.SemiBold;
            shortcutBox.Child = shortcutText;
            shortcutBox.CornerRadius = new CornerRadius(10);
            shortcutBox.Padding = new Thickness(10, 6, 10, 6);
            shortcutBox.HorizontalAlignment = HorizontalAlignment.Left;
            shortcutBox.MinWidth = 150;
            shortcutBox.Focusable = true;
            InputMethod.SetIsInputMethodEnabled(shortcutBox, false);   // an input method would turn key presses into Key.ImeProcessed, which is no shortcut
            shortcutBox.FocusVisualStyle = null;
            shortcutBox.Cursor = Cursors.Hand;
            shortcutBox.MouseLeftButtonUp += delegate
            {
                capturing = true;
                shortcutText.Text = "Press a shortcut…";
                Keyboard.Focus(shortcutBox);
            };
            shortcutBox.PreviewKeyDown += OnShortcutKey;
            shortcutBox.LostKeyboardFocus += delegate
            {
                if (!capturing) return;
                capturing = false;
                shortcutText.Text = shortcut;
            };
            notion.Children.Add(shortcutBox);
            shortcutResult.FontSize = 11;
            shortcutResult.TextWrapping = TextWrapping.Wrap;
            shortcutResult.Margin = new Thickness(0, 3, 0, 0);
            notion.Children.Add(shortcutResult);
            settings.Children.Add(notion);
        }

        void OnShortcutKey(object sender, KeyEventArgs e)
        {
            if (!capturing || e.Key == Key.Escape) return;   // Esc still closes the panel
            e.Handled = true;
            uint modifiers, key;
            if (!HotkeyText.FromKeys(e.Key, e.SystemKey, Keyboard.Modifiers, out modifiers, out key)) return;   // only modifiers so far
            capturing = false;
            string refusal = HotkeyText.Refusal(modifiers, key);
            if (refusal != null)
            {
                ShowShortcutResult(false, shortcut, refusal);
                return;
            }
            if (ShortcutChosen != null) ShortcutChosen(HotkeyText.Format(modifiers, key));
        }

        // The pasted secret stays in the box until the Controller has kept it (SecretSaved): a save that fails, or a link
        // that is refused, must not lose it.
        void SaveNotion()
        {
            if (NotionSaved != null) NotionSaved(secretBox.Password, linkBox.Text);
        }

        // Opens the settings with what is saved now. The secret itself is never shown: only whether one is saved. The Google
        // Calendar section follows with ShowGoogle.
        public void ShowSettings(bool secretSaved, string databaseId, string currentShortcut)
        {
            secretBox.Clear();
            clientSecretBox.Clear();
            calendarLinkBox.Clear();
            clientOpen = false;
            clientIdFresh = true;
            secretHint.Text = secretSaved
                ? SecretSavedHint
                : "Your connection's secret, from app.notion.com/developers (Configuration tab).";
            linkBox.Text = databaseId != "" ? "https://app.notion.com/p/" + databaseId : "";
            notionResult.Text = "";
            shortcut = currentShortcut;
            shortcutText.Text = currentShortcut;
            shortcutResult.Text = "Click, then press a new shortcut.";
            shortcutResult.Foreground = NotchView.Brush(Palette.Secondary);
            if (ShowingMonth)
            {
                month.Visibility = Visibility.Collapsed;
                if (MonthClosed != null) MonthClosed();
            }
            board.Visibility = Visibility.Collapsed;
            settings.Visibility = Visibility.Visible;
            RaiseResized();
        }

        public void ShowBoard()
        {
            // Clicking away to copy the client secret closes the panel: what was typed into the client's fields is handed
            // over first, so it isn't lost.
            if (ShowingSettings && googleClient.Visibility == Visibility.Visible && (clientIdBox.Text.Trim() != "" || clientSecretBox.Password.Trim() != "") && GoogleClientDraft != null)
                GoogleClientDraft(clientIdBox.Text, clientSecretBox.Password);
            capturing = false;
            secretBox.Clear();
            clientSecretBox.Clear();
            calendarLinkBox.Clear();
            if (ShowingMonth)
            {
                month.Visibility = Visibility.Collapsed;
                board.Visibility = Visibility.Visible;
                if (MonthClosed != null) MonthClosed();
                RaiseResized();
                return;
            }
            if (!ShowingSettings) return;
            settings.Visibility = Visibility.Collapsed;
            board.Visibility = Visibility.Visible;
            RaiseResized();
        }

        // The Controller kept the pasted secret: the write-only box empties, and the hint says one is saved.
        public void SecretSaved()
        {
            secretBox.Clear();
            secretHint.Text = SecretSavedHint;
            RaiseResized();
        }

        // ok: null while a test runs.
        public void ShowNotionResult(bool? ok, string text)
        {
            notionResult.Text = (ok == true ? "✓ " : "") + text;
            notionResult.Foreground = NotchView.Brush(ok == true ? Palette.Green : ok == false ? Palette.Amber : Palette.Secondary);
            RaiseResized();
        }

        public void ShowShortcutResult(bool ok, string current, string text)
        {
            shortcut = current;
            shortcutText.Text = current;
            shortcutResult.Text = text;
            shortcutResult.Foreground = NotchView.Brush(ok ? Palette.Green : Palette.Amber);
            RaiseResized();
        }

        void RaiseResized() { if (Resized != null) Resized(); }

        // ---- Google Calendar settings (calendar spec §2) ----

        readonly StackPanel calendarLinkBlock = new StackPanel();   // the calendar's iCal address: the quick way, read-only
        readonly PasswordBox calendarLinkBox = new PasswordBox();   // write-only: the address is a secret
        readonly TextBlock calendarLinkHint = new TextBlock();
        readonly StackPanel calendarLinkButtons = new StackPanel { Orientation = Orientation.Horizontal };
        readonly Border ownClientHost = new Border();               // "Use your own Google client instead", while that part is hidden
        readonly StackPanel googleClient = new StackPanel();    // the client's fields and Sign in: until signed in, or after Change client
        readonly TextBox clientIdBox = new TextBox();
        readonly PasswordBox clientSecretBox = new PasswordBox();
        readonly TextBlock clientSecretHint = new TextBlock();
        readonly Border googleButtonHost = new Border();        // Sign in with Google, or Cancel while the browser is open
        readonly StackPanel googleSignedIn = new StackPanel();  // the account, Sign out and Change client
        readonly TextBlock googleAccount = new TextBlock();
        readonly Border googleSignOutHost = new Border();
        readonly Border signInAgainHost = new Border();         // "Sign in again", when the sign-in predates adding
        readonly Border changeClientHost = new Border();
        readonly StackPanel calendarChoices = new StackPanel();
        readonly TextBlock googleResult = new TextBlock();
        GoogleSettings google = new GoogleSettings();
        bool clientOpen;      // Change client was clicked: the client's fields show while signed in
        bool clientIdFresh;   // the settings just opened: the next ShowGoogle fills in the saved client ID

        public TextBox ClientIdBox { get { return clientIdBox; } }
        public PasswordBox ClientSecretBox { get { return clientSecretBox; } }
        public PasswordBox CalendarLinkBox { get { return calendarLinkBox; } }

        StackPanel BuildGoogle()
        {
            var g = new StackPanel { Margin = new Thickness(Pad + 4, 0, Pad + 4, 6) };
            g.Children.Add(Section("Google Calendar"));
            calendarLinkBlock.Children.Add(Label("Calendar link"));
            Plain(calendarLinkBox);
            var calendarLinkField = new Border();
            Field(calendarLinkField, calendarLinkBox);
            calendarLinkBlock.Children.Add(calendarLinkField);
            calendarLinkHint.FontSize = 11;
            calendarLinkHint.TextWrapping = TextWrapping.Wrap;
            calendarLinkHint.Margin = new Thickness(0, 3, 0, 8);
            calendarLinkBlock.Children.Add(calendarLinkHint);
            calendarLinkButtons.Margin = new Thickness(0, 0, 0, 6);
            calendarLinkBlock.Children.Add(calendarLinkButtons);
            g.Children.Add(calendarLinkBlock);
            ownClientHost.HorizontalAlignment = HorizontalAlignment.Left;
            ownClientHost.Margin = new Thickness(-4, 0, 0, 6);
            g.Children.Add(ownClientHost);
            googleClient.Children.Add(Label("Client ID"));
            Plain(clientIdBox);
            DataObject.AddPastingHandler(clientIdBox, OnClientIdPasting);
            var idField = new Border();
            Field(idField, clientIdBox);
            idField.Margin = new Thickness(0, 0, 0, 6);
            googleClient.Children.Add(idField);
            googleClient.Children.Add(Label("Client secret"));
            Plain(clientSecretBox);
            var secretField = new Border();
            Field(secretField, clientSecretBox);
            googleClient.Children.Add(secretField);
            clientSecretHint.FontSize = 11;
            clientSecretHint.TextWrapping = TextWrapping.Wrap;
            clientSecretHint.Margin = new Thickness(0, 3, 0, 8);
            googleClient.Children.Add(clientSecretHint);
            googleButtonHost.HorizontalAlignment = HorizontalAlignment.Left;
            googleButtonHost.Margin = new Thickness(0, 0, 0, 6);
            googleClient.Children.Add(googleButtonHost);
            g.Children.Add(googleClient);

            googleAccount.FontSize = 12.5;
            googleAccount.TextWrapping = TextWrapping.Wrap;
            googleAccount.Margin = new Thickness(0, 0, 0, 6);
            googleSignedIn.Children.Add(googleAccount);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            signInAgainHost.Margin = new Thickness(0, 0, 8, 0);
            buttons.Children.Add(signInAgainHost);
            buttons.Children.Add(googleSignOutHost);
            changeClientHost.Margin = new Thickness(8, 0, 0, 0);
            changeClientHost.VerticalAlignment = VerticalAlignment.Center;
            buttons.Children.Add(changeClientHost);
            googleSignedIn.Children.Add(buttons);
            g.Children.Add(googleSignedIn);

            g.Children.Add(calendarChoices);
            googleResult.FontSize = 11.5;
            googleResult.TextWrapping = TextWrapping.Wrap;
            googleResult.Margin = new Thickness(0, 2, 0, 4);
            g.Children.Add(googleResult);
            return g;
        }

        // The Google Calendar section as the module has it now: called when the settings open, and whenever the module
        // changes while they show. The client ID box is filled in only when the settings have just opened, so what is
        // being typed in it is never overwritten. The client secret is never shown: only whether one is saved.
        public void ShowGoogle(GoogleSettings s)
        {
            if (s.SignedIn && !google.SignedIn) clientOpen = false;   // a sign-in just worked: the client's fields fold away
            google = s;
            if (clientIdFresh)
            {
                clientIdBox.Text = s.ClientId;
                clientIdFresh = false;
            }
            RenderGoogle();
            RaiseResized();
        }

        // The client's downloaded JSON pasted into Client ID: the ID goes in the box and both are handed over at once. Any
        // other paste is left as it is.
        void OnClientIdPasting(object sender, DataObjectPastingEventArgs e)
        {
            string text = e.DataObject.GetDataPresent(DataFormats.UnicodeText) ? e.DataObject.GetData(DataFormats.UnicodeText) as string : null;
            string id, secret;
            if (!GoogleClientFile.Parse(text, out id, out secret)) return;
            e.CancelCommand();
            clientIdBox.Text = id;
            clientSecretBox.Clear();
            if (GoogleClientDraft != null) GoogleClientDraft(id, secret);
        }

        // The module kept the pasted calendar link: the write-only box empties.
        public void LinkKept()
        {
            calendarLinkBox.Clear();
            RenderGoogle();
            RaiseResized();
        }

        // The module kept the pasted client secret (the sign-in with it worked): the write-only box empties.
        public void ClientSecretKept()
        {
            clientSecretBox.Clear();
            RenderGoogle();
            RaiseResized();
        }

        // Signed out: the client's fields and Sign in with Google. Signing in: the same, with Cancel. Signed in: the account
        // with Sign out and Change client, then a check box per calendar.
        void RenderGoogle()
        {
            // The calendar link first, unless signed in (the sign-in then wins). Your own client shows once it is set up, or
            // asked for.
            calendarLinkBlock.Visibility = google.SignedIn ? Visibility.Collapsed : Visibility.Visible;
            calendarLinkHint.Text = !google.LinkSaved ? LinkHint
                : "Link saved" + (google.LinkName != "" ? ": showing " + google.LinkName : "") + ". Paste a new one to replace it.";
            calendarLinkButtons.Children.Clear();
            calendarLinkButtons.Children.Add(PillButton("Save link", delegate { if (GoogleLinkSaved != null) GoogleLinkSaved(calendarLinkBox.Password); }));
            if (google.LinkSaved)
            {
                Border remove = PillButton("Remove link", delegate { if (GoogleLinkRemoved != null) GoogleLinkRemoved(); });
                remove.Margin = new Thickness(8, 0, 0, 0);
                calendarLinkButtons.Children.Add(remove);
            }
            bool ownClient = google.SignedIn || google.SigningIn || clientOpen || google.ClientId != "" || google.SecretSaved;
            ownClientHost.Child = ownClient ? null : LinkButton("Use your own Google client instead", delegate
            {
                clientOpen = true;
                RenderGoogle();
                RaiseResized();
            });
            ownClientHost.Visibility = ownClient ? Visibility.Collapsed : Visibility.Visible;
            googleClient.Visibility = ownClient && (!google.SignedIn || clientOpen || google.SigningIn) ? Visibility.Visible : Visibility.Collapsed;
            googleSignedIn.Visibility = google.SignedIn ? Visibility.Visible : Visibility.Collapsed;
            clientSecretHint.Text = google.SecretPending ? ClientSecretPendingHint : google.SecretSaved ? ClientSecretSavedHint : ClientHint;
            googleButtonHost.Child = google.SigningIn
                ? PillButton("Cancel", delegate { if (GoogleSignInCancelled != null) GoogleSignInCancelled(); })
                : PillButton("Sign in with Google", delegate { if (GoogleSignInClicked != null) GoogleSignInClicked(clientIdBox.Text, clientSecretBox.Password); });
            googleAccount.Text = (google.Account != "" ? "Signed in as " + google.Account : "Signed in")
                + (google.NeedsSignInAgain ? ". Sign in again to let Capsule add events and tasks." : "");
            googleSignOutHost.Child = PillButton("Sign out", delegate { if (GoogleSignOutClicked != null) GoogleSignOutClicked(); });
            // A sign-in from before adding existed reads, but may not add: a new one asks Google for that too.
            signInAgainHost.Child = google.NeedsSignInAgain && !google.SigningIn
                ? PillButton("Sign in again", delegate { if (GoogleSignInClicked != null) GoogleSignInClicked("", ""); })
                : null;
            signInAgainHost.Visibility = signInAgainHost.Child != null ? Visibility.Visible : Visibility.Collapsed;
            changeClientHost.Child = LinkButton("Change client", delegate
            {
                clientOpen = true;
                RenderGoogle();
                RaiseResized();
            });
            changeClientHost.Visibility = clientOpen ? Visibility.Collapsed : Visibility.Visible;
            calendarChoices.Children.Clear();
            if (google.SignedIn && google.Calendars.Count > 0)
            {
                TextBlock label = CardView.MakeText("Calendars", 12, Palette.Secondary, FontWeights.Normal);
                label.Margin = new Thickness(0, 4, 0, 3);
                calendarChoices.Children.Add(label);
                foreach (CalendarChoice c in google.Calendars) calendarChoices.Children.Add(CalendarBox(c));
            }
            googleResult.Text = google.Status;
            googleResult.Foreground = NotchView.Brush(google.SigningIn ? Palette.Secondary : Palette.Amber);
            googleResult.Visibility = google.Status != "" ? Visibility.Visible : Visibility.Collapsed;
        }

        // A calendar's check box: its colour dot and its name, cut with "…" when it doesn't fit.
        UIElement CalendarBox(CalendarChoice c)
        {
            var label = new DockPanel();
            var dot = new WEllipse { Width = 8, Height = 8, Fill = NotchView.Brush(c.Color), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(dot, Dock.Left);
            label.Children.Add(dot);
            label.Children.Add(CardView.MakeText(c.Name, 12.5, Palette.Text, FontWeights.Normal));
            var box = new CheckBox
            {
                Content = label,
                IsChecked = c.Shown,
                Margin = new Thickness(0, 2, 0, 2),
                VerticalContentAlignment = VerticalAlignment.Center,
                FocusVisualStyle = null,
            };
            System.Windows.Automation.AutomationProperties.SetName(box, c.Name);
            string id = c.Id;
            box.Click += delegate { if (CalendarToggled != null) CalendarToggled(id, box.IsChecked == true); };
            return box;
        }

        // A quiet text button: underlined, lighting up under the mouse.
        static Border LinkButton(string text, Action click)
        {
            TextBlock t = CardView.MakeText(text, 12, Palette.Secondary, FontWeights.Normal);
            t.TextDecorations = TextDecorations.Underline;
            Border b = Button(t, click, Brushes.Transparent, NotchView.Brush(Palette.Track), new Thickness(4, 2, 4, 2));
            System.Windows.Automation.AutomationProperties.SetName(b, text);
            return b;
        }

        TextBlock Section(string text)
        {
            TextBlock t = CardView.MakeText(text, 12, Palette.Secondary, FontWeights.SemiBold);
            t.Margin = new Thickness(0, 6, 0, 6);
            settingsLabels.Add(t);
            return t;
        }

        TextBlock Label(string text)
        {
            TextBlock t = CardView.MakeText(text, 12, Palette.Secondary, FontWeights.Normal);
            t.Margin = new Thickness(0, 0, 0, 3);
            settingsLabels.Add(t);
            return t;
        }

        TextBlock Hint(string text)
        {
            TextBlock t = CardView.Wrap(CardView.MakeText(text, 11, Palette.Secondary, FontWeights.Normal));
            settingsLabels.Add(t);
            return t;
        }

        // A text box without WPF's default frame, for drawing inside a glass field.
        public static void Plain(Control box)
        {
            box.Background = Brushes.Transparent;
            box.BorderThickness = new Thickness(0);
            box.Padding = new Thickness(0);
            box.FontSize = 13;
            box.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            box.FocusVisualStyle = null;
        }

        static void Field(Border field, UIElement content)
        {
            field.CornerRadius = new CornerRadius(10);
            field.Padding = new Thickness(10, 6, 10, 6);
            field.Child = content;
        }

        // ---- Tiles ----

        UIElement UsageContent(UsageTile t)
        {
            var box = new StackPanel();
            var top = new DockPanel { LastChildFill = false };
            string provider = t.Provider;
            Border refresh = IconButton(RefreshGlyph, "Refresh", delegate { if (RefreshClicked != null) RefreshClicked(provider); });
            refresh.Margin = new Thickness(0, -4, -6, 0);
            DockPanel.SetDock(refresh, Dock.Right);
            top.Children.Add(refresh);
            top.Children.Add(new WPath
            {
                Data = Logos.For(t.Provider),
                Fill = NotchView.Brush(Palette.Text),
                Stretch = Stretch.Uniform,
                Width = 14,
                Height = 14,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            TextBlock name = CardView.MakeText(t.Title, 13, Palette.Text, FontWeights.SemiBold);
            name.VerticalAlignment = VerticalAlignment.Center;
            top.Children.Add(name);
            if (t.Plan != "") top.Children.Add(Badge(t.Plan));
            box.Children.Add(top);
            if (t.SignIn)
            {
                Border signIn = PillButton("Sign in", delegate { if (SignInClicked != null) SignInClicked(); });
                signIn.Margin = new Thickness(0, 10, 0, 4);
                signIn.HorizontalAlignment = HorizontalAlignment.Left;
                box.Children.Add(signIn);
            }
            else
            {
                TextBlock percent = CardView.MakeText(t.Percent, 26, Palette.Text, FontWeights.SemiBold);
                percent.Margin = new Thickness(0, 2, 0, 0);
                if (t.Dimmed) percent.Opacity = 0.45;
                box.Children.Add(percent);
                if (t.ShowBar) box.Children.Add(CardView.Bar(t.Used, t.Color, SmallInner));
                if (t.ResetText != "") box.Children.Add(CardView.MakeText(t.ResetText, 11.5, Palette.Secondary, FontWeights.Normal));
            }
            if (t.Note != "")
            {
                TextBlock note = CardView.Wrap(CardView.MakeText(t.Note, 11, Palette.Secondary, FontWeights.Normal));
                note.Margin = new Thickness(0, 4, 0, 0);
                box.Children.Add(note);
            }
            return box;
        }

        // The Calendar tile (calendar spec §2): today's events, then tomorrow's under its label, or a hint; the last list
        // dims when it is old and Google can't be reached, with why. Connected, its month button opens the month page.
        UIElement CalendarContent(CalendarTile t)
        {
            var box = new StackPanel();
            var top = new DockPanel { LastChildFill = false };
            if (t.CanOpenMonth)
            {
                Border open = IconButton(CalendarDay.Glyph, "Month", delegate { if (MonthClicked != null) MonthClicked(); });
                open.Margin = new Thickness(4, -4, -6, 0);
                DockPanel.SetDock(open, Dock.Right);
                top.Children.Add(open);
            }
            TextBlock where = CardView.MakeText("Google", 11.5, Palette.Secondary, FontWeights.Normal);
            DockPanel.SetDock(where, Dock.Right);
            top.Children.Add(where);
            top.Children.Add(TileHeader("Calendar"));
            box.Children.Add(top);
            if (t.Hint != "") box.Children.Add(CardView.Wrap(CardView.MakeText(t.Hint, 12, Palette.Secondary, FontWeights.Normal)));
            var rows = new StackPanel();
            if (t.Dimmed) rows.Opacity = 0.45;
            foreach (CalendarRow row in t.Today) rows.Children.Add(CardView.EventLine(row));
            if (t.Tomorrow.Count > 0)
            {
                TextBlock tomorrow = CardView.MakeText("Tomorrow", 11, Palette.Secondary, FontWeights.SemiBold);
                tomorrow.Margin = new Thickness(0, t.Today.Count > 0 ? 6 : 0, 0, 2);
                rows.Children.Add(tomorrow);
                foreach (CalendarRow row in t.Tomorrow) rows.Children.Add(CardView.EventLine(row));
            }
            box.Children.Add(rows);
            if (t.Note != "")
            {
                TextBlock note = CardView.Wrap(CardView.MakeText(t.Note, 11, Palette.Secondary, FontWeights.Normal));
                note.Margin = new Thickness(0, 4, 0, 0);
                box.Children.Add(note);
            }
            return box;
        }

        static UIElement SessionsContent(SessionsTile t)
        {
            var box = new StackPanel();
            box.Children.Add(TileHeader("Sessions"));
            if (t.Hint != "") box.Children.Add(CardView.Wrap(CardView.MakeText(t.Hint, 12, Palette.Secondary, FontWeights.Normal)));
            foreach (SessionRow s in t.Rows) box.Children.Add(CardView.SessionLine(s));
            if (t.More > 0) box.Children.Add(CardView.MakeText("+" + t.More + " more", 12, Palette.Secondary, FontWeights.Normal));
            return box;
        }

        void BuildIdeas(IdeasTile t)
        {
            ideasRows.Children.Clear();
            foreach (IdeaRow row in t.Rows) ideasRows.Children.Add(IdeaLine(row));
            ideasFooter.Children.Clear();
            if (t.Configured && t.Waiting > 0)
                ideasFooter.Children.Add(CardView.MakeText(t.Waiting + " waiting to sync", 11.5, Palette.Secondary, FontWeights.Normal));
            if (t.Note != "")
            {
                TextBlock note = CardView.Wrap(CardView.MakeText(t.Note, 11.5, t.Configured ? Palette.Amber : Palette.Secondary, FontWeights.Normal));
                note.Margin = new Thickness(0, 4, 0, 0);
                ideasFooter.Children.Add(note);
            }
            if (t.DiskNote != "")   // ideas not safe on disk yet: amber even while Notion isn't set up
            {
                TextBlock disk = CardView.Wrap(CardView.MakeText(t.DiskNote, 11.5, Palette.Amber, FontWeights.Normal));
                disk.Margin = new Thickness(0, 4, 0, 0);
                ideasFooter.Children.Add(disk);
            }
            if (t.CanDiscard)
            {
                Border discard = PillButton("Discard it", delegate { if (DiscardClicked != null) DiscardClicked(); });
                discard.Margin = new Thickness(0, 6, 0, 0);
                discard.HorizontalAlignment = HorizontalAlignment.Left;
                ideasFooter.Children.Add(discard);
            }
        }

        UIElement IdeaLine(IdeaRow row)
        {
            var line = new Grid { Margin = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.Children.Add(CardView.MakeText(row.Text, 12.5, Palette.Text, FontWeights.Normal));
            string mark = "", color = Palette.Secondary;
            if (row.State == IdeaRow.Saving) mark = "saving…";
            else if (row.State == IdeaRow.Waiting) { mark = "waiting"; color = Palette.Amber; }
            else if (row.State == IdeaRow.Saved) { mark = "✓"; color = Palette.Green; }
            else if (row.State == IdeaRow.Rejected) { mark = "refused"; color = Palette.Red; }
            if (mark != "")
            {
                TextBlock m = CardView.MakeText(mark, 11.5, color, FontWeights.SemiBold);
                m.Margin = new Thickness(8, 0, 0, 0);
                Grid.SetColumn(m, 1);
                line.Children.Add(m);
            }
            if (row.Url != "")
            {
                string url = row.Url;
                line.Cursor = Cursors.Hand;
                line.ToolTip = "Open in Notion";
                line.MouseLeftButtonUp += delegate { if (IdeaOpened != null) IdeaOpened(url); };
            }
            return line;
        }

        static TextBlock TileHeader(string text)
        {
            TextBlock t = CardView.MakeText(text, 12, Palette.Secondary, FontWeights.SemiBold);
            t.Margin = new Thickness(0, 0, 0, 6);
            return t;
        }

        static Border Badge(string text)
        {
            return new Border
            {
                Background = NotchView.Brush(Palette.Track),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(5, 0, 5, 1),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = CardView.MakeText(text, 10.5, Palette.Secondary, FontWeights.Normal),
            };
        }

        // A flat button that lights up under the mouse.
        public static Border Button(UIElement content, Action click, Brush rest, Brush hover, Thickness padding)
        {
            var b = new Border { Child = content, CornerRadius = new CornerRadius(8), Padding = padding, Background = rest, Cursor = Cursors.Hand };
            b.MouseEnter += delegate { b.Background = hover; };
            b.MouseLeave += delegate { b.Background = rest; };
            b.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e)
            {
                e.Handled = true;
                click();
            };
            return b;
        }

        static Border IconButton(string glyph, string tip, Action click)
        {
            var icon = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily(IconFont),
                FontSize = 12,
                Foreground = NotchView.Brush(Palette.Secondary),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Border b = Button(icon, click, Brushes.Transparent, NotchView.Brush(Palette.Track), new Thickness(6, 5, 6, 5));
            b.ToolTip = tip;
            return b;
        }

        public static Border PillButton(string text, Action click) { return PillButton(text, click, Button); }

        // The pill's look (its colours, text and padding) on any button maker with Button's shape: the card's request view
        // makes its own, which fires only on the release that ends its own press.
        public static Border PillButton(string text, Action click, Func<UIElement, Action, Brush, Brush, Thickness, Border> button)
        {
            Color ink = Theme.Current.Text;
            Brush hover = Theme.Current.Brush(Color.FromArgb(0x60, ink.R, ink.G, ink.B));
            Border pill = button(CardView.MakeText(text, 12.5, Palette.Text, FontWeights.SemiBold), click, NotchView.Brush(Palette.Track), hover, new Thickness(12, 4, 12, 5));
            System.Windows.Automation.AutomationProperties.SetName(pill, text);   // screen readers read the text, not "Border"
            return pill;
        }
    }
}
