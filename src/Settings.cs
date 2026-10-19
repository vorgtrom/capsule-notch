using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace Capsule
{
    // config.json: where the notch sits, whether it shows, where ideas go, and which Google client and calendars to use.
    public sealed class Config
    {
        public string Edge = "right";   // "left" or "right"
        public string Monitor = "";     // device name such as \\.\DISPLAY2; "" = the primary monitor
        public double Y = 0.5;          // centre of the notch along the edge: 0 = top, 1 = bottom
        public bool ShowNotch = true;
        public bool FirstRunDone;
        public string NotionDatabase = "";   // the ideas database's id (32 hex digits); "" = not set up
        public string Hotkey = HotkeyText.Default;   // the global shortcut that opens the panel on the Ideas box
        public bool CheckUpdates = true;     // a release build looks for a newer release once a day
        public bool LimitAlerts = true;      // notifications as a limit nears, or is on pace to run out before it resets
        public bool Sounds = true;           // a short sound when a session needs you or finishes
        public string GoogleClientId = "";   // the user's own OAuth client (Desktop app); "" = not set up. Its secret is in google-client-secret.bin
        // Calendars whose box the user changed: calendar id -> shown. The others follow Google's own "selected". Ids
        // only, never names.
        public Dictionary<string, bool> CalendarChoices = new Dictionary<string, bool>();
        // The rings switched off in Settings: name -> on. A ring that isn't listed is on.
        public Dictionary<string, bool> Rings = new Dictionary<string, bool>();
        public bool ClipboardHistory = true;   // the last copies, in memory only
        public int FocusMinutes = 25, ShortBreakMinutes = 5, LongBreakMinutes = 15;

        public static readonly string[] RingNames = { "claude", "codex", "calendar", "music", "focus" };
        public const int MaxFocusMinutes = 120, MaxBreakMinutes = 60;

        public bool RingOn(string name)
        {
            bool on;
            return !Rings.TryGetValue(name, out on) || on;
        }

        // A ring may go off only while another one stays on, so the capsule always has something it can show.
        public bool CanTurnOff(string name)
        {
            foreach (string other in RingNames) if (other != name && RingOn(other)) return true;
            return false;
        }

        public static Config Load(string path)
        {
            var c = new Config();
            var o = Json.Obj(Json.TryParse(Files.ReadText(path)));
            if (o == null) return c;
            string edge = Json.Str(Json.Get(o, "edge"));
            if (edge == "left" || edge == "right") c.Edge = edge;
            c.Monitor = Json.Str(Json.Get(o, "monitor")) ?? "";
            double? y = Json.Num(Json.Get(o, "y"));
            if (y.HasValue && !double.IsNaN(y.Value)) c.Y = Math.Max(0, Math.Min(1, y.Value));
            c.ShowNotch = Json.Get(o, "show_notch") as bool? ?? true;
            c.FirstRunDone = Json.Get(o, "first_run_done") as bool? ?? false;
            c.NotionDatabase = Json.Str(Json.Get(o, "notion_database")) ?? "";
            uint modifiers, key;
            if (HotkeyText.TryParse(Json.Str(Json.Get(o, "hotkey")), out modifiers, out key)) c.Hotkey = HotkeyText.Format(modifiers, key);
            c.CheckUpdates = Json.Get(o, "check_updates") as bool? ?? true;
            c.LimitAlerts = Json.Get(o, "limit_alerts") as bool? ?? true;
            c.Sounds = Json.Get(o, "sounds") as bool? ?? true;
            c.GoogleClientId = (Json.Str(Json.Get(o, "google_client_id")) ?? "").Trim();
            Dictionary<string, object> choices = Json.Obj(Json.Get(o, "google_calendars"));
            if (choices != null)
                foreach (KeyValuePair<string, object> choice in choices)
                    if (choice.Key != "" && choice.Value is bool) c.CalendarChoices[choice.Key] = (bool)choice.Value;
            Dictionary<string, object> rings = Json.Obj(Json.Get(o, "rings"));
            if (rings != null)
                foreach (string name in RingNames)
                    if (rings.ContainsKey(name) && rings[name] is bool) c.Rings[name] = (bool)rings[name];
            if (!c.RingOn("claude") && !c.CanTurnOff("claude")) c.Rings["claude"] = true;   // all off, by hand: Claude comes back
            c.ClipboardHistory = Json.Get(o, "clipboard_history") as bool? ?? true;
            c.FocusMinutes = Minutes(Json.Get(o, "focus_minutes"), 25, MaxFocusMinutes);
            c.ShortBreakMinutes = Minutes(Json.Get(o, "short_break_minutes"), 5, MaxBreakMinutes);
            c.LongBreakMinutes = Minutes(Json.Get(o, "long_break_minutes"), 15, MaxBreakMinutes);
            return c;
        }

        // False when config.json couldn't be written.
        public bool Save(string path)
        {
            var d = new Dictionary<string, object>();
            d["edge"] = Edge;
            d["monitor"] = Monitor;
            d["y"] = Y;
            d["show_notch"] = ShowNotch;
            d["first_run_done"] = FirstRunDone;
            d["notion_database"] = NotionDatabase;
            d["hotkey"] = Hotkey;
            d["check_updates"] = CheckUpdates;
            d["limit_alerts"] = LimitAlerts;
            d["sounds"] = Sounds;
            d["google_client_id"] = GoogleClientId;
            var choices = new Dictionary<string, object>();
            foreach (KeyValuePair<string, bool> choice in CalendarChoices) choices[choice.Key] = choice.Value;
            d["google_calendars"] = choices;
            var rings = new Dictionary<string, object>();
            foreach (KeyValuePair<string, bool> ring in Rings) rings[ring.Key] = ring.Value;
            d["rings"] = rings;
            d["clipboard_history"] = ClipboardHistory;
            d["focus_minutes"] = FocusMinutes;
            d["short_break_minutes"] = ShortBreakMinutes;
            d["long_break_minutes"] = LongBreakMinutes;
            return Files.WriteAtomic(path, Json.Write(d));
        }

        // A whole number of minutes from 1 to max, or the default.
        static int Minutes(object value, int fallback, int max)
        {
            double? n = Json.Num(value);
            if (!n.HasValue || double.IsNaN(n.Value)) return fallback;
            return (int)Math.Max(1, Math.Min(max, Math.Round(n.Value)));
        }
    }

    // Start with Windows: a per-user Run value, so no admin rights are needed.
    public static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "Capsule";

        public static bool IsEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                return key != null && key.GetValue(ValueName) != null;
        }

        public static void Set(bool on, string exePath)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) key.SetValue(ValueName, "\"" + exePath + "\"");
                else if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName);
                // The app was called UsageNotch; its old value would otherwise start a stale copy at logon.
                key.DeleteValue("UsageNotch", false);
            }
        }
    }
}
