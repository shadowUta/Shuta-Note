namespace ShutaNote;

/// <summary>文档坐标、视口坐标与缩放之间的纯数据变换。</summary>
public sealed class CanvasCoordinateTransform
{
    public double Zoom { get; private set; } = 1;
    public double PanX { get; private set; }
    public double PanY { get; private set; }

    public void SetViewport(double zoom, double panX, double panY)
    {
        Zoom = Math.Clamp(zoom, .01, 100);
        PanX = panX;
        PanY = panY;
    }

    public PointData ToDocument(PointData viewport) => new((viewport.X - PanX) / Zoom, (viewport.Y - PanY) / Zoom);
    public PointData ToViewport(PointData document) => new(document.X * Zoom + PanX, document.Y * Zoom + PanY);
}
