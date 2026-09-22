using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Devices.Display;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.Graphics.Gdi;

namespace Nami.Interop;

/// <summary>What the monitor under a window can do, as reported by DXGI / the display config API.</summary>
public sealed record DisplayInfo(
    string DeviceName,
    bool IsHdr,
    float MaxLuminance,
    float MaxFullFrameLuminance,
    float MinLuminance,
    uint BitsPerColor,
    /// <summary>SDR white level in nits (Windows default 80 nits = 1.0 slider); 0 if unknown.</summary>
    float SdrWhiteNits)
{
    public override string ToString()
        => $"{DeviceName}: hdr={IsHdr} peak={MaxLuminance:F0} fullframe={MaxFullFrameLuminance:F0} min={MinLuminance:F3} bits={BitsPerColor} sdrWhite={SdrWhiteNits:F0}";

    /// <summary>Query the monitor that currently contains most of <paramref name="hwnd"/>.</summary>
    public static unsafe DisplayInfo? Query(nint hwnd)
    {
        HMONITOR monitor = PInvoke.MonitorFromWindow((HWND)hwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        if (monitor == 0) return null;

        var info = new MONITORINFOEXW();
        info.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);
        if (!PInvoke.GetMonitorInfo(monitor, (MONITORINFO*)&info)) return null;
        string gdiName = info.szDevice.ToString();

        DisplayInfo? result = null;

        void* factoryPtr;
        Guid factoryIid = IDXGIFactory1.IID_Guid;
        if (PInvoke.CreateDXGIFactory1(&factoryIid, &factoryPtr).Failed) return null;
        var factory = (IDXGIFactory1*)factoryPtr;
        try
        {
            for (uint a = 0; ; a++)
            {
                IDXGIAdapter1* adapter;
                if (factory->EnumAdapters1(a, &adapter).Failed) break;
                try
                {
                    for (uint o = 0; ; o++)
                    {
                        IDXGIOutput* output;
                        if (adapter->EnumOutputs(o, &output).Failed) break;
                        try
                        {
                            void* out6Ptr;
                            Guid out6Iid = IDXGIOutput6.IID_Guid;
                            if (output->QueryInterface(&out6Iid, &out6Ptr).Failed) continue;
                            var out6 = (IDXGIOutput6*)out6Ptr;
                            try
                            {
                                DXGI_OUTPUT_DESC1 desc;
                                try { desc = out6->GetDesc1(); }
                                catch (Exception) { continue; }
                                if (desc.Monitor != monitor) continue;

                                bool hdr = desc.ColorSpace == DXGI_COLOR_SPACE_TYPE.DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020;
                                result = new DisplayInfo(gdiName, hdr, desc.MaxLuminance, desc.MaxFullFrameLuminance,
                                    desc.MinLuminance, desc.BitsPerColor, QuerySdrWhiteNits(gdiName));
                                return result;
                            }
                            finally { out6->Release(); }
                        }
                        finally { output->Release(); }
                    }
                }
                finally { adapter->Release(); }
            }
        }
        finally { factory->Release(); }
        return result;
    }

    /// <summary>Windows' "SDR content brightness" for the given GDI device (\\.\DISPLAYn), in nits.</summary>
    private static unsafe float QuerySdrWhiteNits(string gdiDeviceName)
    {
        uint pathCount, modeCount;
        if (PInvoke.GetDisplayConfigBufferSizes(QUERY_DISPLAY_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS, out pathCount, out modeCount) != WIN32_ERROR.NO_ERROR)
            return 0;
        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
        fixed (DISPLAYCONFIG_PATH_INFO* pp = paths)
        fixed (DISPLAYCONFIG_MODE_INFO* pm = modes)
        {
            if (PInvoke.QueryDisplayConfig(QUERY_DISPLAY_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS, &pathCount, pp, &modeCount, pm, null) != WIN32_ERROR.NO_ERROR)
                return 0;
        }

        for (int i = 0; i < pathCount; i++)
        {
            var src = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
            src.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
            src.header.size = (uint)sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME);
            src.header.adapterId = paths[i].sourceInfo.adapterId;
            src.header.id = paths[i].sourceInfo.id;
            if (PInvoke.DisplayConfigGetDeviceInfo(&src.header) != 0) continue;
            if (!string.Equals(src.viewGdiDeviceName.ToString(), gdiDeviceName, StringComparison.OrdinalIgnoreCase)) continue;

            var white = new DISPLAYCONFIG_SDR_WHITE_LEVEL();
            white.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL;
            white.header.size = (uint)sizeof(DISPLAYCONFIG_SDR_WHITE_LEVEL);
            white.header.adapterId = paths[i].targetInfo.adapterId;
            white.header.id = paths[i].targetInfo.id;
            if (PInvoke.DisplayConfigGetDeviceInfo(&white.header) != 0) continue;
            // SDRWhiteLevel is in units of 1/1000 of the 80-nit reference.
            return white.SDRWhiteLevel / 1000f * 80f;
        }
        return 0;
    }
}
