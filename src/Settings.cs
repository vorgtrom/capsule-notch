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
        public string GoogleClientId = "";   // the user's own OAuth client (Desktop app); "" = not set up. Its secret is in google-client-secret.bin
        // Calendars whose box the user changed: calendar id -> shown. The others follow Google's own "selected". Ids
        // only, never names.
        public Dictionary<string, bool> CalendarChoices = new Dictionary<string, bool>();

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
            c.GoogleClientId = (Json.Str(Json.Get(o, "google_client_id")) ?? "").Trim();
            Dictionary<string, object> choices = Json.Obj(Json.Get(o, "google_calendars"));
            if (choices != null)
                foreach (KeyValuePair<string, object> choice in choices)
                    if (choice.Key != "" && choice.Value is bool) c.CalendarChoices[choice.Key] = (bool)choice.Value;
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
            d["google_client_id"] = GoogleClientId;
            var choices = new Dictionary<string, object>();
            foreach (KeyValuePair<string, bool> choice in CalendarChoices) choices[choice.Key] = choice.Value;
            d["google_calendars"] = choices;
            return Files.WriteAtomic(path, Json.Write(d));
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
