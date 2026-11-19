using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Capsule
{
    // A settings row that records a global shortcut: a heading, a box you click and then press keys in, and a result line.
    // Chosen fires with a shortcut Capsule accepts; the owner registers it and answers with Result.
    public sealed class ShortcutField : StackPanel
    {
        readonly Border box = new Border();
        readonly TextBlock text = new TextBlock(), result = new TextBlock();
        string current = "";
        bool recording;

        public event Action<string> Chosen;

        public ShortcutField(UIElement heading, string name)
        {
            Children.Add(heading);
            text.FontSize = 13;
            text.FontWeight = FontWeights.SemiBold;
            box.Child = text;
            box.CornerRadius = new CornerRadius(10);
            box.Padding = new Thickness(10, 6, 10, 6);
            box.HorizontalAlignment = HorizontalAlignment.Left;
            box.MinWidth = 150;
            box.Focusable = true;
            InputMethod.SetIsInputMethodEnabled(box, false);
            box.FocusVisualStyle = null;
            box.Cursor = Cursors.Hand;
            System.Windows.Automation.AutomationProperties.SetName(box, name);
            box.MouseLeftButtonUp += delegate
            {
                Keyboard.Focus(box);
                recording = true;
                text.Text = "Press a shortcut...";
            };
            box.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (!recording || e.Key == Key.Escape) return;
                e.Handled = true;
                uint modifiers, key;
                if (!HotkeyText.FromKeys(e.Key, e.SystemKey, Keyboard.Modifiers, out modifiers, out key)) return;
                recording = false;
                string refusal = HotkeyText.Refusal(modifiers, key);
                if (refusal != null) Result(false, current, refusal);
                else if (Chosen != null) Chosen(HotkeyText.Format(modifiers, key));
            };
            box.LostKeyboardFocus += delegate { recording = false; text.Text = current; };
            Children.Add(box);
            result.FontSize = 11;
            result.TextWrapping = TextWrapping.Wrap;
            result.Margin = new Thickness(0, 3, 0, 0);
            Children.Add(result);
            Restyle();
        }

        public void Show(string shortcut, bool registered)
        {
            Result(registered, shortcut, registered ? "Registered. Click, then press a new shortcut." : "Shortcut unavailable. Choose another shortcut.");
        }

        public void Result(bool ok, string shortcut, string message)
        {
            current = shortcut;
            recording = false;
            text.Text = shortcut;
            result.Text = message;
            result.Foreground = NotchView.Brush(ok ? Palette.Green : Palette.Amber);
        }

        public void Restyle()
        {
            box.Background = NotchView.Brush(Palette.Track);
            text.Foreground = NotchView.Brush(Palette.Text);
        }
    }
}
