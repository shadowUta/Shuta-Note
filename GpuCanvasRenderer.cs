using System.Numerics;
using System.Runtime.InteropServices;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using D2DFactoryType = Vortice.Direct2D1.FactoryType;
using DWriteFactoryType = Vortice.DirectWrite.FactoryType;
using DxgiFormat = Vortice.DXGI.Format;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;
using D3DFeatureLevel = Vortice.Direct3D.FeatureLevel;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;

namespace ShutaNote;

/// <summary>拥有 D3D11 共享纹理、Direct2D 目标及 DirectWrite 工厂的画布渲染器。</summary>
public sealed class GpuCanvasRenderer : IDisposable
{
    private ID3D11Device? device;
    private ID3D11DeviceContext? context;
    private ID3D11Texture2D? texture;
    private IDXGISurface? dxgiSurface;
    private ID2D1Factory? d2dFactory;
    private ID2D1RenderTarget? target;
    private IDWriteFactory? writeFactory;
    private ID2D1StrokeStyle? roundStrokeStyle;
    private readonly Dictionary<string, ID2D1Bitmap> imageCache = [];
    private int pixelWidth;
    private int pixelHeight;

    public bool IsReady => device is not null && d2dFactory is not null && writeFactory is not null;
    public bool HasSurface => target is not null && SharedHandle != IntPtr.Zero;
    public bool IsDeviceLost { get; private set; }
    public IntPtr SharedHandle { get; private set; }
    public string Status { get; private set; } = "未初始化";

    public bool TryInitialize()
    {
        DisposeNative();
        IsDeviceLost = false;
        try
        {
            D3DFeatureLevel[] featureLevels = [D3DFeatureLevel.Level_11_0, D3DFeatureLevel.Level_10_0];
            Vortice.Direct3D11.D3D11.D3D11CreateDevice((IDXGIAdapter?)null, DriverType.Hardware,
                DeviceCreationFlags.BgraSupport, featureLevels, out device!, out context!).CheckError();
            d2dFactory = Vortice.Direct2D1.D2D1.D2D1CreateFactory<ID2D1Factory>(D2DFactoryType.SingleThreaded, DebugLevel.None);
            StrokeStyleProperties strokeProperties = new()
            {
                StartCap = CapStyle.Round,
                EndCap = CapStyle.Round,
                DashCap = CapStyle.Round,
                LineJoin = LineJoin.Round
            };
            roundStrokeStyle = d2dFactory.CreateStrokeStyle(strokeProperties);
            writeFactory = Vortice.DirectWrite.DWrite.DWriteCreateFactory<IDWriteFactory>(DWriteFactoryType.Shared);
            Status = "D3D11/Direct2D/DirectWrite 已就绪";
            return true;
        }
        catch (Exception ex)
        {
            Status = $"GPU 渲染器初始化失败 · {ex.Message}";
            DisposeNative();
            return false;
        }
    }

    public bool TryCreateSurface(int width, int height)
    {
        DisposeSurface();
        if (!IsReady || device is null || d2dFactory is null) return false;
        try
        {
            pixelWidth = Math.Max(1, width);
            pixelHeight = Math.Max(1, height);
            Texture2DDescription description = new(DxgiFormat.B8G8R8A8_UNorm,
                (uint)pixelWidth, (uint)pixelHeight, 1, 1,
                BindFlags.RenderTarget | BindFlags.ShaderResource, ResourceUsage.Default,
                CpuAccessFlags.None, 1, 0, ResourceOptionFlags.Shared);
            texture = device.CreateTexture2D(description);
            using IDXGIResource resource = texture.QueryInterface<IDXGIResource>();
            SharedHandle = resource.SharedHandle;
            dxgiSurface = texture.QueryInterface<IDXGISurface>();
            RenderTargetProperties properties = new(new D2DPixelFormat(DxgiFormat.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied));
            target = d2dFactory.CreateDxgiSurfaceRenderTarget(dxgiSurface, properties);
            target.AntialiasMode = AntialiasMode.PerPrimitive;
            Status = $"共享画布 {pixelWidth}×{pixelHeight} 已就绪";
            return true;
        }
        catch (Exception ex)
        {
            Status = $"共享纹理创建失败 · {ex.Message}";
            DisposeSurface();
            return false;
        }
    }

