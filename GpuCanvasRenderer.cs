using System.Runtime.InteropServices;

namespace ShutaNote;

/// <summary>
/// GPU 画布渲染器的最小生命周期封装。当前负责创建 Direct2D/DirectWrite 工厂，
/// 具体画布纹理与 D3DImage 桥接在此边界内逐步替换，WPF 适配器仍可安全回退。
/// </summary>
public sealed class GpuCanvasRenderer : IDisposable
{
    private IntPtr d2dFactory;
    private IntPtr dwriteFactory;

    public bool IsReady { get; private set; }
    public string Status { get; private set; } = "未初始化";

    public bool TryInitialize()
    {
        DisposeNative();
        int d2dResult = D2D1CreateFactory(FactoryType.SingleThreaded, FactoryOptions.None, FactoryIid, out d2dFactory);
        int writeResult = DWriteCreateFactory(FactoryType.SingleThreaded, DirectWriteFactoryIid, out dwriteFactory);
        if (d2dResult < 0 || writeResult < 0)
        {
            Status = "Direct2D/DirectWrite 工厂初始化失败";
            DisposeNative();
            return false;
        }

        IsReady = true;
        Status = "Direct2D/DirectWrite 工厂已就绪";
        return true;
    }

    public void Dispose()
    {
        DisposeNative();
        GC.SuppressFinalize(this);
    }

    private void DisposeNative()
    {
        Release(d2dFactory);
        Release(dwriteFactory);
        d2dFactory = IntPtr.Zero;
        dwriteFactory = IntPtr.Zero;
        IsReady = false;
    }

    private static void Release(IntPtr value)
    {
        if (value != IntPtr.Zero) Marshal.Release(value);
    }

    private enum FactoryType : uint { SingleThreaded }
    private enum FactoryOptions : uint { None }

    private static readonly Guid FactoryIid = new("06152247-6f50-465a-9245-118bfd3b6007");
    private static readonly Guid DirectWriteFactoryIid = new("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");

    [DllImport("d2d1.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int D2D1CreateFactory(FactoryType factoryType, FactoryOptions options, Guid riid, out IntPtr factory);

    [DllImport("dwrite.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int DWriteCreateFactory(FactoryType factoryType, Guid iid, out IntPtr factory);
}