using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Capsule
{
    public static class ThemeTests
    {
        public static void Run()
        {
            try
            {
                FollowsWindowsSettings();
                GlassIsSeeThroughAndSolidIsNot();
                ResolvesTokensAndAccents();
                ViewsDrawInTheCurrentTheme();
            }
            finally
            {
                Theme.Use(Theme.For(false, false));   // the default other tests expect
            }
        }

        static void FollowsWindowsSettings()
        {
            Theme dark = Theme.FromSettings(0, 1, true);
            TestRunner.Check(!dark.Light && dark.Glass, "dark mode with transparency: dark glass");
            Theme light = Theme.FromSettings(1, 1, true);
            TestRunner.Check(light.Light && light.Glass, "light mode: light glass");
            TestRunner.Check(!Theme.FromSettings(0, 0, true).Glass, "transparency effects off: solid");
            TestRunner.Check(!Theme.FromSettings(1, 1, false).Glass, "no live blur on this PC: solid");
            Theme defaults = Theme.FromSettings(null, null, true);
            TestRunner.Check(defaults.Light && defaults.Glass, "missing settings mean Windows' defaults: light, transparency on");
        }

        static void GlassIsSeeThroughAndSolidIsNot()
        {
            TestRunner.Check(Theme.For(false, true).Surface.A < 0xC0, "dark glass is a tint");
            TestRunner.Check(Theme.For(true, true).Surface.A < 0xC0, "light glass is a tint");
            TestRunner.Check(Theme.For(false, false).Surface.A >= 0xF0, "dark solid glass is opaque");
            TestRunner.Check(Theme.For(true, false).Surface.A >= 0xF0, "light solid glass is opaque");
            TestRunner.Eq(Colors.White, Theme.For(false, true).Text, "white text in dark mode");
            TestRunner.Check(Theme.For(true, true).Text.R < 0x40, "dark text in light mode");
            LinearGradientBrush rim = Theme.For(false, true).RimBrush();
            TestRunner.Check(rim.GradientStops[0].Color.A > rim.GradientStops[1].Color.A, "the rim is brightest along the top");
        }

        static void ResolvesTokensAndAccents()
        {
            Theme light = Theme.For(true, false);
            TestRunner.Eq(light.Text, light.Brush(Palette.Text).Color, "the text token");
            TestRunner.Eq(light.Secondary, light.Brush(Palette.Secondary).Color, "the secondary token");
            TestRunner.Eq(light.Track, light.Brush(Palette.Track).Color, "the track token");
            TestRunner.Eq(light.Amber, light.Brush(Palette.Amber).Color, "the amber token");
            Color darkAmber = Theme.For(false, true).Amber;
            TestRunner.Check(light.Amber.R < darkAmber.R && light.Amber.G < darkAmber.G && light.Amber.B <= darkAmber.B, "amber is deeper in light mode, where it is read as text: red and green are lower, blue is no higher");
            TestRunner.Eq((Color)ColorConverter.ConvertFromString(Palette.Green), light.Brush(Palette.Green).Color, "accents are plain colours");
            TestRunner.Check(light.Brush(Palette.Text).IsFrozen, "brushes are frozen");
            TestRunner.Check(ReferenceEquals(light.Brush(Palette.Amber), light.Brush(Palette.Amber)), "and made once");
        }

        static void ViewsDrawInTheCurrentTheme()
        {
            Theme light = Theme.For(true, false);
            Theme.Use(light);
            var view = new NotchView { Animate = false };
            view.Update(new List<CellModel> { new CellModel { Provider = "claude", Text = "42%" } }, false);
            TextBlock text = null;
            foreach (UIElement e in view.Children) if (e is TextBlock) text = (TextBlock)e;
            TestRunner.Check(text != null && ((SolidColorBrush)text.Foreground).Color == light.Text, "the capsule's text is dark in light mode");
            Theme.Use(Theme.For(false, false));
            view.Restyle();
            view.Update(new List<CellModel> { new CellModel { Provider = "claude", Text = "42%" } }, false);
            foreach (UIElement e in view.Children) if (e is TextBlock) text = (TextBlock)e;
            TestRunner.Check(((SolidColorBrush)text.Foreground).Color == Colors.White, "and white again after restyling in dark mode");
        }
    }
}
