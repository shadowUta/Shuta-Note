using System.Runtime.InteropServices;

namespace ShutaNote;

/// <summary>
/// 最小 GPU 能力探测层。后续 Direct2D/D3DImage 承载层只依赖此接口，失败时由调用方回退到 WPF。
/// </summary>
public sealed class GpuDeviceManager : IDisposable
{
    private IntPtr device;
    private IntPtr context;

    public bool IsAvailable { get; private set; }
    public string Status { get; private set; } = "未初始化";

    public bool TryInitialize()
    {
        DisposeNative();
        int result = D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, 0, 0, IntPtr.Zero, 0, 7, out device, out _, out context);
        if (result < 0)
        {
            Status = $"GPU 初始化失败 (0x{result:X8})";
            IsAvailable = false;
            return false;
        }

        Status = "D3D11 硬件设备已就绪";
        IsAvailable = true;
        return true;
    }

    public void Dispose()
    {
        DisposeNative();
        GC.SuppressFinalize(this);
    }

    private void DisposeNative()
    {
        Release(device); Release(context);
        device = IntPtr.Zero; context = IntPtr.Zero; IsAvailable = false;
    }

    private static void Release(IntPtr value)
    {
        if (value != IntPtr.Zero) Marshal.Release(value);
    }

    private enum DriverType : uint { Hardware = 1 }

    [DllImport("d3d11.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int D3D11CreateDevice(
        IntPtr adapter, DriverType driverType, uint software, uint flags,
        IntPtr featureLevels, uint featureLevelCount, uint sdkVersion,
        out IntPtr device, out uint featureLevel, out IntPtr immediateContext);
}