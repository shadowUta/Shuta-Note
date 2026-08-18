using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Interop;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using IOPath = System.IO.Path;

namespace ShutaNote;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<BoardInfo> boards = [];
    private readonly string dataDir = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShutaNote");
    private readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = true };
    private readonly Stack<string> undo = new();
    private readonly Stack<string> redo = new();
    private readonly string settingsPath;
    private AppearanceSettings appearance = new();
    private CanvasRenderOptions renderOptions = new();
    private readonly GpuDeviceManager gpuDevice = new();
    private readonly GpuCanvasRenderer gpuCanvas = new();
    private readonly CanvasCoordinateTransform coordinateTransform = new();
    private readonly SemaphoreSlim saveGate = new(1, 1);
    private CancellationTokenSource? saveDebounce;
    private string? pendingBoardPath, pendingBoardJson, pendingIndexJson;
    private D3DImageCanvasHost? gpuCanvasHost;
    private bool rebuildingGpuBackend;
    private bool viewportRenderQueued;
    private bool interactiveRenderQueued;
    private CanvasDocument? renderedDocument;
    private double viewportDpiScale = 1;
    private HwndSource? windowSource;
    private BoardInfo? currentBoard;
    private ToolKind tool;
    private Shape? drawingShape;
    private Stroke? activeStroke;
    private readonly List<StylusPoint> pendingStrokePoints = [];
    private bool strokeFrameSubscribed;
    private StylusPoint? gpuStrokeLastPoint;
    private Point startPoint, panStart, selectionStart, rotationCenter, elementDragStart;
    private double panHorizontal, panVertical, zoom = 1;
    private bool isPanning, isMarqueeSelecting, isRotating, isDraggingElements, isColorPicking, colorPreviewStarted, isThemeColorPicking, restoring, appearanceReady;
    private double rotationStartAngle, shapeStartRotation, colorSaturation = 1, colorValue = 1;
    private Shape? rotatingShape;
    private readonly List<(UIElement Element, double Left, double Top)> draggedElements = [];
    private bool focusMode, sidebarCollapsed;
    private Color activeColor = Color.FromRgb(72, 65, 189);
    private Action? dialogConfirmAction;
    private FileDialogMode fileDialogMode;
    private string fileDialogDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

    public MainWindow()
    {
        InitializeComponent();
        Directory.CreateDirectory(dataDir);
        settingsPath = IOPath.Combine(dataDir, "settings.json");
        LoadAppearanceSettings();
        BoardList.ItemsSource = boards;
        BoardCanvas.DefaultDrawingAttributes = new DrawingAttributes { Color = activeColor, Width = 3, Height = 3, FitToCurve = true };
        LoadIndex();
        if (boards.Count == 0) CreateBoard("我的第一个白板");
        BoardList.SelectedIndex = 0;
        SetTool(ToolKind.Select);
        Loaded += (_, _) => { CanvasScroll.ScrollToHorizontalOffset(1700); CanvasScroll.ScrollToVerticalOffset(1150); };
        Loaded += (_, _) => InitializeRenderBackend();
        SourceInitialized += (_, _) =>
        {
            ApplySystemBackdrop();
            windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            windowSource?.AddHook(WindowMessageHook);
        };
        Closed += (_, _) => { UnsubscribeStrokeFrame(); saveDebounce?.Dispose(); saveGate.Dispose(); windowSource?.RemoveHook(WindowMessageHook); gpuCanvasHost?.Dispose(); gpuCanvas.Dispose(); gpuDevice.Dispose(); };
    }

    private void LoadAppearanceSettings()
    {
        try { if (File.Exists(settingsPath)) appearance = JsonSerializer.Deserialize<AppearanceSettings>(File.ReadAllText(settingsPath)) ?? new(); }
        catch { appearance = new(); }
        renderOptions = appearance.RenderOptions ?? new();
        LightModeRadio.IsChecked = !appearance.DarkMode;
        DarkModeRadio.IsChecked = appearance.DarkMode;
        GlassOpacitySlider.Value = Math.Clamp(appearance.GlassOpacity, 35, 100);
        GlassBlurSlider.Value = Math.Clamp(appearance.GlassBlur, 0, 40);
        GpuCanvasCheckBox.IsChecked = renderOptions.Backend == CanvasRenderBackend.Direct2DComposition;
        GpuFallbackCheckBox.IsChecked = renderOptions.EnableGpuFallback;
        GpuDiagnosticsCheckBox.IsChecked = renderOptions.ShowDiagnostics;
        appearanceReady = true; ApplyAppearance(false);
    }

    private void SaveAppearanceSettings()
    {
        if (!appearanceReady) return;
        appearance.RenderOptions = renderOptions;
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(appearance, jsonOptions));
    }
    private void ApplyAppearance(bool save = true)
    {
        Color accent = ParseColor(appearance.AccentColor, Color.FromRgb(102, 87, 232)); bool dark = appearance.DarkMode;
        SetBrush("Primary", accent); SetBrush("TextBrush", dark ? Color.FromRgb(235, 238, 246) : Color.FromRgb(28, 32, 51));
        SetBrush("WindowBackground", dark ? Color.FromRgb(18, 21, 29) : Color.FromRgb(233, 237, 245), dark ? .72 : .58);
        // Keep the WPF panels translucent so the window-level Acrylic compositor is visible.
        SetBrush("GlassBrush", dark ? Color.FromRgb(37, 41, 53) : Colors.White, appearance.GlassOpacity / 100d * .24);
        SetBrush("GlassBrushStrong", dark ? Color.FromRgb(42, 46, 59) : Colors.White, appearance.GlassOpacity / 100d * .34);
        SetBrush("ToolbarGlassBrush", dark ? Color.FromRgb(37, 41, 53) : Colors.White, appearance.GlassOpacity / 100d * .18);
        SetBrush("BorderBrush", dark ? Color.FromRgb(83, 89, 106) : Color.FromRgb(205, 210, 223), .58);
        SetBrush("HoverBrush", Mix(accent, dark ? Color.FromRgb(38, 42, 54) : Colors.White, dark ? .24 : .14));
        SetBrush("MutedBrush", dark ? Color.FromRgb(165, 171, 190) : Color.FromRgb(125, 132, 153));
        SetBrush("CanvasBrush", dark ? Color.FromRgb(24, 27, 36) : Color.FromRgb(250, 250, 252));
        SetBrush("GridBrush", dark ? Color.FromRgb(66, 72, 88) : Color.FromRgb(203, 208, 219));
        SetBrush("ViewportBrush", dark ? Color.FromRgb(13, 16, 23) : Color.FromRgb(228, 232, 241));
        SetBrush("SelectionBrush", dark ? Colors.White : accent);
        SetBrush("ActiveColorBrush", activeColor);
        OpacityValueText.Text = $"{appearance.GlassOpacity:0}%"; GlassBlurValueText.Text = $"{appearance.GlassBlur:0}"; if (save) SaveAppearanceSettings(); if (new WindowInteropHelper(this).Handle != IntPtr.Zero) ApplySystemBackdrop(); if (IsLoaded) RenderGpuDocument();
    }
    private void SetBrush(string key, Color color, double opacity = 1) => Resources[key] = new SolidColorBrush(color) { Opacity = opacity };
    private static Color ParseColor(string value, Color fallback) { try { return (Color)ColorConverter.ConvertFromString(value); } catch { return fallback; } }
    private static Color Mix(Color foreground, Color background, double amount) => Color.FromRgb((byte)(background.R + (foreground.R - background.R) * amount), (byte)(background.G + (foreground.G - background.G) * amount), (byte)(background.B + (foreground.B - background.B) * amount));
    private void ApplySystemBackdrop()
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            int dark = appearance.DarkMode ? 1 : 0;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));

            // Acrylic blur is a compositor effect; DropShadowEffect only blurs a shadow.
            // GradientColor is stored as AABBGGRR by SetWindowCompositionAttribute.
            uint alpha = (uint)Math.Clamp(appearance.GlassOpacity * .72, 25, 150);
            byte tint = appearance.DarkMode ? (byte)28 : (byte)238;
            byte blurTint = (byte)Math.Clamp(tint + appearance.GlassBlur * (appearance.DarkMode ? .35 : .08), 0, 255);
            uint gradientColor = alpha << 24 | (uint)blurTint << 16 | (uint)blurTint << 8 | blurTint;
            AccentPolicy policy = new() { AccentState = AccentEnableAcrylicBlurBehind, AccentFlags = 2, GradientColor = gradientColor };
            int size = Marshal.SizeOf(policy);
            IntPtr policyPtr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, policyPtr, false);
                WindowCompositionAttributeData data = new() { Attribute = WcaAccentPolicy, Data = policyPtr, SizeOfData = size };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally { Marshal.FreeHGlobal(policyPtr); }
        }
        catch { }
    }

    private void InitializeRenderBackend()
    {
        if (renderOptions.Backend != CanvasRenderBackend.Direct2DComposition)
        {
            HintText.Text = "WPF 软件画布 · GPU 已关闭";
            return;
        }

        if (gpuDevice.TryInitialize())
        {
            (int surfaceWidth, int surfaceHeight) = GetGpuSurfacePixels();
            if (gpuCanvas.TryInitialize() && gpuCanvas.TryCreateSurface(surfaceWidth, surfaceHeight))
            {
                gpuCanvasHost = CreateGpuHost();
                if (gpuCanvasHost.TryInitialize(new WindowInteropHelper(this).Handle, surfaceWidth, surfaceHeight, gpuCanvas.SharedHandle))
                {
                    Viewport.Children.Insert(0, gpuCanvasHost);
                    Panel.SetZIndex(gpuCanvasHost, -1);
                    BoardCanvas.Opacity = 1;
                    RenderGpuDocument();
                    UpdateGpuDiagnostics();
                }
                else
                {
                    gpuCanvasHost.Dispose();
                    gpuCanvasHost = null;
                    if (renderOptions.EnableGpuFallback)
                    {
                        renderOptions.Backend = CanvasRenderBackend.WpfFallback;
                        SaveAppearanceSettings();
                        HintText.Text = "D3DImage 共享表面不可用，已自动回退到 WPF 画布";
                        return;
                    }
                }
                if (renderOptions.ShowDiagnostics) HintText.Text = $"GPU 画布基础设施已就绪 · {gpuDevice.Status} · {gpuCanvas.Status}";
            }
            else if (renderOptions.EnableGpuFallback)
            {
                renderOptions.Backend = CanvasRenderBackend.WpfFallback;
                SaveAppearanceSettings();
                HintText.Text = $"Direct2D 不可用，已自动回退到 WPF 画布 · {gpuCanvas.Status}";
            }
            return;
        }

        if (renderOptions.EnableGpuFallback)
        {
            renderOptions.Backend = CanvasRenderBackend.WpfFallback;
            appearance.RenderOptions = renderOptions;
            SaveAppearanceSettings();
            HintText.Text = $"GPU 不可用，已自动回退到 WPF 画布 · {gpuDevice.Status}";
            return;
        }

        HintText.Text = $"GPU 初始化失败 · {gpuDevice.Status}";
    }
    private const int WcaAccentPolicy = 19;
    private const int AccentDisabled = 0;
    private const int AccentEnableAcrylicBlurBehind = 4;
    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;
    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy { public int AccentState; public int AccentFlags; public uint GradientColor; public int AnimationId; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData { public int Attribute; public IntPtr Data; public int SizeOfData; }
    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmEnterSizeMove)
        {
            SetAccentState(hwnd, AccentDisabled);
            if (gpuCanvasHost is not null) gpuCanvasHost.Visibility = Visibility.Hidden;
        }
        else if (message == WmExitSizeMove)
        {
            ApplySystemBackdrop();
            if (gpuCanvasHost is not null) TryRebuildGpuBackend();
        }
        return IntPtr.Zero;
    }

    private static void SetAccentState(IntPtr hwnd, int state)
    {
        AccentPolicy policy = new() { AccentState = state };
        int size = Marshal.SizeOf(policy);
        IntPtr policyPtr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, policyPtr, false);
            WindowCompositionAttributeData data = new() { Attribute = WcaAccentPolicy, Data = policyPtr, SizeOfData = size };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally { Marshal.FreeHGlobal(policyPtr); }
    }

    private void LoadIndex()
    {
        string path = IOPath.Combine(dataDir, "boards.json");
        try { if (File.Exists(path)) foreach (var board in JsonSerializer.Deserialize<List<BoardInfo>>(File.ReadAllText(path)) ?? []) boards.Add(board); }
        catch { }
    }
    private void SaveIndex() => File.WriteAllText(IOPath.Combine(dataDir, "boards.json"), JsonSerializer.Serialize(boards, jsonOptions));
    private string BoardPath(BoardInfo board) => IOPath.Combine(dataDir, board.Id + ".json");

    private void CreateBoard(string name)
    {
        var board = new BoardInfo { Id = Guid.NewGuid().ToString("N"), Name = name, UpdatedAt = DateTime.Now };
        boards.Add(board); SaveIndex(); BoardList.SelectedItem = board;
    }
    private void NewBoard_Click(object sender, RoutedEventArgs e) => CreateBoard($"未命名白板 {boards.Count + 1}");
    private void BoardList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BoardList.SelectedItem is not BoardInfo selected || selected == currentBoard) return;
        SaveCurrentBoard(); currentBoard = selected; TitleBox.Text = selected.Name; undo.Clear(); redo.Clear(); LoadBoard(selected);
    }
    private void RenameBoard_Click(object sender, RoutedEventArgs e) { TitleBox.Focus(); TitleBox.SelectAll(); }
    private void TitleBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (currentBoard is null || string.IsNullOrWhiteSpace(TitleBox.Text)) return;
        currentBoard.Name = TitleBox.Text.Trim(); BoardList.Items.Refresh(); SaveIndex();
    }
    private void DeleteBoard_Click(object sender, RoutedEventArgs e)
    {
        if (currentBoard is null || boards.Count <= 1) { ShowAppDialog("无法删除", "至少需要保留一个白板。", false); return; }
        var board = currentBoard;
        ShowAppDialog("删除白板", $"确定要删除“{board.Name}”吗？该操作无法撤销。", true, () =>
        {
            currentBoard = null; boards.Remove(board); File.Delete(BoardPath(board)); SaveIndex(); BoardList.SelectedIndex = 0;
        });
    }

    private void SaveCurrentBoard()
    {
        if (currentBoard is null || restoring) return;
        CanvasDocument document = CaptureDocument();
        currentBoard.UpdatedAt = DateTime.Now;
        QueueBoardSave(BoardPath(currentBoard), JsonSerializer.Serialize(document.ToState(), jsonOptions), JsonSerializer.Serialize(boards, jsonOptions));
        RenderGpuDocument(document);
    }

    private void QueueBoardSave(string boardPath, string boardJson, string indexJson)
    {
        pendingBoardPath = boardPath; pendingBoardJson = boardJson; pendingIndexJson = indexJson;
        SaveStatus.Text = "正在保存…";
        saveDebounce?.Cancel();
        saveDebounce?.Dispose();
        saveDebounce = new CancellationTokenSource();
        _ = FlushPendingSaveAsync(saveDebounce.Token);
    }

    private async Task FlushPendingSaveAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(180, token);
            string? boardPath = pendingBoardPath, boardJson = pendingBoardJson, indexJson = pendingIndexJson;
            if (boardPath is null || boardJson is null || indexJson is null) return;
            await saveGate.WaitAsync(token);
            try
            {
                await File.WriteAllTextAsync(boardPath, boardJson, token);
                await File.WriteAllTextAsync(IOPath.Combine(dataDir, "boards.json"), indexJson, token);
            }
            finally { saveGate.Release(); }
            if (!token.IsCancellationRequested) SaveStatus.Text = "已保存";
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!token.IsCancellationRequested) SaveStatus.Text = "保存失败"; }
    }

    private void FlushPendingSaveSynchronously()
    {
        saveDebounce?.Cancel();
        if (pendingBoardPath is null || pendingBoardJson is null || pendingIndexJson is null) return;
        saveGate.Wait();
        try
        {
            File.WriteAllText(pendingBoardPath, pendingBoardJson);
            File.WriteAllText(IOPath.Combine(dataDir, "boards.json"), pendingIndexJson);
        }
        finally { saveGate.Release(); }
    }
    private string CaptureState()
    {
        return JsonSerializer.Serialize(CaptureDocument().ToState(), jsonOptions);
    }
    private CanvasDocument CaptureDocument()
    {
        var document = new CanvasDocument();
        foreach (Stroke stroke in BoardCanvas.Strokes)
        {
            var item = new CanvasStroke { Color = stroke.DrawingAttributes.Color.ToString(), Width = stroke.DrawingAttributes.Width };
            item.Points.AddRange(stroke.StylusPoints.Select(p => new PointData(p.X, p.Y)));
            document.Strokes.Add(item);
        }
        foreach (UIElement child in BoardCanvas.Children)
        {
            var item = new CanvasElement { X = InkCanvas.GetLeft(child), Y = InkCanvas.GetTop(child), Z = Panel.GetZIndex(child) };
            if (child is TextBox text) { item.Type = "Text"; item.Text = text.Text; item.Width = text.Width; item.Height = text.Height; item.FontSize = text.FontSize; item.FontFamily = text.FontFamily.Source; item.Color = ((SolidColorBrush)text.Foreground).Color.ToString(); item.Bold = text.FontWeight == FontWeights.Bold; item.Italic = text.FontStyle == FontStyles.Italic; item.Underline = text.TextDecorations == TextDecorations.Underline; }
            else if (child is Image image && image.Source is BitmapSource bitmap) { item.Type = "Image"; item.Width = image.Width; item.Height = image.Height; item.Image = image.Tag as string ?? BitmapToBase64(bitmap); image.Tag = item.Image; }
            else if (child is Shape shape) { item.Type = child is Rectangle ? "Rectangle" : child is Ellipse ? "Ellipse" : "Arrow"; item.Width = shape.Width; item.Height = shape.Height; item.Color = ((SolidColorBrush)shape.Stroke).Color.ToString(); item.Rotation = GetRotation(shape); if (shape is Line line) { item.X1 = line.X1; item.Y1 = line.Y1; item.X2 = line.X2; item.Y2 = line.Y2; } }
            document.Elements.Add(item);
        }
        return document;
    }
    private void RenderGpuDocument()
    {
        RenderGpuDocument(CaptureDocument());
    }
    private void RenderGpuDocument(CanvasDocument document)
    {
        renderedDocument = document;
        if (gpuCanvasHost?.IsReady != true) return;
        if (gpuCanvas.Render(document, appearance.DarkMode, zoom, CanvasScroll.HorizontalOffset, CanvasScroll.VerticalOffset, viewportDpiScale)) gpuCanvasHost.InvalidateSurface();
        else if (!rebuildingGpuBackend && gpuCanvas.IsDeviceLost && TryRebuildGpuBackend()) return;
        else if (renderOptions.EnableGpuFallback)
        {
            ActivateWpfFallback($"GPU 绘制失败，已显示 WPF 画布 · {gpuCanvas.Status}");
        }
        UpdateGpuDiagnostics();
    }

    private void ScheduleViewportRender()
    {
        if (viewportRenderQueued || renderedDocument is null || gpuCanvasHost?.IsReady != true) return;
        viewportRenderQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            viewportRenderQueued = false;
            if (renderedDocument is not null) RenderGpuDocument(renderedDocument);
        }, DispatcherPriority.Render);
    }

    private void ScheduleInteractiveDocumentRender()
    {
        if (interactiveRenderQueued || gpuCanvasHost?.IsReady != true) return;
        interactiveRenderQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            interactiveRenderQueued = false;
            if (gpuCanvasHost?.IsReady == true) RenderGpuDocument(CaptureDocument());
        }, DispatcherPriority.Render);
    }

    private bool TryRebuildGpuBackend()
    {
        rebuildingGpuBackend = true;
        try
        {
            gpuCanvasHost?.Dispose();
            if (gpuCanvasHost is not null) Viewport.Children.Remove(gpuCanvasHost);
            gpuCanvasHost = null;
            (int surfaceWidth, int surfaceHeight) = GetGpuSurfacePixels();
            if (!gpuCanvas.TryRebuildSurface(surfaceWidth, surfaceHeight)) return false;
            var replacement = CreateGpuHost();
            if (!replacement.TryInitialize(new WindowInteropHelper(this).Handle, surfaceWidth, surfaceHeight, gpuCanvas.SharedHandle))
            {
                replacement.Dispose();
                return false;
            }
            gpuCanvasHost = replacement;
            Viewport.Children.Insert(0, replacement);
            Panel.SetZIndex(replacement, -1);
            BoardCanvas.Opacity = 1;
            if (!gpuCanvas.Render(CaptureDocument(), appearance.DarkMode, zoom, CanvasScroll.HorizontalOffset, CanvasScroll.VerticalOffset, viewportDpiScale)) return false;
            replacement.InvalidateSurface();
            HintText.Text = "GPU 设备已重建";
            return true;
        }
        finally
        {
            rebuildingGpuBackend = false;
            if (gpuCanvasHost?.IsReady != true && renderOptions.EnableGpuFallback)
                ActivateWpfFallback($"GPU 设备重建失败，已回退到 WPF 画布 · {gpuCanvas.Status}");
        }
    }

    private (int Width, int Height) GetGpuSurfacePixels()
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(Viewport);
        viewportDpiScale = dpi.DpiScaleX;
        return (Math.Max(1, (int)Math.Ceiling(Viewport.ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(Viewport.ActualHeight * dpi.DpiScaleY)));
    }

    private D3DImageCanvasHost CreateGpuHost()
    {
        return new D3DImageCanvasHost
        {
            Width = Viewport.ActualWidth,
            Height = Viewport.ActualHeight,
            Visibility = Visibility.Hidden,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
    }

    private void ActivateWpfFallback(string message)
    {
        BoardCanvas.Opacity = 1;
        gpuCanvasHost?.Dispose();
        if (gpuCanvasHost is not null) Viewport.Children.Remove(gpuCanvasHost);
        gpuCanvasHost = null;
        HintText.Text = message;
    }

    private void LoadBoard(BoardInfo board)
    {
        restoring = true; BoardCanvas.Strokes.Clear(); BoardCanvas.Children.Clear();
        try { if (File.Exists(BoardPath(board))) RestoreState(File.ReadAllText(BoardPath(board))); }
        catch { ShowAppDialog("读取失败", "白板文件无法读取，已为你打开空白画布。", false); }
        restoring = false;
        RenderGpuDocument();
    }
    private void RestoreState(string json)
    {
        var state = JsonSerializer.Deserialize<BoardState>(json) ?? new();
        var document = CanvasDocument.FromState(state);
        BoardCanvas.Strokes.Clear(); BoardCanvas.Children.Clear();
        foreach (var data in document.Strokes)
        {
            var points = new StylusPointCollection(data.Points.Select(p => new StylusPoint(p.X, p.Y)));
            if (points.Count > 0) BoardCanvas.Strokes.Add(new Stroke(points) { DrawingAttributes = new DrawingAttributes { Color = (Color)ColorConverter.ConvertFromString(data.Color), Width = data.Width, Height = data.Width, FitToCurve = true } });
        }
        foreach (var item in document.Elements)
        {
            UIElement element = item.Type switch
            {
                "Text" => CreateTextBox(item.ToLegacy()),
                "Image" => new Image { Source = Base64ToBitmap(item.Image ?? ""), Width = item.Width, Height = item.Height, Stretch = Stretch.Fill, Tag = item.Image },
                _ => CreateShape(item.Type == "Diamond" ? "Rectangle" : item.Type, item.Width, item.Height, (Color)ColorConverter.ConvertFromString(item.Color ?? "#4841BD"))
            };
            if (element is Shape shape) { shape.RenderTransformOrigin = new Point(.5, .5); shape.RenderTransform = new RotateTransform(item.Rotation); if (shape is Line line && item.X2 is not null) { line.X1 = item.X1 ?? 0; line.Y1 = item.Y1 ?? 0; line.X2 = item.X2.Value; line.Y2 = item.Y2 ?? 0; } }
            InkCanvas.SetLeft(element, item.X); InkCanvas.SetTop(element, item.Y); Panel.SetZIndex(element, item.Z); BoardCanvas.Children.Add(element);
        }
    }
    private void RecordUndo() { if (restoring) return; undo.Push(CaptureState()); redo.Clear(); SaveStatus.Text = "正在保存…"; }
    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();
    private void Undo() { if (undo.Count == 0) return; redo.Push(CaptureState()); restoring = true; RestoreState(undo.Pop()); restoring = false; SaveCurrentBoard(); }
    private void Redo() { if (redo.Count == 0) return; undo.Push(CaptureState()); restoring = true; RestoreState(redo.Pop()); restoring = false; SaveCurrentBoard(); }

    private void Tool_Click(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse(tag, out ToolKind next)) SetTool(next); }
    private void SetTool(ToolKind next)
    {
        tool = next; BoardCanvas.EditingMode = next == ToolKind.Select ? InkCanvasEditingMode.Select : InkCanvasEditingMode.None;
        BoardCanvas.Cursor = next == ToolKind.Text ? Cursors.IBeam : Cursors.Arrow;
        foreach (var button in FindVisualChildren<System.Windows.Controls.Primitives.ToggleButton>(RootGrid).Where(b => b.Tag is string)) button.IsChecked = string.Equals(button.Tag?.ToString(), next.ToString(), StringComparison.Ordinal);
        HintText.Text = next switch { ToolKind.Select => "左键选择图形 · 中键拖动画布 · 右键打开菜单", ToolKind.Pen => "画笔 · 按住鼠标自由绘制", ToolKind.Text => "文字工具 · 点击画布添加文本", _ => $"{next} · 在画布上拖动绘制" };
    }
    private void BoardCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (tool == ToolKind.Pen)
        {
            RecordUndo();
            Point point = e.GetPosition(BoardCanvas);
            activeStroke = new Stroke(new StylusPointCollection([new StylusPoint(point.X, point.Y)]))
            {
                DrawingAttributes = BoardCanvas.DefaultDrawingAttributes.Clone()
            };
            activeStroke.DrawingAttributes.FitToCurve = false;
            BoardCanvas.Strokes.Add(activeStroke);
            pendingStrokePoints.Clear();
            gpuStrokeLastPoint = activeStroke.StylusPoints[0];
            SubscribeStrokeFrame();
            BoardCanvas.CaptureMouse();
            e.Handled = true;
            return;
        }
        if (tool == ToolKind.Select) return;
        startPoint = e.GetPosition(BoardCanvas); RecordUndo();
        if (tool == ToolKind.Text)
        {
            var text = CreateTextBox(new ElementData { Text = "输入文字", Width = 220, Height = 70, FontSize = GetFontSize(), FontFamily = GetFontFamily(), Color = activeColor.ToString() });
            AddElement(text, startPoint.X, startPoint.Y); text.Focus(); text.SelectAll(); SetTool(ToolKind.Select); SaveCurrentBoard(); e.Handled = true; return;
        }
        drawingShape = CreateShape(tool.ToString(), 1, 1, activeColor); AddElement(drawingShape, startPoint.X, startPoint.Y); BoardCanvas.CaptureMouse(); e.Handled = true;
    }
    private void BoardCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (activeStroke is not null)
        {
            QueueStrokePoint(e.GetPosition(BoardCanvas));
            FlushStrokePoints();
            activeStroke = null;
            gpuStrokeLastPoint = null;
            pendingStrokePoints.Clear();
            UnsubscribeStrokeFrame();
            BoardCanvas.ReleaseMouseCapture();
            redo.Clear();
            SaveCurrentBoard();
            e.Handled = true;
            return;
        }
        if (drawingShape is null) return;
        Shape completedShape = drawingShape; drawingShape = null; BoardCanvas.ReleaseMouseCapture();
        BoardCanvas.Select(new StrokeCollection(), new List<UIElement> { completedShape }); SaveCurrentBoard(); SetTool(ToolKind.Select); e.Handled = true;
    }
    private Shape CreateShape(string type, double width, double height, Color color)
    {
        Shape shape = type switch { "Ellipse" => new Ellipse(), "Arrow" => new Line { X1 = 0, Y1 = 0, X2 = Math.Max(1, width), Y2 = Math.Max(1, height), StrokeEndLineCap = PenLineCap.Triangle }, _ => new Rectangle { Tag = type, RadiusX = 8, RadiusY = 8 } };
        shape.Width = Math.Max(1, width); shape.Height = Math.Max(1, height); shape.Stroke = new SolidColorBrush(color); shape.StrokeThickness = 3; shape.Fill = new SolidColorBrush(Color.FromArgb(18, color.R, color.G, color.B));
        shape.RenderTransformOrigin = new Point(.5, .5); return shape;
    }
    private TextBox CreateTextBox(ElementData item)
    {
        var text = new TextBox { Text = item.Text ?? "", Width = item.Width, Height = item.Height, FontSize = item.FontSize <= 0 ? 18 : item.FontSize, FontFamily = new FontFamily(item.FontFamily ?? "Microsoft YaHei UI"), Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(item.Color ?? "#1C2033")), FontWeight = item.Bold ? FontWeights.Bold : FontWeights.Normal, FontStyle = item.Italic ? FontStyles.Italic : FontStyles.Normal, TextDecorations = item.Underline ? TextDecorations.Underline : null, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(1), BorderBrush = Brushes.Transparent, Background = Brushes.Transparent, Padding = new Thickness(5) };
        text.GotFocus += (_, _) => { text.BorderBrush = new SolidColorBrush(Color.FromRgb(102, 87, 232)); };
        text.LostFocus += (_, _) => { text.BorderBrush = Brushes.Transparent; SaveCurrentBoard(); }; return text;
    }
    private void AddElement(UIElement element, double x, double y) { InkCanvas.SetLeft(element, x); InkCanvas.SetTop(element, y); BoardCanvas.Children.Add(element); }
    private void BoardCanvas_StrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
    {
        if (restoring) return;
        redo.Clear();
        SaveCurrentBoard();
    }

    private void SubscribeStrokeFrame()
    {
        if (strokeFrameSubscribed) return;
        CompositionTarget.Rendering += StrokeFrame_Rendering;
        strokeFrameSubscribed = true;
    }

    private void UnsubscribeStrokeFrame()
    {
        if (!strokeFrameSubscribed) return;
        CompositionTarget.Rendering -= StrokeFrame_Rendering;
        strokeFrameSubscribed = false;
    }

    private void StrokeFrame_Rendering(object? sender, EventArgs e) => FlushStrokePoints();

    private void QueueStrokePoint(Point point)
    {
        if (activeStroke is null) return;
        StylusPoint last = pendingStrokePoints.Count > 0 ? pendingStrokePoints[^1] : activeStroke.StylusPoints[^1];
        double dx = point.X - last.X, dy = point.Y - last.Y;
        if (dx * dx + dy * dy >= .36) pendingStrokePoints.Add(new StylusPoint(point.X, point.Y));
    }

    private void FlushStrokePoints()
    {
        if (activeStroke is null || pendingStrokePoints.Count == 0) return;
        if (gpuCanvasHost?.IsReady == true && gpuStrokeLastPoint is StylusPoint previous)
        {
            var segments = new List<PointData>(pendingStrokePoints.Count + 1) { new(previous.X, previous.Y) };
            segments.AddRange(pendingStrokePoints.Select(point => new PointData(point.X, point.Y)));
            if (gpuCanvas.RenderStrokeSegments(segments, activeStroke.DrawingAttributes.Color.ToString(),
                activeStroke.DrawingAttributes.Width, zoom, CanvasScroll.HorizontalOffset, CanvasScroll.VerticalOffset, viewportDpiScale))
                gpuCanvasHost.InvalidateSurface();
            gpuStrokeLastPoint = pendingStrokePoints[^1];
        }
        activeStroke.StylusPoints.Add(new StylusPointCollection(pendingStrokePoints));
        pendingStrokePoints.Clear();
    }

    private void BoardCanvas_SelectionChanged(object sender, EventArgs e)
    {
        UpdateSelectionVisuals();
        Dispatcher.BeginInvoke(HideNativeSelectionAdorner, System.Windows.Threading.DispatcherPriority.Loaded);
    }
    private void UpdateSelectionVisuals()
    {
        Rect bounds = BoardCanvas.GetSelectionBounds();
        if (bounds.IsEmpty || tool != ToolKind.Select)
        {
            SelectedElementOutline.Visibility = Visibility.Collapsed;
        }
        else
        {
            Shape? shape = BoardCanvas.GetSelectedElements().OfType<Shape>().SingleOrDefault();
            bool onlyShape = shape is not null && BoardCanvas.GetSelectedElements().Count == 1 && BoardCanvas.GetSelectedStrokes().Count == 0;
            if (onlyShape)
            {
                Point center = shape!.TranslatePoint(new Point(shape.ActualWidth / 2, shape.ActualHeight / 2), Viewport);
                SelectedElementOutline.Width = shape.ActualWidth * zoom;
                SelectedElementOutline.Height = shape.ActualHeight * zoom;
                SelectedElementOutline.Margin = new Thickness(center.X - SelectedElementOutline.Width / 2, center.Y - SelectedElementOutline.Height / 2, 0, 0);
                SelectedElementOutline.RenderTransformOrigin = new Point(.5, .5);
                SelectedElementOutline.RenderTransform = new RotateTransform(GetRotation(shape));
            }
            else
            {
                Point topLeft = BoardCanvas.TranslatePoint(bounds.TopLeft, Viewport);
                Point bottomRight = BoardCanvas.TranslatePoint(bounds.BottomRight, Viewport);
                SelectedElementOutline.Margin = new Thickness(topLeft.X, topLeft.Y, 0, 0);
                SelectedElementOutline.Width = Math.Max(0, bottomRight.X - topLeft.X);
                SelectedElementOutline.Height = Math.Max(0, bottomRight.Y - topLeft.Y);
                SelectedElementOutline.RenderTransform = Transform.Identity;
            }
            SelectedElementOutline.Visibility = Visibility.Visible;
        }
        UpdateRotationHandle();
    }
    private void HideNativeSelectionAdorner()
    {
        foreach (var adorner in FindVisualChildren<System.Windows.Documents.Adorner>(BoardCanvas))
        {
            if (!adorner.GetType().Name.Contains("Selection", StringComparison.OrdinalIgnoreCase)) continue;
            adorner.Opacity = 0;
            adorner.IsHitTestVisible = false;
        }
    }
    private void UpdateRotationHandle()
    {
        Shape? shape = isRotating ? rotatingShape : BoardCanvas.GetSelectedElements().OfType<Shape>().Take(2).Count() == 1 ? BoardCanvas.GetSelectedElements().OfType<Shape>().First() : null;
        if (shape is null || tool != ToolKind.Select) { RotationOverlay.Visibility = Visibility.Collapsed; return; }
        Point topCenter = shape.TranslatePoint(new Point(shape.ActualWidth / 2, 0), Viewport);
        Point center = shape.TranslatePoint(new Point(shape.ActualWidth / 2, shape.ActualHeight / 2), Viewport);
        Vector direction = topCenter - center; if (direction.Length < 1) direction = new Vector(0, -1); direction.Normalize();
        Point handle = topCenter + direction * 30;
        RotationGuide.X1 = topCenter.X; RotationGuide.Y1 = topCenter.Y; RotationGuide.X2 = handle.X; RotationGuide.Y2 = handle.Y;
        Canvas.SetLeft(RotationHandle, handle.X - RotationHandle.Width / 2); Canvas.SetTop(RotationHandle, handle.Y - RotationHandle.Height / 2);
        RotationOverlay.Visibility = Visibility.Visible;
    }
    private void RotationHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        rotatingShape = BoardCanvas.GetSelectedElements().OfType<Shape>().SingleOrDefault(); if (rotatingShape is null) return;
        RecordUndo(); isRotating = true; rotationCenter = rotatingShape.TranslatePoint(new Point(rotatingShape.ActualWidth / 2, rotatingShape.ActualHeight / 2), Viewport);
        Point mouse = e.GetPosition(Viewport); rotationStartAngle = Math.Atan2(mouse.Y - rotationCenter.Y, mouse.X - rotationCenter.X) * 180 / Math.PI;
        shapeStartRotation = GetRotation(rotatingShape); Viewport.CaptureMouse(); e.Handled = true;
    }

    private void Viewport_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle) { isPanning = true; panStart = e.GetPosition(Viewport); panHorizontal = CanvasScroll.HorizontalOffset; panVertical = CanvasScroll.VerticalOffset; coordinateTransform.SetViewport(zoom, -panHorizontal, -panVertical); Viewport.CaptureMouse(); e.Handled = true; }
        else if (tool == ToolKind.Select && e.ChangedButton == MouseButton.Left && TrySelectElement(e.OriginalSource as DependencyObject))
        {
            if (!IsInsideSelectedElements(e.GetPosition(BoardCanvas))) { e.Handled = true; return; }
            RecordUndo(); isDraggingElements = true; elementDragStart = e.GetPosition(BoardCanvas); draggedElements.Clear();
            foreach (UIElement element in BoardCanvas.GetSelectedElements()) draggedElements.Add((element, GetCanvasCoordinate(element, true), GetCanvasCoordinate(element, false)));
            Viewport.CaptureMouse(); e.Handled = true;
        }
        else if (tool == ToolKind.Select && e.ChangedButton == MouseButton.Left && ReferenceEquals(e.OriginalSource, BoardCanvas))
        {
            isMarqueeSelecting = true; selectionStart = e.GetPosition(Viewport); SelectionMarquee.Width = SelectionMarquee.Height = 0;
            SelectionMarquee.Margin = new Thickness(selectionStart.X, selectionStart.Y, 0, 0); SelectionMarquee.Visibility = Visibility.Visible;
            Viewport.CaptureMouse(); e.Handled = true;
        }
    }
    private bool TrySelectElement(DependencyObject? source)
    {
        UIElement? element = source as UIElement;
        while (element is not null && !ReferenceEquals(element, BoardCanvas))
        {
            if (BoardCanvas.Children.Contains(element))
            {
                if (!BoardCanvas.GetSelectedElements().Contains(element)) BoardCanvas.Select(new StrokeCollection(), new List<UIElement> { element });
                return true;
            }
            element = VisualTreeHelper.GetParent(element) as UIElement;
        }
        if (ReferenceEquals(source, BoardCanvas)) BoardCanvas.Select(new StrokeCollection(), []);
        return false;
    }
    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        UpdateGridHighlight(e.GetPosition(BoardCanvas));
        if (activeStroke is not null && e.LeftButton == MouseButtonState.Pressed)
        {
            QueueStrokePoint(e.GetPosition(BoardCanvas));
            e.Handled = true;
            return;
        }
        if (isRotating && rotatingShape is not null)
        {
            Point mouse = e.GetPosition(Viewport); double angle = Math.Atan2(mouse.Y - rotationCenter.Y, mouse.X - rotationCenter.X) * 180 / Math.PI;
            rotatingShape.RenderTransformOrigin = new Point(.5, .5); rotatingShape.RenderTransform = new RotateTransform(shapeStartRotation + angle - rotationStartAngle); UpdateSelectionVisuals(); ScheduleInteractiveDocumentRender(); e.Handled = true; return;
        }
        if (isPanning) { Point p = e.GetPosition(Viewport); CanvasScroll.ScrollToHorizontalOffset(panHorizontal - (p.X - panStart.X)); CanvasScroll.ScrollToVerticalOffset(panVertical - (p.Y - panStart.Y)); coordinateTransform.SetViewport(zoom, -CanvasScroll.HorizontalOffset, -CanvasScroll.VerticalOffset); ScheduleViewportRender(); e.Handled = true; return; }
        if (isDraggingElements && e.LeftButton == MouseButtonState.Pressed)
        {
            Point current = e.GetPosition(BoardCanvas); Vector delta = current - elementDragStart;
            foreach (var item in draggedElements) { InkCanvas.SetLeft(item.Element, item.Left + delta.X); InkCanvas.SetTop(item.Element, item.Top + delta.Y); }
            UpdateSelectionVisuals(); ScheduleInteractiveDocumentRender(); e.Handled = true; return;
        }
        if (isMarqueeSelecting)
        {
            Point p = e.GetPosition(Viewport); double x = Math.Min(selectionStart.X, p.X), y = Math.Min(selectionStart.Y, p.Y);
            SelectionMarquee.Margin = new Thickness(x, y, 0, 0); SelectionMarquee.Width = Math.Abs(p.X - selectionStart.X); SelectionMarquee.Height = Math.Abs(p.Y - selectionStart.Y); e.Handled = true; return;
        }
        if (drawingShape is not null && e.LeftButton == MouseButtonState.Pressed)
        {
            Point p = e.GetPosition(BoardCanvas); double x = Math.Min(startPoint.X, p.X), y = Math.Min(startPoint.Y, p.Y), w = Math.Max(2, Math.Abs(p.X - startPoint.X)), h = Math.Max(2, Math.Abs(p.Y - startPoint.Y));
            InkCanvas.SetLeft(drawingShape, x); InkCanvas.SetTop(drawingShape, y); drawingShape.Width = w; drawingShape.Height = h;
            if (drawingShape is Line line) { line.X1 = startPoint.X - x; line.Y1 = startPoint.Y - y; line.X2 = p.X - x; line.Y2 = p.Y - y; }
            ScheduleInteractiveDocumentRender();
        }
    }
    private void Viewport_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (isRotating) { isRotating = false; rotatingShape = null; Viewport.ReleaseMouseCapture(); UpdateRotationHandle(); SaveCurrentBoard(); e.Handled = true; return; }
        if (isPanning) { isPanning = false; Viewport.ReleaseMouseCapture(); e.Handled = true; return; }
        if (isDraggingElements)
        {
            isDraggingElements = false; draggedElements.Clear(); Viewport.ReleaseMouseCapture(); UpdateSelectionVisuals(); SaveCurrentBoard(); e.Handled = true; return;
        }
        if (isMarqueeSelecting)
        {
            Point end = e.GetPosition(Viewport); Point a = Viewport.TranslatePoint(selectionStart, BoardCanvas); Point b = Viewport.TranslatePoint(end, BoardCanvas);
            var area = new Rect(a, b); var strokes = new StrokeCollection(BoardCanvas.Strokes.Where(s => area.IntersectsWith(s.GetBounds())));
            var elements = BoardCanvas.Children.Cast<UIElement>().Where(item => { Point origin = item.TranslatePoint(new Point(), BoardCanvas); return area.IntersectsWith(new Rect(origin, item.RenderSize)); }).ToList();
            BoardCanvas.Select(strokes, elements); isMarqueeSelecting = false; SelectionMarquee.Visibility = Visibility.Collapsed; Viewport.ReleaseMouseCapture(); e.Handled = true;
        }
    }
    private bool IsInsideSelectedElements(Point point)
    {
        if (BoardCanvas.GetSelectedElements().Count == 0) return false;
        Rect bounds = BoardCanvas.GetSelectionBounds();
        if (bounds.IsEmpty) return false;
        double inset = Math.Min(8 / zoom, Math.Min(bounds.Width, bounds.Height) * .2);
        Rect interior = bounds;
        if (bounds.Width > inset * 2 && bounds.Height > inset * 2) interior.Inflate(-inset, -inset);
        return interior.Contains(point);
    }
    private static double GetCanvasCoordinate(UIElement element, bool horizontal)
    {
        double value = horizontal ? InkCanvas.GetLeft(element) : InkCanvas.GetTop(element);
        return double.IsNaN(value) ? 0 : value;
    }
    private void Viewport_MouseLeave(object sender, MouseEventArgs e) => GridHighlight.Visibility = Visibility.Collapsed;
    private void UpdateGridHighlight(Point point)
    {
        bool inside = point.X is >= 0 and <= 5000 && point.Y is >= 0 and <= 3500;
        GridHighlight.Visibility = inside ? Visibility.Visible : Visibility.Collapsed;
        if (!inside) return;
        GridHighlightMask.Center = new Point(point.X / 5000, point.Y / 3500);
        GridHighlightMask.GradientOrigin = GridHighlightMask.Center;
    }
    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        Point viewportPoint = e.GetPosition(Viewport);
        Point canvasPoint = e.GetPosition(BoardCanvas);
        SetZoom(zoom * (e.Delta > 0 ? 1.1 : 0.9));
        CanvasScroll.UpdateLayout();
        CanvasScroll.ScrollToHorizontalOffset(canvasPoint.X * zoom - viewportPoint.X);
        CanvasScroll.ScrollToVerticalOffset(canvasPoint.Y * zoom - viewportPoint.Y);
        ScheduleViewportRender();
        UpdateGridHighlight(canvasPoint);
        UpdateRotationHandle();
        e.Handled = true;
    }
    private void SetZoom(double value)
    {
        zoom = Math.Clamp(value, .25, 3);
        coordinateTransform.SetViewport(zoom, -CanvasScroll.HorizontalOffset, -CanvasScroll.VerticalOffset);
        if (BoardCanvas.Parent is FrameworkElement host) host.LayoutTransform = new ScaleTransform(zoom, zoom);
        ZoomText.Text = FocusZoomText.Text = $"{zoom:P0}";
        ScheduleViewportRender();
        UpdateGpuDiagnostics();
    }
    private void UpdateGpuDiagnostics()
    {
        if (GpuDiagnosticsOverlay is null) return;
        GpuDiagnosticsOverlay.Visibility = renderOptions.ShowDiagnostics ? Visibility.Visible : Visibility.Collapsed;
        if (!renderOptions.ShowDiagnostics) return;
        GpuDiagnosticsText.Text = $"Backend: {renderOptions.Backend}\nD3D11: {gpuDevice.IsAvailable}\nD3DImage: {gpuCanvasHost?.IsReady == true}\nZoom: {zoom:P0}\nStrokes: {BoardCanvas.Strokes.Count}\nElements: {BoardCanvas.Children.Count}";
    }
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(zoom * 1.15);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(zoom / 1.15);
    private void Fit_Click(object sender, RoutedEventArgs e) { SetZoom(.5); CanvasScroll.ScrollToHorizontalOffset(900); CanvasScroll.ScrollToVerticalOffset(550); }

    private void ImportImage_Click(object sender, RoutedEventArgs e) => ShowFileDialog(FileDialogMode.OpenImage);
    private void AddImage(BitmapSource source) { RecordUndo(); double ratio = Math.Min(1, 500d / Math.Max(source.PixelWidth, source.PixelHeight)); string encoded = BitmapToBase64(source); AddElement(new Image { Source = source, Width = source.PixelWidth * ratio, Height = source.PixelHeight * ratio, Stretch = Stretch.Fill, Tag = encoded }, CanvasScroll.HorizontalOffset / zoom + 350, CanvasScroll.VerticalOffset / zoom + 220); SaveCurrentBoard(); }
    private void ExportPng_Click(object sender, RoutedEventArgs e)
    {
        if (GetContentBounds().IsEmpty) { ShowAppDialog("无法导出", "白板还是空的，请先添加一些内容。", false); return; }
        ShowFileDialog(FileDialogMode.SavePng);
    }
    private void ExportPng(string path)
    {
        Rect bounds = GetContentBounds(); bounds.Inflate(40, 40);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height), 96, 96, PixelFormats.Pbgra32); var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) { dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, bounds.Width, bounds.Height)); dc.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y)); dc.DrawRectangle(new VisualBrush(BoardCanvas), null, new Rect(0, 0, BoardCanvas.ActualWidth, BoardCanvas.ActualHeight)); } bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream); SaveStatus.Text = "PNG 已导出";
    }
    private Rect GetContentBounds() { Rect result = Rect.Empty; foreach (var s in BoardCanvas.Strokes) result.Union(s.GetBounds()); foreach (UIElement c in BoardCanvas.Children) result.Union(new Rect(InkCanvas.GetLeft(c), InkCanvas.GetTop(c), c.RenderSize.Width, c.RenderSize.Height)); return result; }
    private static string BitmapToBase64(BitmapSource bitmap) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var ms = new MemoryStream(); encoder.Save(ms); return Convert.ToBase64String(ms.ToArray()); }
    private static BitmapImage Base64ToBitmap(string data) { var image = new BitmapImage(); using var ms = new MemoryStream(Convert.FromBase64String(data)); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = ms; image.EndInit(); image.Freeze(); return image; }

    private IEnumerable<TextBox> SelectedTexts() => BoardCanvas.GetSelectedElements().OfType<TextBox>().Concat(BoardCanvas.Children.OfType<TextBox>().Where(t => t.IsKeyboardFocusWithin)).Distinct();
    private double GetFontSize() => double.TryParse((FontSizeBox.SelectedItem as ComboBoxItem)?.Content?.ToString(), out double value) ? value : 18;
    private string GetFontFamily() => (FontFamilyBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Microsoft YaHei UI";
    private void FontFamilyBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!IsLoaded) return; RecordUndo(); foreach (var t in SelectedTexts()) t.FontFamily = new FontFamily(GetFontFamily()); SaveCurrentBoard(); }
    private void FontSizeBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!IsLoaded) return; RecordUndo(); foreach (var t in SelectedTexts()) t.FontSize = GetFontSize(); SaveCurrentBoard(); }
    private void Bold_Click(object sender, RoutedEventArgs e) { RecordUndo(); foreach (var t in SelectedTexts()) t.FontWeight = t.FontWeight == FontWeights.Bold ? FontWeights.Normal : FontWeights.Bold; SaveCurrentBoard(); }
    private void Italic_Click(object sender, RoutedEventArgs e) { RecordUndo(); foreach (var t in SelectedTexts()) t.FontStyle = t.FontStyle == FontStyles.Italic ? FontStyles.Normal : FontStyles.Italic; SaveCurrentBoard(); }
    private void Underline_Click(object sender, RoutedEventArgs e) { RecordUndo(); foreach (var t in SelectedTexts()) t.TextDecorations = t.TextDecorations == TextDecorations.Underline ? null : TextDecorations.Underline; SaveCurrentBoard(); }
    private void Color_Click(object sender, RoutedEventArgs e)
    {
        OpenColorPalette(activeColor, false);
    }
    private void ThemePalette_Click(object sender, RoutedEventArgs e)
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
        OpenColorPalette(ParseColor(appearance.AccentColor, Color.FromRgb(102, 87, 232)), true);
    }
    private void OpenColorPalette(Color color, bool forTheme)
    {
        isThemeColorPicking = forTheme; ColorPaletteOverlay.Visibility = Visibility.Visible; colorPreviewStarted = false;
        RgbToHsv(color, out double hue, out colorSaturation, out colorValue); HueSlider.Value = hue; UpdateColorFieldPointer(); PreviewPickerColor();
    }
    private void CloseColorPalette_Click(object sender, RoutedEventArgs e) => CloseColorPalette();
    private void ColorPaletteOverlay_MouseDown(object sender, MouseButtonEventArgs e) => CloseColorPalette();
    private void CloseColorPalette()
    {
        ColorPaletteOverlay.Visibility = Visibility.Collapsed;
        if (isThemeColorPicking) SaveAppearanceSettings(); else if (colorPreviewStarted) SaveCurrentBoard();
        isThemeColorPicking = false;
    }
    private void ColorPaletteCard_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
    private void HueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (HueColorStop is null) return; HueColorStop.Color = HsvToRgb(e.NewValue, 1, 1); if (ColorPaletteOverlay?.Visibility == Visibility.Visible) PreviewPickerColor();
    }
    private void ColorField_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { isColorPicking = true; ColorField.CaptureMouse(); UpdatePickerFromMouse(e.GetPosition(ColorField)); e.Handled = true; }
    private void ColorField_MouseMove(object sender, MouseEventArgs e) { if (isColorPicking && e.LeftButton == MouseButtonState.Pressed) UpdatePickerFromMouse(e.GetPosition(ColorField)); }
    private void ColorField_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) { if (!isColorPicking) return; UpdatePickerFromMouse(e.GetPosition(ColorField)); isColorPicking = false; ColorField.ReleaseMouseCapture(); e.Handled = true; }
    private void UpdatePickerFromMouse(Point point)
    {
        colorSaturation = Math.Clamp(point.X / Math.Max(1, ColorField.ActualWidth), 0, 1); colorValue = 1 - Math.Clamp(point.Y / Math.Max(1, ColorField.ActualHeight), 0, 1);
        UpdateColorFieldPointer(); PreviewPickerColor();
    }
    private void UpdateColorFieldPointer()
    {
        if (ColorField is null) return; ColorFieldPointer.Margin = new Thickness(colorSaturation * Math.Max(0, ColorField.ActualWidth) - 8, (1 - colorValue) * Math.Max(0, ColorField.ActualHeight) - 8, 0, 0);
    }
    private void PreviewPickerColor()
    {
        Color pickedColor = HsvToRgb(HueSlider.Value, colorSaturation, colorValue);
        ColorPreview.Background = new SolidColorBrush(pickedColor); ColorHexText.Text = pickedColor.ToString();
        if (isThemeColorPicking)
        {
            appearance.AccentColor = pickedColor.ToString(); ApplyAppearance(false); colorPreviewStarted = true; return;
        }
        if (!colorPreviewStarted) { RecordUndo(); colorPreviewStarted = true; }
        activeColor = pickedColor; BoardCanvas.DefaultDrawingAttributes.Color = activeColor;
        foreach (var text in SelectedTexts()) text.Foreground = new SolidColorBrush(activeColor);
        foreach (var shape in BoardCanvas.GetSelectedElements().OfType<Shape>()) { shape.Stroke = new SolidColorBrush(activeColor); shape.Fill = new SolidColorBrush(Color.FromArgb(18, activeColor.R, activeColor.G, activeColor.B)); }
        foreach (var stroke in BoardCanvas.GetSelectedStrokes()) stroke.DrawingAttributes.Color = activeColor;
        SetBrush("ActiveColorBrush", activeColor);
    }
    private static Color HsvToRgb(double hue, double saturation, double value)
    {
        hue = ((hue % 360) + 360) % 360; double c = value * saturation, x = c * (1 - Math.Abs(hue / 60 % 2 - 1)), m = value - c; (double r, double g, double b) = hue switch { < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x), < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x) }; return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
    private static void RgbToHsv(Color color, out double hue, out double saturation, out double value)
    {
        double r = color.R / 255d, g = color.G / 255d, b = color.B / 255d, max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), delta = max - min; hue = delta == 0 ? 0 : max == r ? 60 * (((g - b) / delta) % 6) : max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4); if (hue < 0) hue += 360; saturation = max == 0 ? 0 : delta / max; value = max;
    }
    private static double GetRotation(UIElement element) => element.RenderTransform is RotateTransform rotation ? rotation.Angle : 0;
    private void RotateSelection_Click(object sender, RoutedEventArgs e)
    {
        var selected = BoardCanvas.GetSelectedElements().OfType<Shape>().ToList(); if (selected.Count == 0) return; RecordUndo();
        foreach (var shape in selected) { shape.RenderTransformOrigin = new Point(.5, .5); shape.RenderTransform = new RotateTransform((GetRotation(shape) + 15) % 360); }
        SaveCurrentBoard();
    }
    private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
    {
        sidebarCollapsed = !sidebarCollapsed; SideGlass.Visibility = sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
        SideColumn.Width = sidebarCollapsed ? new GridLength(0) : new GridLength(230); ExpandSidebarButton.Visibility = sidebarCollapsed ? Visibility.Visible : Visibility.Collapsed;
    }
    private void DeleteSelection_Click(object sender, RoutedEventArgs e) { if (BoardCanvas.GetSelectedElements().Count == 0 && BoardCanvas.GetSelectedStrokes().Count == 0) return; RecordUndo(); foreach (var item in BoardCanvas.GetSelectedElements().ToList()) BoardCanvas.Children.Remove(item); BoardCanvas.Strokes.Remove(BoardCanvas.GetSelectedStrokes()); SaveCurrentBoard(); }
    private void CopySelection_Click(object sender, RoutedEventArgs e)
    {
        if (BoardCanvas.GetSelectedElements().OfType<Image>().FirstOrDefault()?.Source is BitmapSource image) Clipboard.SetImage(image);
        else if (SelectedTexts().FirstOrDefault() is TextBox text) Clipboard.SetText(text.SelectedText.Length > 0 ? text.SelectedText : text.Text);
    }
    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        if (Clipboard.ContainsImage()) AddImage(Clipboard.GetImage());
        else if (Clipboard.ContainsText()) { RecordUndo(); Point p = Mouse.GetPosition(BoardCanvas); var text = CreateTextBox(new ElementData { Text = Clipboard.GetText(), Width = 240, Height = 80, FontSize = GetFontSize(), FontFamily = GetFontFamily(), Color = activeColor.ToString() }); AddElement(text, Math.Clamp(p.X, 0, 4760), Math.Clamp(p.Y, 0, 3420)); SaveCurrentBoard(); }
    }
    private void SelectAll_Click(object sender, RoutedEventArgs e) => BoardCanvas.Select(BoardCanvas.Strokes, BoardCanvas.Children.Cast<UIElement>().ToList());
    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsOverlay.Visibility = Visibility.Visible;
    private void CloseSettings_Click(object sender, RoutedEventArgs e) => SettingsOverlay.Visibility = Visibility.Collapsed;
    private void SettingsOverlay_MouseDown(object sender, MouseButtonEventArgs e) => SettingsOverlay.Visibility = Visibility.Collapsed;
    private void SettingsCard_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
    private void ThemeMode_Checked(object sender, RoutedEventArgs e) { if (!appearanceReady) return; appearance.DarkMode = DarkModeRadio.IsChecked == true; ApplyAppearance(); }
    private void GlassOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!appearanceReady) return; appearance.GlassOpacity = e.NewValue; ApplyAppearance(); }
    private void GlassBlurSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!appearanceReady) return; appearance.GlassBlur = e.NewValue; ApplyAppearance(); }
    private void GpuCanvasCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!appearanceReady || GpuCanvasCheckBox is null) return;
        renderOptions.Backend = GpuCanvasCheckBox.IsChecked == true ? CanvasRenderBackend.Direct2DComposition : CanvasRenderBackend.WpfFallback;
        SaveAppearanceSettings();
    }
    private void GpuFallbackCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!appearanceReady || GpuFallbackCheckBox is null) return;
        renderOptions.EnableGpuFallback = GpuFallbackCheckBox.IsChecked == true;
        SaveAppearanceSettings();
    }
    private void GpuDiagnosticsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!appearanceReady || GpuDiagnosticsCheckBox is null) return;
        renderOptions.ShowDiagnostics = GpuDiagnosticsCheckBox.IsChecked == true;
        UpdateGpuDiagnostics();
        SaveAppearanceSettings();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { ToggleMaximize(); return; }
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void ToggleMaximize() { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□"; }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void FocusDrag_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
    private void FocusMode_Click(object sender, RoutedEventArgs e) => SetFocusMode(!focusMode);
    private void SetFocusMode(bool enabled)
    {
        focusMode = enabled;
        SideGlass.Visibility = enabled || sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
        SideColumn.Width = enabled || sidebarCollapsed ? new GridLength(0) : new GridLength(230);
        HeaderGlass.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        ToolbarGlass.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        StatusGlass.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        MainArea.RowDefinitions[0].Height = enabled ? new GridLength(0) : new GridLength(58);
        MainArea.RowDefinitions[1].Height = enabled ? new GridLength(0) : new GridLength(68);
        MainArea.RowDefinitions[3].Height = enabled ? new GridLength(0) : new GridLength(32);
        FocusToolbar.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        PinButton.Foreground = FocusPinButton.Foreground = Topmost ? (Brush)FindResource("Primary") : (Brush)FindResource("TextBrush");
        PinButton.ToolTip = FocusPinButton.ToolTip = Topmost ? "取消置顶" : "置顶";
    }

    private void ShowAppDialog(string title, string message, bool canCancel, Action? confirm = null)
    {
        dialogConfirmAction = confirm; DialogTitle.Text = title; DialogMessage.Text = message; DialogCancelButton.Visibility = canCancel ? Visibility.Visible : Visibility.Collapsed;
        FileDialog.Visibility = Visibility.Collapsed; MessageDialog.Visibility = Visibility.Visible; DialogOverlay.Visibility = Visibility.Visible;
    }
    private void DialogCancel_Click(object sender, RoutedEventArgs e) { dialogConfirmAction = null; DialogOverlay.Visibility = Visibility.Collapsed; }
    private void DialogConfirm_Click(object sender, RoutedEventArgs e) { var action = dialogConfirmAction; dialogConfirmAction = null; DialogOverlay.Visibility = Visibility.Collapsed; action?.Invoke(); }

    private void ShowFileDialog(FileDialogMode mode)
    {
        fileDialogMode = mode; if (!Directory.Exists(fileDialogDirectory)) fileDialogDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        FileDialogTitle.Text = mode == FileDialogMode.OpenImage ? "导入图片" : "导出 PNG"; FileAcceptButton.Content = mode == FileDialogMode.OpenImage ? "选择图片" : "保存";
        FileNameBox.Text = mode == FileDialogMode.SavePng ? SanitizeFileName(currentBoard?.Name ?? "白板") + ".png" : "";
        MessageDialog.Visibility = Visibility.Collapsed; FileDialog.Visibility = Visibility.Visible; DialogOverlay.Visibility = Visibility.Visible; RefreshFileItems();
    }
    private void RefreshFileItems()
    {
        try
        {
            FilePathBox.Text = fileDialogDirectory;
            var items = new List<FileBrowserItem>();
            foreach (string directory in Directory.EnumerateDirectories(fileDialogDirectory).OrderBy(IOPath.GetFileName)) items.Add(new FileBrowserItem { Name = IOPath.GetFileName(directory), FullPath = directory, IsDirectory = true, Icon = "▰", Description = "文件夹" });
            if (fileDialogMode == FileDialogMode.OpenImage) foreach (string file in Directory.EnumerateFiles(fileDialogDirectory).Where(IsImageFile).OrderBy(IOPath.GetFileName)) items.Add(new FileBrowserItem { Name = IOPath.GetFileName(file), FullPath = file, Icon = "▧", Description = new FileInfo(file).Length < 1048576 ? $"{new FileInfo(file).Length / 1024d:0} KB" : $"{new FileInfo(file).Length / 1048576d:0.0} MB" });
            FileItemsList.ItemsSource = items;
        }
        catch { ShowAppDialog("无法访问", "该位置无法访问，请选择其他文件夹。", false); }
    }
    private static bool IsImageFile(string path) => new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif" }.Contains(IOPath.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    private static string SanitizeFileName(string value) => string.Concat(value.Select(c => IOPath.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    private void FileItemsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileItemsList.SelectedItem is not FileBrowserItem item) return;
        if (item.IsDirectory) { fileDialogDirectory = item.FullPath; RefreshFileItems(); } else { FileNameBox.Text = item.Name; FileDialogAccept_Click(sender, e); }
    }
    private void FileUp_Click(object sender, RoutedEventArgs e) { var parent = Directory.GetParent(fileDialogDirectory); if (parent is not null) { fileDialogDirectory = parent.FullName; RefreshFileItems(); } }
    private void FileGo_Click(object sender, RoutedEventArgs e) { if (Directory.Exists(FilePathBox.Text)) { fileDialogDirectory = FilePathBox.Text; RefreshFileItems(); } else ShowAppDialog("路径不存在", "输入的文件夹路径不存在。", false); }
    private void FilePathBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) FileGo_Click(sender, e); }
    private void FileDialogCancel_Click(object sender, RoutedEventArgs e) => DialogOverlay.Visibility = Visibility.Collapsed;
    private void FileDialogAccept_Click(object sender, RoutedEventArgs e)
    {
        string path = IOPath.Combine(fileDialogDirectory, FileNameBox.Text.Trim());
        if (fileDialogMode == FileDialogMode.OpenImage)
        {
            if (!File.Exists(path) || !IsImageFile(path)) { ShowAppDialog("请选择图片", "请选择 PNG、JPG、BMP 或 GIF 图片文件。", false); return; }
            DialogOverlay.Visibility = Visibility.Collapsed; AddImage(new BitmapImage(new Uri(path)));
        }
        else
        {
            if (string.IsNullOrWhiteSpace(FileNameBox.Text)) { ShowAppDialog("文件名为空", "请输入导出文件名。", false); return; }
            if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) path += ".png";
            if (File.Exists(path)) { string exportPath = path; ShowAppDialog("覆盖文件", "该文件已经存在，确定覆盖吗？", true, () => ExportPng(exportPath)); return; }
            DialogOverlay.Visibility = Visibility.Collapsed; ExportPng(path);
        }
    }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11) { SetFocusMode(!focusMode); e.Handled = true; return; }
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control) { Paste_Click(sender, e); e.Handled = true; return; }
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { CopySelection_Click(sender, e); e.Handled = true; return; }
        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control) { SelectAll_Click(sender, e); e.Handled = true; return; }
        if (Keyboard.FocusedElement is TextBox) return;
        if (e.Key == Key.Delete) DeleteSelection_Click(sender, e); else if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) Undo(); else if (e.Key == Key.Y && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) Redo(); else if (e.Key == Key.V) SetTool(ToolKind.Select); else if (e.Key == Key.P) SetTool(ToolKind.Pen); else if (e.Key == Key.T) SetTool(ToolKind.Text); else if (e.Key == Key.R) SetTool(ToolKind.Rectangle); else if (e.Key == Key.O) SetTool(ToolKind.Ellipse); else if (e.Key == Key.A) SetTool(ToolKind.Arrow);
    }
    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { DependencyObject child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match; foreach (var nested in FindVisualChildren<T>(child)) yield return nested; }
    }
    private void Window_Closing(object? sender, CancelEventArgs e) { SaveCurrentBoard(); FlushPendingSaveSynchronously(); }
}

public enum ToolKind { Select, Pen, Text, Rectangle, Ellipse, Arrow }
public sealed class BoardInfo { public string Id { get; set; } = ""; public string Name { get; set; } = ""; public DateTime UpdatedAt { get; set; } public override string ToString() => Name; }
public sealed class AppearanceSettings
{
    public bool DarkMode { get; set; }
    public string AccentColor { get; set; } = "#6657E8";
    public double GlassOpacity { get; set; } = 90;
    public double GlassBlur { get; set; } = 18;
    public CanvasRenderOptions RenderOptions { get; set; } = new();
}
public enum FileDialogMode { OpenImage, SavePng }
public sealed class FileBrowserItem { public string Name { get; set; } = ""; public string FullPath { get; set; } = ""; public bool IsDirectory { get; set; } public string Icon { get; set; } = ""; public string Description { get; set; } = ""; }