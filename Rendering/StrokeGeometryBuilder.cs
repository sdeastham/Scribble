using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using Scribble.Models;

namespace Scribble.Rendering;

/// <summary>
/// Converts pressure-sensitive InkStrokes into smooth variable-width outline polygons
/// shared identically by WPF rendering and PDF vector appearance streams.
/// </summary>
public static class StrokeGeometryBuilder
{
    private const double MinWidthFactor = 0.32;
    private const double MaxWidthFactor = 1.50;
    private const int CapSegments = 6;

    public static double RadiusForPressure(float pressure, double baseWidth)
    {
        double p = Math.Clamp(pressure, 0.05, 1.0);
        double factor = MinWidthFactor + (MaxWidthFactor - MinWidthFactor) * Math.Pow(p, 0.85);
        return Math.Max(0.25, (baseWidth * 0.5) * factor);
    }

    public static List<Point> BuildOutlinePolygon(InkStroke stroke)
    {
        var raw = stroke.Points;
        if (raw.Count == 0)
        {
            return [];
        }

        // Deduplicate closely spaced samples
        var filtered = new List<InkPoint>(raw.Count) { raw[0] };
        for (int i = 1; i < raw.Count; i++)
        {
            double dx = raw[i].X - filtered[^1].X;
            double dy = raw[i].Y - filtered[^1].Y;
            if (dx * dx + dy * dy >= 0.09) // >= 0.3 pt spacing
            {
                filtered.Add(raw[i]);
            }
        }

        if (filtered.Count == 1)
        {
            double r = RadiusForPressure(filtered[0].Pressure, stroke.BaseWidth);
            return BuildCircle(filtered[0].X, filtered[0].Y, r, 14);
        }

        // Light smoothing pass on position + pressure to tame handwriting jitter
        var smoothed = SmoothPoints(filtered);

        // Subdivide long segments slightly with quadratic midpoint interpolation for smooth curves
        var spine = SubdivideSmooth(smoothed);

        int n = spine.Count;
        var left = new Point[n];
        var right = new Point[n];
        var radii = new double[n];
        var tangents = new Vector[n];

        for (int i = 0; i < n; i++)
        {
            radii[i] = RadiusForPressure(spine[i].Pressure, stroke.BaseWidth);

            Vector t;
            if (i == 0)
            {
                t = new Vector(spine[1].X - spine[0].X, spine[1].Y - spine[0].Y);
            }
            else if (i == n - 1)
            {
                t = new Vector(spine[n - 1].X - spine[n - 2].X, spine[n - 1].Y - spine[n - 2].Y);
            }
            else
            {
                var v1 = new Vector(spine[i].X - spine[i - 1].X, spine[i].Y - spine[i - 1].Y);
                var v2 = new Vector(spine[i + 1].X - spine[i].X, spine[i + 1].Y - spine[i].Y);
                if (v1.LengthSquared > 1e-9) v1.Normalize();
                if (v2.LengthSquared > 1e-9) v2.Normalize();
                t = v1 + v2;
                if (t.LengthSquared < 1e-9)
                {
                    t = v2.LengthSquared > 1e-9 ? v2 : new Vector(1, 0);
                }
            }

            if (t.LengthSquared < 1e-9)
            {
                t = new Vector(1, 0);
            }
            else
            {
                t.Normalize();
            }

            tangents[i] = t;
            var normal = new Vector(-t.Y, t.X);
            left[i] = new Point(spine[i].X + normal.X * radii[i], spine[i].Y + normal.Y * radii[i]);
            right[i] = new Point(spine[i].X - normal.X * radii[i], spine[i].Y - normal.Y * radii[i]);
        }

        var polygon = new List<Point>(n * 2 + CapSegments * 2 + 2);

        // Start cap (from right[0] around back to left[0])
        // Right normal is (t.Y, -t.X), Left normal is (-t.Y, t.X)
        double rightStartAngle = Math.Atan2(-tangents[0].X, tangents[0].Y);
        for (int s = 1; s < CapSegments; s++)
        {
            double a = rightStartAngle - Math.PI * s / CapSegments;
            polygon.Add(new Point(
                spine[0].X + Math.Cos(a) * radii[0],
                spine[0].Y + Math.Sin(a) * radii[0]));
        }

        // Left side forward
        for (int i = 0; i < n; i++)
        {
            polygon.Add(left[i]);
        }

        // End cap (from left[n-1] around front to right[n-1])
        double leftEndAngle = Math.Atan2(tangents[n - 1].X, -tangents[n - 1].Y);
        for (int s = 1; s < CapSegments; s++)
        {
            double a = leftEndAngle - Math.PI * s / CapSegments;
            polygon.Add(new Point(
                spine[n - 1].X + Math.Cos(a) * radii[n - 1],
                spine[n - 1].Y + Math.Sin(a) * radii[n - 1]));
        }

        // Right side backward
        for (int i = n - 1; i >= 0; i--)
        {
            polygon.Add(right[i]);
        }

        return polygon;
    }

