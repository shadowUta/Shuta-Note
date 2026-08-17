using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.Direct3D9;

namespace ShutaNote;

/// <summary>WPF D3DImage 承载层，持有可由 Direct3D 共享的 GPU 表面。</summary>
public sealed class D3DImageCanvasHost : FrameworkElement, IDisposable
{
    private readonly D3DImage image = new();
    private IDirect3D9Ex? direct3D;
    private IDirect3DDevice9Ex? device;
    private IDirect3DTexture9? texture;
    private IDirect3DSurface9? surface;
    private int pixelWidth;
    private int pixelHeight;

    public bool IsReady => surface is not null;

    public bool TryInitialize(IntPtr windowHandle, int width, int height)
    {
        DisposeResources();
        try
        {
            direct3D = Vortice.Direct3D9.D3D9.Direct3DCreate9Ex();
            PresentParameters parameters = new()
            {
                Windowed = true,
                SwapEffect = SwapEffect.Discard,
                DeviceWindowHandle = windowHandle,
                PresentationInterval = PresentInterval.Immediate,
                BackBufferFormat = Format.Unknown,
                BackBufferWidth = 1,
                BackBufferHeight = 1
            };
            device = direct3D!.CreateDeviceEx(0, DeviceType.Hardware, windowHandle,
                CreateFlags.HardwareVertexProcessing | CreateFlags.Multithreaded | CreateFlags.FpuPreserve,
                [parameters], []);
            Resize(width, height);
            return IsReady;
        }
        catch
        {
            DisposeResources();
            return false;
        }
    }

    public void Resize(int width, int height)
    {
        if (device is null) return;
        width = Math.Max(1, width); height = Math.Max(1, height);
        if (pixelWidth == width && pixelHeight == height && surface is not null) return;
        image.Lock();
        try
        {
            image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
            surface?.Dispose(); texture?.Dispose();
            texture = device.CreateTexture((uint)width, (uint)height, 1, Usage.RenderTarget, Format.A8R8G8B8, Pool.Default);
            surface = texture.GetSurfaceLevel(0);
            image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, surface.NativePointer, true);
            pixelWidth = width; pixelHeight = height;
            image.AddDirtyRect(new Int32Rect(0, 0, width, height));
        }
        finally { image.Unlock(); }
        InvalidateVisual();
    }

    public void InvalidateSurface()
    {
        if (surface is null) return;
        image.Lock();
        image.AddDirtyRect(new Int32Rect(0, 0, pixelWidth, pixelHeight));
        image.Unlock();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        drawingContext.DrawImage(image, new System.Windows.Rect(0, 0, ActualWidth, ActualHeight));
    }

    public void Dispose()
    {
        DisposeResources();
        GC.SuppressFinalize(this);
    }

    private void DisposeResources()
    {
        if (image.IsFrontBufferAvailable)
        {
            image.Lock();
            image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
            image.Unlock();
        }
        surface?.Dispose(); texture?.Dispose(); device?.Dispose(); direct3D?.Dispose();
        surface = null; texture = null; device = null; direct3D = null;
        pixelWidth = pixelHeight = 0;
    }
}
