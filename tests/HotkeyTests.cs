using System.IO;
using System.Windows.Input;

namespace Capsule
{
    public static class HotkeyTests
    {
        public static void Run()
        {
            ReadsShortcuts();
            RefusesShortcutsThatWouldStealTyping();
            RefusalsSayWhy();
            WritesShortcuts();
            ConfigKeepsTheShortcut();
            ReadsKeyPresses();
        }

        static void ReadsKeyPresses()
        {
            uint m, k;
            TestRunner.Check(HotkeyText.FromKeys(Key.N, Key.None, ModifierKeys.Control | ModifierKeys.Alt, out m, out k) && m == (HotkeyText.Ctrl | HotkeyText.Alt) && k == 0x4E, "Ctrl+Alt+N pressed");
            TestRunner.Check(HotkeyText.FromKeys(Key.System, Key.K, ModifierKeys.Alt, out m, out k) && m == HotkeyText.Alt && k == 0x4B, "with Alt held WPF reports System: the real key is used");
            TestRunner.Check(HotkeyText.FromKeys(Key.Space, Key.None, ModifierKeys.Windows | ModifierKeys.Shift, out m, out k) && m == (HotkeyText.Win | HotkeyText.Shift) && k == 0x20, "Win+Shift+Space");
            TestRunner.Check(!HotkeyText.FromKeys(Key.LeftCtrl, Key.None, ModifierKeys.Control, out m, out k), "only a modifier so far: keep waiting");
            TestRunner.Check(!HotkeyText.FromKeys(Key.Enter, Key.None, ModifierKeys.Control, out m, out k), "a key Capsule doesn't offer");
            TestRunner.Check(HotkeyText.FromKeys(Key.System, Key.F4, ModifierKeys.Alt, out m, out k) && !HotkeyText.Valid(m, k), "Alt+F4 in the shortcut box is read, then refused: registering it would take it from every window");
        }

        static void Parses(string text, uint modifiers, uint key)
        {
            uint m, k;
            bool ok = HotkeyText.TryParse(text, out m, out k);
            TestRunner.Check(ok && m == modifiers && k == key, text + " reads as modifiers " + modifiers + " and key " + key + " (got " + ok + ", " + m + ", " + k + ")");
        }

        static void Refuses(string text, string why)
        {
            uint m, k;
            TestRunner.Check(!HotkeyText.TryParse(text, out m, out k), why + ": " + (text ?? "null"));
        }

        static void ReadsShortcuts()
        {
            Parses("Ctrl+Alt+Space", HotkeyText.Ctrl | HotkeyText.Alt, 0x20);
            Parses("ctrl + alt + space", HotkeyText.Ctrl | HotkeyText.Alt, 0x20);
            Parses("Control+Shift+K", HotkeyText.Ctrl | HotkeyText.Shift, 0x4B);
            Parses("Win+Alt+7", HotkeyText.Win | HotkeyText.Alt, 0x37);
            Parses("Alt+F9", HotkeyText.Alt, 0x78);
            Parses("Ctrl+F24", HotkeyText.Ctrl, 0x87);
            Parses("Ctrl+Alt+F4", HotkeyText.Ctrl | HotkeyText.Alt, 0x73);   // only Alt+F4 itself is refused
            Parses(HotkeyText.Default, HotkeyText.Ctrl | HotkeyText.Alt, 0x4E);   // the default has to be a shortcut Capsule accepts, or it can't register its own
            Parses("Win+K", HotkeyText.Win, 0x4B);   // Win alone is enough: no Ctrl or Alt needed
        }

        static void RefusesShortcutsThatWouldStealTyping()
        {
            Refuses("Space", "no modifier");
            Refuses("Shift+K", "Shift alone would steal capital letters");
            Refuses("Ctrl+Alt", "no main key");
            Refuses("Ctrl+K+L", "two main keys");
            Refuses("Ctrl+Enter", "a key Capsule doesn't offer");
            Refuses("Ctrl+F25", "no such F-key");
            Refuses("Alt+F4", "it closes the window in front: a shortcut on it would take that from every app");
            Refuses("", "empty");
            Refuses(null, "nothing");
        }

        // The settings show why a shortcut was refused, and Alt+F4 gets a sentence of its own: the general one reads as if
        // Alt+F4 were fine with only a different modifier.
        static void RefusalsSayWhy()
        {
            TestRunner.Eq(null, HotkeyText.Refusal(HotkeyText.Ctrl | HotkeyText.Alt, 0x4E), "Ctrl+Alt+N: nothing to say");
            string f4 = HotkeyText.Refusal(HotkeyText.Alt, 0x73);
            TestRunner.Check(f4 != null && f4.Contains("Alt+F4") && !f4.Contains("Use Ctrl"), "Alt+F4 has its own sentence: " + f4);
            string noModifier = HotkeyText.Refusal(HotkeyText.Shift, 0x4B);
            TestRunner.Check(noModifier != null && noModifier.StartsWith("Use Ctrl, Alt or Win with"), "Shift+K gets the general one: " + noModifier);
            TestRunner.Check(HotkeyText.Refusal(HotkeyText.Ctrl, 0x0D) != null, "a key Capsule doesn't offer (Enter) is refused too");
            TestRunner.Eq(null, HotkeyText.Refusal(HotkeyText.Ctrl | HotkeyText.Alt, 0x73), "Ctrl+Alt+F4 is fine: only Alt+F4 itself is refused");
            TestRunner.Check(HotkeyText.Valid(HotkeyText.Alt, 0x73) == (HotkeyText.Refusal(HotkeyText.Alt, 0x73) == null) && HotkeyText.Valid(HotkeyText.Win, 0x4B) == (HotkeyText.Refusal(HotkeyText.Win, 0x4B) == null), "Valid and Refusal always agree");
        }

        static void WritesShortcuts()
        {
            TestRunner.Eq("Ctrl+Alt+Space", HotkeyText.Format(HotkeyText.Alt | HotkeyText.Ctrl, 0x20), "modifiers in Windows' order");
            TestRunner.Eq("Ctrl+Shift+Win+F12", HotkeyText.Format(HotkeyText.Win | HotkeyText.Shift | HotkeyText.Ctrl, 0x7B), "all of them");
            uint m, k;
            HotkeyText.TryParse("alt+ctrl+q", out m, out k);
            TestRunner.Eq("Ctrl+Alt+Q", HotkeyText.Format(m, k), "read and written back tidily");
            TestRunner.Eq("Ctrl+Alt+N", HotkeyText.Default, "the default: N for note, clear of the Claude app's Ctrl+Alt+Space");
        }

        static void ConfigKeepsTheShortcut()
        {
            string dir = TestRunner.NewTempDir();
            string path = Path.Combine(dir, "config.json");
            TestRunner.Eq(HotkeyText.Default, Config.Load(path).Hotkey, "the default until changed");
            new Config { Hotkey = "Ctrl+Shift+K" }.Save(path);
            TestRunner.Eq("Ctrl+Shift+K", Config.Load(path).Hotkey, "kept in config.json");
            File.WriteAllText(path, "{ \"hotkey\": \"Shift+K\" }");
            TestRunner.Eq(HotkeyText.Default, Config.Load(path).Hotkey, "a shortcut Capsule refuses falls back to the default");
            File.WriteAllText(path, "{ \"hotkey\": \"Alt+F4\" }");
            TestRunner.Eq(HotkeyText.Default, Config.Load(path).Hotkey, "and so does Alt+F4");
        }
    }
}
