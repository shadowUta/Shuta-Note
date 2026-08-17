namespace ShutaNote;

/// <summary>不依赖 WPF 的 GPU/软件画布元素命中测试。</summary>
public static class CanvasHitTester
{
    public static CanvasElement? HitElement(CanvasDocument document, PointData point, double tolerance = 6)
    {
        return document.Elements
            .OrderByDescending(element => element.Z)
            .FirstOrDefault(element => Contains(element, point, tolerance));
    }

    public static CanvasStroke? HitStroke(CanvasDocument document, PointData point, double tolerance = 6)
    {
        foreach (CanvasStroke stroke in document.Strokes.AsEnumerable().Reverse())
        {
            double radius = Math.Max(tolerance, stroke.Width / 2);
            for (int index = 1; index < stroke.Points.Count; index++)
                if (DistanceToSegment(point, stroke.Points[index - 1], stroke.Points[index]) <= radius) return stroke;
        }
        return null;
    }

    private static bool Contains(CanvasElement element, PointData point, double tolerance)
    {
        double left = element.X - tolerance;
        double top = element.Y - tolerance;
        double right = element.X + element.Width + tolerance;
        double bottom = element.Y + element.Height + tolerance;
        return point.X >= left && point.X <= right && point.Y >= top && point.Y <= bottom;
    }

    private static double DistanceToSegment(PointData point, PointData start, PointData end)
    {
        double dx = end.X - start.X, dy = end.Y - start.Y;
        if (dx == 0 && dy == 0) return Distance(point.X - start.X, point.Y - start.Y);
        double amount = Math.Clamp(((point.X - start.X) * dx + (point.Y - start.Y) * dy) / (dx * dx + dy * dy), 0, 1);
        return Distance(point.X - (start.X + amount * dx), point.Y - (start.Y + amount * dy));
    }

    private static double Distance(double x, double y) => Math.Sqrt(x * x + y * y);
}