    public bool Render(CanvasDocument document, bool darkMode, double zoom = 1, double offsetX = 0, double offsetY = 0, double dpiScale = 1)
    {
        if (target is null || writeFactory is null) return false;
        try
        {
            Color4 viewportBackground = darkMode ? Rgba(13, 16, 23) : Rgba(228, 232, 241);
            Color4 canvasBackground = darkMode ? Rgba(24, 27, 36) : Rgba(250, 250, 252);
            Color4 gridColor = darkMode ? Rgba(66, 72, 88, .72f) : Rgba(203, 208, 219, .78f);
            target.BeginDraw();
            target.Clear(viewportBackground);
            float pixelScale = (float)(zoom * dpiScale);
            Matrix3x2 viewportTransform = Matrix3x2.CreateScale(pixelScale) * Matrix3x2.CreateTranslation((float)(-offsetX * dpiScale), (float)(-offsetY * dpiScale));
            target.Transform = viewportTransform;
            using (ID2D1SolidColorBrush canvas = target.CreateSolidColorBrush(canvasBackground))
                target.FillRectangle(new Rect(0, 0, 5000, 3500), canvas);
            using (ID2D1SolidColorBrush grid = target.CreateSolidColorBrush(gridColor))
            {
                double left = offsetX / zoom, top = offsetY / zoom;
                double right = (offsetX + pixelWidth / dpiScale) / zoom, bottom = (offsetY + pixelHeight / dpiScale) / zoom;
                int firstX = Math.Max(0, (int)Math.Floor(left / 24) * 24);
                int firstY = Math.Max(0, (int)Math.Floor(top / 24) * 24);
                for (int x = firstX; x <= Math.Min(5000, right); x += 24) target.DrawLine(new Vector2(x, (float)Math.Max(0, top)), new Vector2(x, (float)Math.Min(3500, bottom)), grid, .65f);
                for (int y = firstY; y <= Math.Min(3500, bottom); y += 24) target.DrawLine(new Vector2((float)Math.Max(0, left), y), new Vector2((float)Math.Min(5000, right), y), grid, .65f);
            }

            foreach (CanvasStroke stroke in document.Strokes) DrawStroke(stroke);
            foreach (CanvasElement element in document.Elements.OrderBy(item => item.Z)) DrawElement(element);
            target.Transform = Matrix3x2.Identity;
            target.EndDraw(out _, out _).CheckError();
            context?.Flush();
            if (device?.DeviceRemovedReason.Failure == true) device.DeviceRemovedReason.CheckError();
            Status = $"Direct2D 已绘制 · {document.Strokes.Count} 笔迹 / {document.Elements.Count} 元素";
            return true;
        }
        catch (Exception ex)
        {
            IsDeviceLost = device?.DeviceRemovedReason.Failure == true || ex.HResult == unchecked((int)0x8899000C);
            Status = $"Direct2D 绘制失败 · {ex.Message}";
            return false;
        }
    }

    public bool TryRebuildSurface(int width, int height)
    {
        Status = "正在重建设备与共享画布";
        return TryInitialize() && TryCreateSurface(width, height);
    }

    public bool RenderStrokeSegments(IReadOnlyList<PointData> points, string color, double width,
        double zoom, double offsetX, double offsetY, double dpiScale = 1)
    {
        if (target is null || points.Count < 2) return false;
        try
        {
            target.BeginDraw();
            float pixelScale = (float)(zoom * dpiScale);
            target.Transform = Matrix3x2.CreateScale(pixelScale) * Matrix3x2.CreateTranslation((float)(-offsetX * dpiScale), (float)(-offsetY * dpiScale));
            using ID2D1SolidColorBrush brush = target.CreateSolidColorBrush(ParseColor(color));
            float strokeWidth = (float)Math.Max(.5, width);
            for (int index = 1; index < points.Count; index++)
            {
                PointData previous = points[index - 1];
                PointData current = points[index];
                target.DrawLine(new Vector2((float)previous.X, (float)previous.Y),
                    new Vector2((float)current.X, (float)current.Y), brush, strokeWidth, roundStrokeStyle);
            }
            target.Transform = Matrix3x2.Identity;
            target.EndDraw(out _, out _).CheckError();
            context?.Flush();
            return true;
        }
        catch (Exception ex)
        {
            IsDeviceLost = device?.DeviceRemovedReason.Failure == true || ex.HResult == unchecked((int)0x8899000C);
            Status = $"增量笔迹绘制失败 · {ex.Message}";
            return false;
        }
    }

    private void DrawStroke(CanvasStroke stroke)
    {
        if (target is null || stroke.Points.Count == 0) return;
        using ID2D1SolidColorBrush brush = target.CreateSolidColorBrush(ParseColor(stroke.Color));
        float width = (float)Math.Max(.5, stroke.Width);
        for (int index = 1; index < stroke.Points.Count; index++)
        {
            PointData previous = stroke.Points[index - 1];
            PointData current = stroke.Points[index];
            target.DrawLine(new Vector2((float)previous.X, (float)previous.Y), new Vector2((float)current.X, (float)current.Y), brush, width, roundStrokeStyle);
        }
    }