    public static StreamGeometry? BuildWpfGeometry(InkStroke stroke)
    {
        var poly = BuildOutlinePolygon(stroke);
        if (poly.Count < 3)
        {
            return null;
        }

        var geom = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var ctx = geom.Open())
        {
            ctx.BeginFigure(poly[0], isFilled: true, isClosed: true);
            ctx.PolyLineTo(poly.GetRange(1, poly.Count - 1), isStroked: false, isSmoothJoin: false);
        }
        geom.Freeze();
        return geom;
    }

    /// <summary>
    /// Builds PDF content stream operators for a Normal Appearance Stream (/AP /N)
    /// using PDF bottom-left origin coordinates.
    /// </summary>
    public static string BuildPdfAppearanceStream(InkStroke stroke, double pageHeightPt)
    {
        var poly = BuildOutlinePolygon(stroke);
        if (poly.Count < 3)
        {
            return string.Empty;
        }

        var inv = CultureInfo.InvariantCulture;
        double r = stroke.Color.R / 255.0;
        double g = stroke.Color.G / 255.0;
        double b = stroke.Color.B / 255.0;

        var sb = new StringBuilder(poly.Count * 18 + 64);
        sb.Append("q\n");
        sb.AppendFormat(inv, "{0:0.###} {1:0.###} {2:0.###} rg\n", r, g, b);

        Point p0 = poly[0];
        sb.AppendFormat(inv, "{0:0.##} {1:0.##} m\n", p0.X, pageHeightPt - p0.Y);

        for (int i = 1; i < poly.Count; i++)
        {
            Point p = poly[i];
            sb.AppendFormat(inv, "{0:0.##} {1:0.##} l\n", p.X, pageHeightPt - p.Y);
        }

        sb.Append("h\nf\nQ\n");
        return sb.ToString();
    }

    private static List<Point> BuildCircle(double cx, double cy, double radius, int segments)
    {
        var pts = new List<Point>(segments);
        for (int i = 0; i < segments; i++)
        {
            double a = (2.0 * Math.PI * i) / segments;
            pts.Add(new Point(cx + Math.Cos(a) * radius, cy + Math.Sin(a) * radius));
        }
        return pts;
    }

    private static List<InkPoint> SmoothPoints(List<InkPoint> input)
    {
        if (input.Count <= 2)
        {
            return input;
        }

        var result = new List<InkPoint>(input.Count) { input[0] };
        for (int i = 1; i < input.Count - 1; i++)
        {
            var prev = input[i - 1];
            var curr = input[i];
            var next = input[i + 1];

            double x = 0.2 * prev.X + 0.6 * curr.X + 0.2 * next.X;
            double y = 0.2 * prev.Y + 0.6 * curr.Y + 0.2 * next.Y;
            float p = 0.25f * prev.Pressure + 0.5f * curr.Pressure + 0.25f * next.Pressure;
            result.Add(new InkPoint(x, y, p));
        }
        result.Add(input[^1]);
        return result;
    }

    private static List<InkPoint> SubdivideSmooth(List<InkPoint> pts)
    {
        if (pts.Count <= 2)
        {
            return pts;
        }

        // One pass of quadratic midpoint refinement for silky curves
        var result = new List<InkPoint>(pts.Count * 2) { pts[0] };

        for (int i = 1; i < pts.Count - 1; i++)
        {
            var prev = pts[i - 1];
            var curr = pts[i];
            var next = pts[i + 1];

            var m1 = new InkPoint(
                (prev.X + curr.X) * 0.5,
                (prev.Y + curr.Y) * 0.5,
                (prev.Pressure + curr.Pressure) * 0.5f);

            var m2 = new InkPoint(
                (curr.X + next.X) * 0.5,
                (curr.Y + next.Y) * 0.5,
                (curr.Pressure + next.Pressure) * 0.5f);

            // Evaluate quadratic Bezier (m1, curr, m2) at t = 0.5
            var mid = new InkPoint(
                0.25 * m1.X + 0.5 * curr.X + 0.25 * m2.X,
                0.25 * m1.Y + 0.5 * curr.Y + 0.25 * m2.Y,
                0.25f * m1.Pressure + 0.5f * curr.Pressure + 0.25f * m2.Pressure);

            AppendIfDistinct(result, m1);
            AppendIfDistinct(result, mid);
        }

        AppendIfDistinct(result, pts[^1]);
        return result;
    }

    private static void AppendIfDistinct(List<InkPoint> list, InkPoint pt)
    {
        if (list.Count == 0)
        {
            list.Add(pt);
            return;
        }

        var last = list[^1];
        double dx = pt.X - last.X;
        double dy = pt.Y - last.Y;
        if (dx * dx + dy * dy >= 0.04)
        {
            list.Add(pt);
        }
    }
}
