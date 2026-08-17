namespace ShutaNote;

/// <summary>
/// 与 WPF 视觉树无关的白板文档快照，作为 GPU 渲染和旧版 WPF 适配器之间的稳定边界。
/// </summary>
public sealed class CanvasDocument
{
    public List<CanvasStroke> Strokes { get; } = [];
    public List<CanvasElement> Elements { get; } = [];

    public static CanvasDocument FromState(BoardState state)
    {
        var document = new CanvasDocument();
        document.Strokes.AddRange(state.Strokes.Select(CanvasStroke.FromLegacy));
        document.Elements.AddRange(state.Elements.Select(CanvasElement.FromLegacy));
        return document;
    }

    public BoardState ToState() => new()
    {
        Strokes = Strokes.Select(stroke => stroke.ToLegacy()).ToList(),
        Elements = Elements.Select(element => element.ToLegacy()).ToList()
    };
}

/// <summary>与 WPF 无关的笔迹。</summary>
public sealed class CanvasStroke
{
    public string Color { get; set; } = "#4841BD";
    public double Width { get; set; } = 3;
    public List<PointData> Points { get; } = [];

    internal static CanvasStroke FromLegacy(StrokeData source)
    {
        var stroke = new CanvasStroke { Color = source.Color, Width = source.Width };
        stroke.Points.AddRange(source.Points.Select(point => new PointData(point.X, point.Y)));
        return stroke;
    }

    internal StrokeData ToLegacy() => new()
    {
        Color = Color,
        Width = Width,
        Points = Points.Select(point => new PointData(point.X, point.Y)).ToList()
    };
}

/// <summary>与 WPF 无关的画布元素，覆盖旧版 JSON 的全部字段。</summary>
public sealed class CanvasElement
{
    public string Type { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public int Z { get; set; }
    public string? Text { get; set; }
    public string? Image { get; set; }
    public string? Color { get; set; }
    public string? FontFamily { get; set; }
    public double FontSize { get; set; }
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    public double Rotation { get; set; }
    public double? X1 { get; set; }
    public double? Y1 { get; set; }
    public double? X2 { get; set; }
    public double? Y2 { get; set; }

    internal static CanvasElement FromLegacy(ElementData source) => new()
    {
        Type = source.Type,
        X = source.X,
        Y = source.Y,
        Width = source.Width,
        Height = source.Height,
        Z = source.Z,
        Text = source.Text,
        Image = source.Image,
        Color = source.Color,
        FontFamily = source.FontFamily,
        FontSize = source.FontSize,
        Bold = source.Bold,
        Italic = source.Italic,
        Underline = source.Underline,
        Rotation = source.Rotation,
        X1 = source.X1,
        Y1 = source.Y1,
        X2 = source.X2,
        Y2 = source.Y2
    };

    internal ElementData ToLegacy() => new()
    {
        Type = Type, X = X, Y = Y, Width = Width, Height = Height, Z = Z,
        Text = Text, Image = Image, Color = Color, FontFamily = FontFamily,
        FontSize = FontSize, Bold = Bold, Italic = Italic, Underline = Underline,
        Rotation = Rotation, X1 = X1, Y1 = Y1, X2 = X2, Y2 = Y2
    };
}

// Legacy DTOs intentionally remain serializable so existing JSON files are unchanged.
public sealed class BoardState { public List<StrokeData> Strokes { get; set; } = []; public List<ElementData> Elements { get; set; } = []; }
public sealed class StrokeData { public string Color { get; set; } = "#4841BD"; public double Width { get; set; } = 3; public List<PointData> Points { get; set; } = []; }
public sealed record PointData(double X, double Y);
public sealed class ElementData { public string Type { get; set; } = ""; public double X { get; set; } public double Y { get; set; } public double Width { get; set; } public double Height { get; set; } public int Z { get; set; } public string? Text { get; set; } public string? Image { get; set; } public string? Color { get; set; } public string? FontFamily { get; set; } public double FontSize { get; set; } public bool Bold { get; set; } public bool Italic { get; set; } public bool Underline { get; set; } public double Rotation { get; set; } public double? X1 { get; set; } public double? Y1 { get; set; } public double? X2 { get; set; } public double? Y2 { get; set; } }

public enum CanvasRenderBackend
{
    WpfFallback,
    Direct2DComposition
}

public sealed class CanvasRenderOptions
{
    public CanvasRenderBackend Backend { get; set; } = CanvasRenderBackend.Direct2DComposition;
    public bool EnableGpuFallback { get; set; } = true;
    public bool ShowDiagnostics { get; set; }
}