    private void DrawElement(CanvasElement element)
    {
        if (target is null || writeFactory is null) return;
        using ID2D1SolidColorBrush brush = target.CreateSolidColorBrush(ParseColor(element.Color ?? "#4841BD"));
        float left = (float)element.X;
        float top = (float)element.Y;
        float width = (float)Math.Max(1, element.Width);
        float height = (float)Math.Max(1, element.Height);
        Rect bounds = new(left, top, left + width, top + height);
        Matrix3x2 previousTransform = target.Transform;
        if (Math.Abs(element.Rotation) > .01)
            target.Transform = Matrix3x2.CreateRotation((float)(element.Rotation * Math.PI / 180), new Vector2(left + width / 2, top + height / 2)) * previousTransform;
        switch (element.Type)
        {
            case "Rectangle":
                target.DrawRectangle(bounds, brush, 2f);
                break;
            case "Ellipse":
                Vector2 center = new(left + width / 2, top + height / 2);
                target.DrawEllipse(new Ellipse(center, width / 2, height / 2), brush, 2f);
                break;
            case "Arrow":
                DrawArrow(element, brush);
                break;
            case "Image" when !string.IsNullOrWhiteSpace(element.Image):
                DrawImage(element.Image, bounds);
                break;
            case "Text" when !string.IsNullOrEmpty(element.Text):
                using (IDWriteTextFormat format = writeFactory.CreateTextFormat(
                    string.IsNullOrWhiteSpace(element.FontFamily) ? "Microsoft YaHei UI" : element.FontFamily,
                    null, element.Bold ? FontWeight.Bold : FontWeight.Normal,
                    element.Italic ? FontStyle.Italic : FontStyle.Normal,
                    FontStretch.Normal, (float)Math.Max(8, element.FontSize), "zh-cn"))
                {
                    target.DrawText(element.Text, format, bounds, brush);
                }
                break;
        }
        target.Transform = previousTransform;
    }

    private void DrawImage(string encodedImage, Rect bounds)
    {
        if (target is null) return;
        try
        {
            if (imageCache.TryGetValue(encodedImage, out ID2D1Bitmap? cached))
            {
                target.DrawBitmap(cached, bounds, 1f, BitmapInterpolationMode.Linear, new Rect(0, 0, cached.PixelSize.Width, cached.PixelSize.Height));
                return;
            }
            byte[] bytes = Convert.FromBase64String(encodedImage);
            using MemoryStream stream = new(bytes);
            BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource source = decoder.Frames[0];
            var converted = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
            int stride = converted.PixelWidth * 4;
            byte[] pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);
            IntPtr data = Marshal.AllocHGlobal(pixels.Length);
            try
            {
                Marshal.Copy(pixels, 0, data, pixels.Length);
                BitmapProperties properties = new(new D2DPixelFormat(DxgiFormat.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied));
                ID2D1Bitmap bitmap = target.CreateBitmap(new SizeI(converted.PixelWidth, converted.PixelHeight), data, (uint)stride, properties);
                imageCache[encodedImage] = bitmap;
                target.DrawBitmap(bitmap, bounds, 1f, BitmapInterpolationMode.Linear, new Rect(0, 0, converted.PixelWidth, converted.PixelHeight));
            }
            finally { Marshal.FreeHGlobal(data); }
        }
        catch { }
    }

    private void DrawArrow(CanvasElement element, ID2D1Brush brush)
    {
        if (target is null) return;
        Vector2 start = new((float)(element.X + (element.X1 ?? 0)), (float)(element.Y + (element.Y1 ?? 0)));
        Vector2 end = new((float)(element.X + (element.X2 ?? element.Width)), (float)(element.Y + (element.Y2 ?? element.Height)));
        target.DrawLine(start, end, brush, 2f);
        Vector2 direction = Vector2.Normalize(start - end);
        if (float.IsNaN(direction.X)) return;
        Vector2 normal = new(-direction.Y, direction.X);
        target.DrawLine(end, end + direction * 14 + normal * 6, brush, 2f);
        target.DrawLine(end, end + direction * 14 - normal * 6, brush, 2f);
    }

    private static Color4 ParseColor(string value)
    {
        try
        {
            string hex = value.TrimStart('#');
            if (hex.Length == 8) return Rgba(Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16), Convert.ToByte(hex[6..8], 16), Convert.ToByte(hex[0..2], 16) / 255f);
            if (hex.Length == 6) return Rgba(Convert.ToByte(hex[0..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16));
        }
        catch { }
        return Rgba(72, 65, 189);
    }

    private static Color4 Rgba(byte red, byte green, byte blue, float alpha = 1) => new(red / 255f, green / 255f, blue / 255f, alpha);

    public void Dispose()
    {
        DisposeNative();
        GC.SuppressFinalize(this);
    }

    private void DisposeSurface()
    {
        foreach (ID2D1Bitmap bitmap in imageCache.Values) bitmap.Dispose();
        imageCache.Clear();
        target?.Dispose();
        dxgiSurface?.Dispose();
        texture?.Dispose();
        target = null;
        dxgiSurface = null;
        texture = null;
        SharedHandle = IntPtr.Zero;
        pixelWidth = pixelHeight = 0;
    }

    private void DisposeNative()
    {
        DisposeSurface();
        writeFactory?.Dispose();
        roundStrokeStyle?.Dispose();
        d2dFactory?.Dispose();
        context?.Dispose();
        device?.Dispose();
        writeFactory = null;
        roundStrokeStyle = null;
        d2dFactory = null;
        context = null;
        device = null;
    }
}
