using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using WPath = System.Windows.Shapes.Path;

namespace Capsule
{
    // The look every Capsule window shares (spec §5), drawn over the live blur, or alone as solid glass: a tint, a
    // soft glow inside the top edge, a bright 1-px rim and, for windows that float, a soft shadow outside the
    // outline only (so it never darkens the glass). A Canvas, so it takes no room in layout: put it first in a
    // window's content and give it the window's outline.
    public sealed class GlassSurface : Canvas
    {
        readonly Canvas shadowHost = new Canvas();
        readonly WPath shadow = new WPath();
        readonly WPath surface = new WPath();
        readonly WPath glow = new WPath();
        readonly WPath rim = new WPath();

        public GlassSurface()
        {
            shadow.Effect = new BlurEffect { Radius = 16 };
            shadow.RenderTransform = new TranslateTransform(0, 4);
            shadowHost.Children.Add(shadow);
            rim.StrokeThickness = 1;
            Children.Add(shadowHost);
            Children.Add(surface);
            Children.Add(glow);
            Children.Add(rim);
        }

        // outline: the window's shape, in this canvas's coordinates. edge: the line the rim follows (null = the
        // whole outline; the capsule leaves out the side on the screen edge). shadowRoom: how far the shadow may
        // spread outside the outline, 0 for none.
        public void Apply(Geometry outline, Geometry edge, double shadowRoom)
        {
            // This canvas takes no room, so a new outline never changes its size and layout wouldn't measure it again:
            // its shapes would be arranged at their old, empty size and clipped away. Have it measured afresh.
            InvalidateMeasure();
            // The shadow sits one canvas deeper, in shadowHost, which the call above doesn't reach: without this its
            // slot stays empty and it is clipped away.
            shadowHost.InvalidateMeasure();
            Theme theme = Theme.Current;
            surface.Data = outline;
            surface.Fill = theme.Brush(theme.Surface);
            glow.Data = outline;
            glow.Fill = theme.GlowBrush();
            rim.Data = edge ?? outline;
            rim.Stroke = theme.RimBrush();
            if (shadowRoom <= 0 || outline == null)
            {
                shadowHost.Visibility = Visibility.Collapsed;
                return;
            }
            shadow.Data = outline;
            shadow.Fill = theme.Brush(theme.Shadow);
            Rect room = outline.Bounds;
            room.Inflate(shadowRoom, shadowRoom);
            var outside = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(room), outline);
            outside.Freeze();
            shadowHost.Clip = outside;
            shadowHost.Visibility = Visibility.Visible;
        }

        public Geometry Outline { get { return surface.Data; } }
        public Brush SurfaceFill { get { return surface.Fill; } }
    }
}
