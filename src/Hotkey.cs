using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace Capsule
{
    // Shortcut text such as "Ctrl+Alt+Space", and the modifiers and virtual key Windows registers (spec §3).
    public static class HotkeyText
    {
        public const uint Alt = 0x1, Ctrl = 0x2, Shift = 0x4, Win = 0x8;   // RegisterHotKey's MOD_ values
        const uint KeyF4 = 0x73;
        // N for note. Not Ctrl+Alt+Space: the Claude desktop app takes that for its own quick entry.
        public const string Default = "Ctrl+Alt+N";

        // False unless the text is a shortcut Capsule accepts: Ctrl, Alt or Win (Shift may join them) with one
        // letter, digit, F-key or Space.
        public static bool TryParse(string text, out uint modifiers, out uint key)
        {
            modifiers = 0;
            key = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            foreach (string raw in text.Split('+'))
            {
                string part = raw.Trim();
                if (Is(part, "Ctrl") || Is(part, "Control")) modifiers |= Ctrl;
                else if (Is(part, "Alt")) modifiers |= Alt;
                else if (Is(part, "Shift")) modifiers |= Shift;
                else if (Is(part, "Win") || Is(part, "Windows")) modifiers |= Win;
                else
                {
                    if (key != 0) return false;   // two main keys
                    key = KeyFromName(part);
                    if (key == 0) return false;
                }
            }
            return Valid(modifiers, key);
        }

        static bool Is(string part, string name) { return part.Equals(name, StringComparison.OrdinalIgnoreCase); }

        // One main key, with Ctrl, Alt or Win: Shift alone would take over ordinary typing. Alt+F4 is refused too: it
        // closes the window in front, and a shortcut on it would take that from every app.
        public static bool Valid(uint modifiers, uint key) { return Refusal(modifiers, key) == null; }

        // Why Capsule won't take this shortcut, as a sentence for the settings, or null when it will.
        public static string Refusal(uint modifiers, uint key)
        {
            if (modifiers == Alt && key == KeyF4) return "Alt+F4 closes windows, so it can't be a shortcut";
            if (KeyName(key) == "" || (modifiers & (Ctrl | Alt | Win)) == 0) return "Use Ctrl, Alt or Win with a letter, digit, F-key or Space";
            return null;
        }

        public static string Format(uint modifiers, uint key)
        {
            var parts = new List<string>();
            if ((modifiers & Ctrl) != 0) parts.Add("Ctrl");
            if ((modifiers & Alt) != 0) parts.Add("Alt");
            if ((modifiers & Shift) != 0) parts.Add("Shift");
            if ((modifiers & Win) != 0) parts.Add("Win");
            parts.Add(KeyName(key));
            return string.Join("+", parts);
        }

        // "Space", "K", "7", "F9", or "" for a key Capsule doesn't offer.
        public static string KeyName(uint key)
        {
            if (key == 0x20) return "Space";
            if ((key >= 0x30 && key <= 0x39) || (key >= 0x41 && key <= 0x5A)) return ((char)key).ToString();
            if (key >= 0x70 && key <= 0x87) return "F" + (key - 0x70 + 1);
            return "";
        }

        // A key press in the settings' shortcut box. False while only modifier keys are down.
        public static bool FromKeys(Key key, Key systemKey, ModifierKeys held, out uint modifiers, out uint vk)
        {
            modifiers = 0;
            if ((held & ModifierKeys.Control) != 0) modifiers |= Ctrl;
            if ((held & ModifierKeys.Alt) != 0) modifiers |= Alt;
            if ((held & ModifierKeys.Shift) != 0) modifiers |= Shift;
            if ((held & ModifierKeys.Windows) != 0) modifiers |= Win;
            Key pressed = key == Key.System ? systemKey : key;   // with Alt held, WPF reports the key as System
            vk = (uint)KeyInterop.VirtualKeyFromKey(pressed);
            return KeyName(vk) != "";
        }

        public static uint KeyFromName(string name)
        {
            if (Is(name, "Space")) return 0x20;
            if (name.Length == 1)
            {
                char c = char.ToUpperInvariant(name[0]);
                if ((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z')) return c;
            }
            int n;
            if (name.Length >= 2 && (name[0] == 'F' || name[0] == 'f') && int.TryParse(name.Substring(1), out n) && n >= 1 && n <= 24) return (uint)(0x70 + n - 1);
            return 0;
        }
    }

    // The global shortcut (spec §3), registered with Windows on a hidden window. Pressed fires on the UI thread.
    public sealed class GlobalHotkey : IDisposable
    {
        const int WM_HOTKEY = 0x0312, Id = 1;
        const uint MOD_NOREPEAT = 0x4000;   // holding the keys down doesn't repeat it

        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);

        readonly HwndSource window;
        bool registered;

        public event Action Pressed;

        public GlobalHotkey()
        {
            var p = new HwndSourceParameters("Capsule shortcut");
            p.WindowStyle = 0;
            p.ExtendedWindowStyle = 0x80;   // WS_EX_TOOLWINDOW: never in the taskbar
            p.PositionX = -32000;
            p.PositionY = -32000;
            p.Width = 1;
            p.Height = 1;
            window = new HwndSource(p);
            window.AddHook(Hook);
        }

        IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == Id)
            {
                handled = true;
                if (Pressed != null) Pressed();
            }
            return IntPtr.Zero;
        }

        // Replaces the shortcut. False when another app already has this one; then none is registered.
        public bool Register(uint modifiers, uint key)
        {
            Unregister();
            registered = RegisterHotKey(window.Handle, Id, modifiers | MOD_NOREPEAT, key);
            return registered;
        }

        void Unregister()
        {
            if (registered) UnregisterHotKey(window.Handle, Id);
            registered = false;
        }

        public void Dispose()
        {
            Unregister();
            window.Dispose();
        }
    }
}
