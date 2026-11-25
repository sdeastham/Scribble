using System.Windows;
using System.Windows.Media;

namespace Scribble.Models;

public sealed class InkStroke
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Color Color { get; init; }
    public double BaseWidth { get; init; } = 2.8;
    public List<InkPoint> Points { get; } = new();

    private Rect _bounds = Rect.Empty;
    private int _boundsPointCount = -1;

    public Rect Bounds
    {
        get
        {
            if (_boundsPointCount != Points.Count)
            {
                RecomputeBounds();
            }
            return _bounds;
        }
    }

    public InkStroke(Color color, double baseWidth = 2.8)
    {
        Color = color;
        BaseWidth = baseWidth;
    }

    public InkStroke(Color color, double baseWidth, IEnumerable<InkPoint> points, Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        Color = color;
        BaseWidth = baseWidth;
        Points.AddRange(points);
        RecomputeBounds();
    }

    public void AddPoint(InkPoint point)
    {
        Points.Add(point);
        double pad = BaseWidth * 1.6;
        var ptRect = new Rect(point.X - pad, point.Y - pad, pad * 2, pad * 2);
        _bounds = _bounds.IsEmpty ? ptRect : Rect.Union(_bounds, ptRect);
        _boundsPointCount = Points.Count;
    }

    private void RecomputeBounds()
    {
        if (Points.Count == 0)
        {
            _bounds = Rect.Empty;
            _boundsPointCount = 0;
            return;
        }

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in Points)
        {
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }

        double pad = BaseWidth * 1.6;
        _bounds = new Rect(minX - pad, minY - pad, (maxX - minX) + pad * 2, (maxY - minY) + pad * 2);
        _boundsPointCount = Points.Count;
    }

    public bool HitTest(Point slidePoint, double tolerancePt)
    {
        if (Points.Count == 0)
        {
            return false;
        }

        Rect expanded = Bounds;
        expanded.Inflate(tolerancePt, tolerancePt);
        if (!expanded.Contains(slidePoint))
        {
            return false;
        }

        double thresholdSq = (tolerancePt + BaseWidth * 0.75) * (tolerancePt + BaseWidth * 0.75);

        if (Points.Count == 1)
        {
            double dx = slidePoint.X - Points[0].X;
            double dy = slidePoint.Y - Points[0].Y;
            return (dx * dx + dy * dy) <= thresholdSq;
        }

        for (int i = 0; i < Points.Count - 1; i++)
        {
            if (DistanceToSegmentSquared(slidePoint, Points[i], Points[i + 1]) <= thresholdSq)
            {
                return true;
            }
        }

        return false;
    }

    private static double DistanceToSegmentSquared(Point p, InkPoint a, InkPoint b)
    {
        double abx = b.X - a.X;
        double aby = b.Y - a.Y;
        double apx = p.X - a.X;
        double apy = p.Y - a.Y;
        double abLenSq = abx * abx + aby * aby;

        if (abLenSq < 1e-9)
        {
            return apx * apx + apy * apy;
        }

        double t = Math.Clamp((apx * abx + apy * aby) / abLenSq, 0.0, 1.0);
        double cx = a.X + t * abx;
        double cy = a.Y + t * aby;
        double dx = p.X - cx;
        double dy = p.Y - cy;
        return dx * dx + dy * dy;
    }
}
