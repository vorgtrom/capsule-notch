using System;
using System.Collections.Generic;
using System.Windows.Media;
using Microsoft.Win32;

namespace Capsule
{
    // Light or dark, live glass or solid (spec §5): the colours every window is drawn with. Follows Windows' app
    // mode (AppsUseLightTheme) and Transparency effects (EnableTransparency), and changes live when they do.
    public sealed class Theme
    {
        const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        public bool Light { get; private set; }
        public bool Glass { get; private set; }           // live blur behind the windows; false = solid glass

        public Color Surface { get; private set; }        // the capsule, card and panel: a tint over the blur, or solid
        public Color RimTop { get; private set; }         // the bright 1-px edge along the top...
        public Color RimBottom { get; private set; }      // ...fading further down
        public Color Glow { get; private set; }           // the soft light just inside the top edge
        public Color Shadow { get; private set; }
        public Color Tile { get; private set; }           // the panel's tiles and input boxes
        public Color TileRim { get; private set; }
        public Color Text { get; private set; }
        public Color Secondary { get; private set; }
        public Color Track { get; private set; }          // ring tracks, bars and badges
        public Color Amber { get; private set; }          // waiting on you

        readonly Dictionary<string, SolidColorBrush> brushes = new Dictionary<string, SolidColorBrush>();

        public static Theme Current { get; private set; }
        public static event Action Changed;

        static Theme() { Current = For(false, false); }

        Theme() { }

        public static Theme For(bool light, bool glass)
        {
            var t = new Theme { Light = light, Glass = glass };
            if (!light)
            {
                t.Surface = Hex(glass ? "#66141416" : "#FA1C1C1E");
                t.RimTop = Hex(glass ? "#B3FFFFFF" : "#66FFFFFF");
                t.RimBottom = Hex(glass ? "#1FFFFFFF" : "#14FFFFFF");
                t.Glow = Hex(glass ? "#26FFFFFF" : "#14FFFFFF");
                t.Shadow = Hex("#66000000");
                t.Tile = Hex(glass ? "#14FFFFFF" : "#0FFFFFFF");
                t.TileRim = Hex(glass ? "#1AFFFFFF" : "#14FFFFFF");
                t.Text = Hex("#FFFFFFFF");
                t.Secondary = Hex(glass ? "#B3EBEBF5" : "#FF8E8E93");
                t.Track = Hex(glass ? "#40FFFFFF" : "#FF3A3A3C");
                t.Amber = Hex("#FFFF9F0A");
            }
            else
            {
                t.Surface = Hex(glass ? "#A6F5F5F7" : "#FAF2F2F7");
                t.RimTop = Hex(glass ? "#F2FFFFFF" : "#FFFFFFFF");
                t.RimBottom = Hex(glass ? "#4DFFFFFF" : "#1A000000");
                t.Glow = Hex(glass ? "#59FFFFFF" : "#66FFFFFF");
                t.Shadow = Hex("#40000000");
                t.Tile = Hex(glass ? "#66FFFFFF" : "#FFFFFFFF");
                t.TileRim = Hex(glass ? "#99FFFFFF" : "#14000000");
                t.Text = Hex("#FF1C1C1E");
                t.Secondary = Hex(glass ? "#993C3C43" : "#FF6C6C70");
                t.Track = Hex(glass ? "#24000000" : "#FFE5E5EA");
                t.Amber = Hex("#FFB25000");
            }
            return t;
        }

        // The theme Windows' settings ask for. Missing values mean Windows' defaults: light mode, transparency on.
        public static Theme FromSettings(int? appsUseLightTheme, int? enableTransparency, bool blurAvailable)
        {
            return For((appsUseLightTheme ?? 1) != 0, (enableTransparency ?? 1) != 0 && blurAvailable);
        }

        // Re-reads Windows' settings and raises Changed when the theme differs. blurAvailable: whether live blur
        // works on this PC at all.
        public static void Refresh(bool blurAvailable)
        {
            Theme next = FromSettings(ReadDword("AppsUseLightTheme"), ReadDword("EnableTransparency"), blurAvailable);
            if (next.Light == Current.Light && next.Glass == Current.Glass) return;
            Current = next;
            if (Changed != null) Changed();
        }

        // For previews and tests: draw with this theme from now on.
        public static void Use(Theme theme) { Current = theme; }

        static int? ReadDword(string name)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(PersonalizeKey))
                    return key == null ? null : key.GetValue(name) as int?;
            }
            catch (Exception) { return null; }
        }

        static Color Hex(string hex) { return (Color)ColorConverter.ConvertFromString(hex); }

        // A brush for a theme token (Palette.Text, Secondary, Track, Amber) or an accent such as "#30D158".
        public SolidColorBrush Brush(string color)
        {
            SolidColorBrush brush;
            if (brushes.TryGetValue(color, out brush)) return brush;
            if (color == Palette.Text) brush = Brush(Text);
            else if (color == Palette.Secondary) brush = Brush(Secondary);
            else if (color == Palette.Track) brush = Brush(Track);
            else if (color == Palette.Amber) brush = Brush(Amber);
            else
            {
                brush = new SolidColorBrush(Hex(color));
                brush.Freeze();
            }
            brushes[color] = brush;
            return brush;
        }

        public SolidColorBrush Brush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        // The edge highlight: bright along the top, fading to RimBottom by a third of the way down.
        public LinearGradientBrush RimBrush()
        {
            var brush = new LinearGradientBrush(RimTop, RimBottom, 90);
            brush.GradientStops[1].Offset = 0.35;
            brush.Freeze();
            return brush;
        }

        // The inner glow: Glow at the top, gone by a third of the way down.
        public LinearGradientBrush GlowBrush()
        {
            var brush = new LinearGradientBrush(Glow, Color.FromArgb(0, Glow.R, Glow.G, Glow.B), 90);
            brush.GradientStops[1].Offset = 0.35;
            brush.Freeze();
            return brush;
        }
    }
}
