using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace Capsule
{
    // A window's outline as polygons in physical pixels: the shape the glass's blur is cut to, and the window region
    // that lets clicks outside it fall through. Curves are flattened to a quarter of a pixel, which looks smooth.
    public static class GlassShape
    {
        public const double Tolerance = 0.25;   // pixels

        [StructLayout(LayoutKind.Sequential)]
        struct GdiPoint
        {
            public int X, Y;
        }

        [DllImport("gdi32.dll")] static extern IntPtr CreatePolyPolygonRgn(GdiPoint[] points, int[] counts, int polygons, int fillMode);

        // outline: in DIPs from the window's top-left, any transform applied. scale: the monitor's DPI / 96.
        public static List<Point[]> Polygons(Geometry outline, double scale)
        {
            var result = new List<Point[]>();
            if (outline == null || scale <= 0) return result;
            PathGeometry flat = outline.GetFlattenedPathGeometry(Tolerance / scale, ToleranceType.Absolute);
            foreach (PathFigure figure in flat.Figures)
            {
                var points = new List<Point> { Scaled(figure.StartPoint, scale) };
                foreach (PathSegment segment in figure.Segments)
                {
                    var poly = segment as PolyLineSegment;
                    var line = segment as LineSegment;
                    if (poly != null) foreach (Point p in poly.Points) points.Add(Scaled(p, scale));
                    else if (line != null) points.Add(Scaled(line.Point, scale));
                }
                if (points.Count >= 3) result.Add(points.ToArray());
            }
            return result;
        }

        static Point Scaled(Point p, double scale) { return new Point(p.X * scale, p.Y * scale); }

        // The polygons as a GDI region, filled the same way as the blur (non-zero winding). SetWindowRgn takes it over.
        public static IntPtr Region(List<Point[]> polygons)
        {
            var points = new List<GdiPoint>();
            var counts = new List<int>();
            foreach (Point[] polygon in polygons)
            {
                foreach (Point p in polygon) points.Add(new GdiPoint { X = (int)Math.Round(p.X), Y = (int)Math.Round(p.Y) });
                counts.Add(polygon.Length);
            }
            if (counts.Count == 0) return IntPtr.Zero;
            return CreatePolyPolygonRgn(points.ToArray(), counts.ToArray(), counts.Count, 2);   // WINDING
        }

        // Equal for equal outlines at the same window size, so an unchanged outline isn't rebuilt.
        public static string Key(List<Point[]> polygons, int width, int height)
        {
            var sb = new StringBuilder();
            sb.Append(width).Append('x').Append(height);
            foreach (Point[] polygon in polygons)
            {
                sb.Append('|');
                foreach (Point p in polygon)
                {
                    sb.Append(Math.Round(p.X, 1).ToString(CultureInfo.InvariantCulture)).Append(',');
                    sb.Append(Math.Round(p.Y, 1).ToString(CultureInfo.InvariantCulture)).Append(' ');
                }
            }
            return sb.ToString();
        }
    }
}
