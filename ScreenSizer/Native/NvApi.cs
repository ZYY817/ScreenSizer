using System.Runtime.InteropServices;

namespace ScreenSizer.Native;

/// <summary>
/// NVAPI 绑定层。接口 ID 和结构体布局来自 NVIDIA 官方 nvapi.h（见 reference/nvapi）。
/// x64 专用：加载 nvapi64.dll，通过 nvapi_QueryInterface 取函数指针。
/// </summary>
internal static class NvApi
{
    // ----- 接口 ID（nvapi_interface.h） -----
    private const uint ID_Initialize = 0x0150e828;
    private const uint ID_Unload = 0xd22bdd7e;
    private const uint ID_GetErrorMessage = 0x6c2d048c;
    private const uint ID_EnumPhysicalGPUs = 0xe5ac921f;
    private const uint ID_GetDisplayIdByDisplayName = 0xae457190;
    private const uint ID_GetTiming = 0x175167e9;
    private const uint ID_TryCustomDisplay = 0x1f7db630;
    private const uint ID_SaveCustomDisplay = 0x49882876;
    private const uint ID_RevertCustomDisplayTrial = 0xcbbd40f0;
    private const uint ID_DeleteCustomDisplay = 0x552e5b9b;
    private const uint ID_GetDisplayConfig = 0x11abccf8;
    private const uint ID_SetDisplayConfig = 0x5d8cf8de;

    // ----- 常量（nvapi.h） -----
    public const uint NV_SCALING_DEFAULT = 0;
    public const uint NV_SCALING_GPU_SCALING_TO_CLOSEST = 1;          // 全屏缩放(平衡)
    public const uint NV_SCALING_GPU_SCALING_TO_NATIVE = 2;           // 全屏缩放(GPU)
    public const uint NV_SCALING_GPU_SCANOUT_TO_NATIVE = 3;           // 居中/无缩放(GPU) — 我们要的
    public const uint NV_SCALING_GPU_SCALING_TO_ASPECT_SCANOUT = 5;   // 保持纵横比(GPU)
    public const uint NV_SCALING_GPU_SCANOUT_TO_CLOSEST = 7;          // 居中/无缩放(平衡)
    public const uint NV_SCALING_GPU_INTEGER_ASPECT_SCALING = 8;      // 整数缩放

    private const uint NV_TIMING_OVERRIDE_CURRENT = 0;
    public const uint NV_TIMING_OVERRIDE_AUTO = 1;
    public const uint NV_TIMING_OVERRIDE_CVT = 5;
    public const uint NV_TIMING_OVERRIDE_GTF = 7;

    private const uint NV_DISPLAYCONFIG_DRIVER_RELOAD_ALLOWED = 0x00000004;

    // ----- 结构体 -----

    [StructLayout(LayoutKind.Sequential)]
    public struct NvTimingExt
    {
        public uint Flag;
        public ushort Rr;
        public uint Rrx1k;
        public uint Aspect;
        public ushort Rep;
        public uint Status;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 40)]
        public byte[] Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NvTiming
    {
        public ushort HVisible, HBorder, HFrontPorch, HSyncWidth, HTotal;
        public byte HSyncPol;
        public ushort VVisible, VBorder, VFrontPorch, VSyncWidth, VTotal;
        public byte VSyncPol;
        public ushort Interlaced;
        public uint Pclk;
        public NvTimingExt Etc;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NvTimingInput
    {
        // 注意：新驱动（R580+）实际期望 32 字节，比头文件定义的 24 字节多 8 字节保留字段
        public uint Version;    // MAKE_NVAPI_VERSION = sizeof | (ver<<16)
        public uint Width;
        public uint Height;
        public float Rr;
        public uint Flag;       // NV_TIMING_FLAG 位域打包: isInterlaced:4 | reserved:12 | tvFormat/ceaId/nvPsfId:8 | scaling:8
        public uint Type;       // NV_TIMING_OVERRIDE
        public uint Reserved1;
        public uint Reserved2;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NvCustomDisplay
    {
        public uint Version;
        public uint Width, Height, Depth;
        public uint ColorFormat;
        public float SrcX, SrcY, SrcW, SrcH;
        public float XRatio, YRatio;
        public NvTiming Timing;
        public uint HwModeSetOnly;
    }

    // ----- 委托签名 -----

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvInitializeDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvUnloadDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvGetErrorMessageDelegate(int status, IntPtr szDesc);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvEnumPhysicalGPUsDelegate(IntPtr handles, ref uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvGetDisplayIdByNameDelegate([MarshalAs(UnmanagedType.LPStr)] string displayName, ref uint displayId);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvGetTimingDelegate(uint displayId, ref NvTimingInput timingInput, ref NvTiming timing);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvGetTimingRawDelegate(uint displayId, IntPtr timingInput, IntPtr timing);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvTryCustomDisplayDelegate(ref uint displayIds, uint count, ref NvCustomDisplay custDisp);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvSaveCustomDisplayDelegate(ref uint displayIds, uint count, uint isThisOutputIdOnly, uint isThisMonitorIdOnly);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvRevertCustomDisplayTrialDelegate(ref uint displayIds, uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvDeleteCustomDisplayDelegate(ref uint displayIds, uint count, ref NvCustomDisplay custDisp);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvGetDisplayConfigDelegate(ref uint pathInfoCount, IntPtr pathInfo);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvSetDisplayConfigDelegate(uint pathInfoCount, IntPtr pathInfo, uint flags);

    // ----- 初始化 -----

    private static IntPtr _lib;
    private static IntPtr _queryInterface;
    private static bool _initialized;
    private static readonly Dictionary<uint, Delegate> _delegates = new();

    public static bool IsAvailable
    {
        get
        {
            if (_lib != IntPtr.Zero) return true;
            _lib = NativeMethods.LoadLibrary("nvapi64.dll");
            if (_lib == IntPtr.Zero) return false;
            _queryInterface = NativeMethods.GetProcAddress(_lib, "nvapi_QueryInterface");
            if (_queryInterface == IntPtr.Zero) return false;
            return true;
        }
    }

    private static T Get<T>(uint id) where T : Delegate
    {
        if (!_delegates.TryGetValue(id, out var d))
        {
            var query = Marshal.GetDelegateForFunctionPointer<NvQueryInterface>(_queryInterface);
            var fn = query(id);
            if (fn == IntPtr.Zero)
                throw new NvApiException($"接口 0x{id:x8} 不存在（驱动版本太旧？）");
            d = Marshal.GetDelegateForFunctionPointer<T>(fn);
            _delegates[id] = d;
        }
        return (T)d;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr NvQueryInterface(uint interfaceId);

    public class NvApiException : Exception
    {
        public int Status;
        public NvApiException(string msg) : base(msg) { }
        public NvApiException(int status, string api)
            : base($"{api} 失败: {ErrorText(status)} (code {status})") => Status = status;
    }

    public static string ErrorText(int status)
    {
        try
        {
            var buf = Marshal.AllocHGlobal(64);
            Get<NvGetErrorMessageDelegate>(ID_GetErrorMessage)(status, buf);
            var s = Marshal.PtrToStringAnsi(buf) ?? $"err {status}";
            Marshal.FreeHGlobal(buf);
            return s.Trim();
        }
        catch { return $"err {status}"; }
    }

    public static void Initialize()
    {
        if (_initialized) return;
        if (!IsAvailable) throw new NvApiException("nvapi64.dll 加载失败（非 NVIDIA 显卡或未装驱动）");
        int r = Get<NvInitializeDelegate>(ID_Initialize)();
        if (r != 0) throw new NvApiException(r, "NvAPI_Initialize");
        _initialized = true;
    }

    // ----- 公共 API -----

    public static uint GetDisplayId(string gdiDisplayName)
    {
        uint id = 0;
        int r = Get<NvGetDisplayIdByNameDelegate>(ID_GetDisplayIdByDisplayName)(gdiDisplayName, ref id);
        if (r != 0) throw new NvApiException(r, "GetDisplayIdByDisplayName");
        return id;
    }

    /// <summary>让驱动为指定分辨率+刷新率生成一套时序参数。</summary>
    public static NvTiming GetTiming(uint displayId, uint w, uint h, float refreshHz, uint structVer = 1)
    {
        var input = new NvTimingInput
        {
            Version = (uint)Marshal.SizeOf<NvTimingInput>() | (structVer << 16),
            Width = w,
            Height = h,
            Rr = refreshHz,
            Flag = 0,
            Type = NV_TIMING_OVERRIDE_AUTO,
        };
        var timing = new NvTiming { Etc = new NvTimingExt { Name = new byte[40] } };
        int r = Get<NvGetTimingDelegate>(ID_GetTiming)(displayId, ref input, ref timing);
        if (r != 0)
        {
            // AUTO 不行就试 CVT
            input.Type = NV_TIMING_OVERRIDE_CVT;
            r = Get<NvGetTimingDelegate>(ID_GetTiming)(displayId, ref input, ref timing);
        }
        if (r != 0) throw new NvApiException(r, "GetTiming");
        return timing;
    }

    /// <summary>诊断用：原始缓冲区版本。</summary>
    public static int GetTimingRaw(uint displayId, IntPtr timingInput, IntPtr timing)
        => Get<NvGetTimingRawDelegate>(ID_GetTiming)(displayId, timingInput, timing);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvEnumCustomDisplayDelegate(uint displayId, uint index, IntPtr custDisp);
    private const uint ID_EnumCustomDisplay = 0xa2072d59;

    /// <summary>诊断用：枚举已保存的自定义显示模式（验证 NV_CUSTOM_DISPLAY 版本）。</summary>
    public static int EnumCustomDisplayRaw(uint displayId, uint index, IntPtr custDisp)
        => Get<NvEnumCustomDisplayDelegate>(ID_EnumCustomDisplay)(displayId, index, custDisp);

    public static NvCustomDisplay MakeCustomDisplay(uint w, uint h, NvTiming timing)
    {
        return new NvCustomDisplay
        {
            Version = (uint)Marshal.SizeOf<NvCustomDisplay>() | (1u << 16),
            Width = w,
            Height = h,
            Depth = 32,
            ColorFormat = 0, // NV_FORMAT_UNKNOWN，驱动自选
            SrcX = 0, SrcY = 0, SrcW = 1f, SrcH = 1f,
            XRatio = 1f, YRatio = 1f,
            Timing = timing,
            HwModeSetOnly = 0,
        };
    }

    public static void TryCustomDisplay(uint displayId, ref NvCustomDisplay cust)
    {
        int r = Get<NvTryCustomDisplayDelegate>(ID_TryCustomDisplay)(ref displayId, 1, ref cust);
        if (r != 0) throw new NvApiException(r, "TryCustomDisplay");
    }

    /// <summary>把刚才试用成功的自定义显示模式保存进注册表（之后出现在系统分辨率列表里）。</summary>
    public static void SaveCustomDisplay(uint displayId)
    {
        int r = Get<NvSaveCustomDisplayDelegate>(ID_SaveCustomDisplay)(ref displayId, 1, 0, 1);
        if (r != 0) throw new NvApiException(r, "SaveCustomDisplay");
    }

    public static int RevertCustomDisplayTrial(uint displayId)
        => Get<NvRevertCustomDisplayTrialDelegate>(ID_RevertCustomDisplayTrial)(ref displayId, 1);

    public static void DeleteCustomDisplay(uint displayId, ref NvCustomDisplay cust)
    {
        int r = Get<NvDeleteCustomDisplayDelegate>(ID_DeleteCustomDisplay)(ref displayId, 1, ref cust);
        if (r != 0) throw new NvApiException(r, "DeleteCustomDisplay");
    }

    // ----- DisplayConfig（缩放模式控制）-----
    // 直接操作非托管缓冲区，避开嵌套指针结构体的 marshalling。

    private const int PathInfoSize = 48;      // NV_DISPLAYCONFIG_PATH_INFO_V2 (x64)
    private const int TargetInfoSize = 24;    // NV_DISPLAYCONFIG_PATH_TARGET_INFO_V2
    private const int DetailsSize = 128;      // NV_DISPLAYCONFIG_PATH_ADVANCED_TARGET_INFO_V1
    private const int SrcModeSize = 32;       // NV_DISPLAYCONFIG_SOURCE_MODE_INFO_V1
    private const int MaxTargets = 16;

    // PATH_INFO_V2 字段偏移
    private const int PO_Version = 0, PO_SourceId = 4, PO_TargetCount = 8, PO_TargetInfo = 16, PO_SrcMode = 24, PO_Bitfield = 32, PO_OSAdapter = 40;
    // TARGET_INFO_V2 字段偏移
    private const int TO_DisplayId = 0, TO_Details = 8, TO_TargetId = 16;
    // ADVANCED_TARGET_INFO_V1 字段偏移
    private const int DO_Version = 0, DO_Rotation = 4, DO_Scaling = 8;

    private sealed class DisplayConfig : IDisposable
    {
        public IntPtr Buf;
        public uint Count;
        public int PathBase(int i) => i * PathInfoSize;
        public IntPtr PathPtr(int i) => Buf + PathBase(i);
        public IntPtr TargetsRegion(int i) => Buf + (int)(Count * PathInfoSize + i * MaxTargets * TargetInfoSize);
        public IntPtr DetailsRegion(int i) => Buf + (int)(Count * PathInfoSize + Count * MaxTargets * TargetInfoSize + i * MaxTargets * DetailsSize);
        public IntPtr SrcModeRegion(int i) => Buf + (int)(Count * PathInfoSize + Count * MaxTargets * (TargetInfoSize + DetailsSize) + i * SrcModeSize);

        public void Dispose() { if (Buf != IntPtr.Zero) Marshal.FreeHGlobal(Buf); Buf = IntPtr.Zero; }
    }

    private static DisplayConfig QueryDisplayConfig()
    {
        uint count = 0;
        int r = Get<NvGetDisplayConfigDelegate>(ID_GetDisplayConfig)(ref count, IntPtr.Zero);
        if (r != 0) throw new NvApiException(r, "GetDisplayConfig(count)");
        if (count == 0) throw new NvApiException("GetDisplayConfig 返回 0 个显示路径");

        var cfg = new DisplayConfig
        {
            Count = count,
            Buf = Marshal.AllocHGlobal((int)(count * PathInfoSize
                + count * MaxTargets * TargetInfoSize
                + count * MaxTargets * DetailsSize
                + count * SrcModeSize)),
        };
        unsafe { new Span<byte>(cfg.Buf.ToPointer(), (int)(count * PathInfoSize + count * MaxTargets * (TargetInfoSize + DetailsSize) + count * SrcModeSize)).Clear(); }

        for (int i = 0; i < count; i++)
        {
            var p = cfg.PathPtr(i);
            Marshal.WriteInt32(p + PO_Version, unchecked((int)(PathInfoSize | (2u << 16))));
            Marshal.WriteInt32(p + PO_TargetCount, MaxTargets);
            Marshal.WriteIntPtr(p + PO_TargetInfo, cfg.TargetsRegion(i));
            Marshal.WriteIntPtr(p + PO_SrcMode, cfg.SrcModeRegion(i));
            for (int t = 0; t < MaxTargets; t++)
            {
                var tp = cfg.TargetsRegion(i) + t * TargetInfoSize;
                Marshal.WriteIntPtr(tp + TO_Details, cfg.DetailsRegion(i) + t * DetailsSize);
                Marshal.WriteInt32(cfg.DetailsRegion(i) + t * DetailsSize + DO_Version, unchecked((int)(DetailsSize | (1u << 16))));
            }
        }

        r = Get<NvGetDisplayConfigDelegate>(ID_GetDisplayConfig)(ref count, cfg.Buf);
        if (r != 0) { cfg.Dispose(); throw new NvApiException(r, "GetDisplayConfig"); }
        cfg.Count = count;
        return cfg;
    }

    /// <summary>读取指定显示器当前的缩放模式。</summary>
    public static uint? GetScaling(uint displayId)
    {
        using var cfg = QueryDisplayConfig();
        for (int i = 0; i < cfg.Count; i++)
        {
            int tc = Marshal.ReadInt32(cfg.PathPtr(i) + PO_TargetCount);
            for (int t = 0; t < Math.Min(tc, MaxTargets); t++)
            {
                var tp = cfg.TargetsRegion(i) + t * TargetInfoSize;
                if ((uint)Marshal.ReadInt32(tp + TO_DisplayId) == displayId)
                {
                    var dp = Marshal.ReadIntPtr(tp + TO_Details);
                    return dp == IntPtr.Zero ? null : (uint)Marshal.ReadInt32(dp + DO_Scaling);
                }
            }
        }
        return null;
    }

    /// <summary>设置指定显示器的缩放模式（其余配置原样回写）。</summary>
    public static void SetScaling(uint displayId, uint scaling)
    {
        using var cfg = QueryDisplayConfig();
        bool found = false;
        for (int i = 0; i < cfg.Count; i++)
        {
            int tc = Marshal.ReadInt32(cfg.PathPtr(i) + PO_TargetCount);
            for (int t = 0; t < Math.Min(tc, MaxTargets); t++)
            {
                var tp = cfg.TargetsRegion(i) + t * TargetInfoSize;
                if ((uint)Marshal.ReadInt32(tp + TO_DisplayId) == displayId)
                {
                    var dp = Marshal.ReadIntPtr(tp + TO_Details);
                    if (dp == IntPtr.Zero) throw new NvApiException("目标显示器无高级配置信息");
                    Marshal.WriteInt32(dp + DO_Scaling, (int)scaling);
                    found = true;
                }
            }
        }
        if (!found) throw new NvApiException($"未找到 displayId=0x{displayId:x8} 的显示路径");


        int r = Get<NvSetDisplayConfigDelegate>(ID_SetDisplayConfig)(cfg.Count, cfg.Buf, 0);
        if (r != 0)
            r = Get<NvSetDisplayConfigDelegate>(ID_SetDisplayConfig)(cfg.Count, cfg.Buf, NV_DISPLAYCONFIG_DRIVER_RELOAD_ALLOWED);
        if (r != 0) throw new NvApiException(r, "SetDisplayConfig");
    }

    /// <summary>诊断：dump 所有显示路径/目标/displayId。</summary>
    public static void DumpDisplayConfig()
    {
        using var cfg = QueryDisplayConfig();
        Console.WriteLine($"DisplayConfig: {cfg.Count} 条路径");
        for (int i = 0; i < cfg.Count; i++)
        {
            var p = cfg.PathPtr(i);
            int tc = Marshal.ReadInt32(p + PO_TargetCount);
            int bf = Marshal.ReadInt32(p + PO_Bitfield);
            Console.WriteLine($"  path[{i}] targets={tc} bitfield=0x{bf:x8}");
            for (int t = 0; t < Math.Min(tc, MaxTargets); t++)
            {
                var tp = cfg.TargetsRegion(i) + t * TargetInfoSize;
                uint did = (uint)Marshal.ReadInt32(tp + TO_DisplayId);
                var dp = Marshal.ReadIntPtr(tp + TO_Details);
                uint scaling = dp == IntPtr.Zero ? 0xFFFFFFFF : (uint)Marshal.ReadInt32(dp + DO_Scaling);
                uint dver = dp == IntPtr.Zero ? 0 : (uint)Marshal.ReadInt32(dp + DO_Version);
                Console.WriteLine($"    target[{t}] displayId=0x{did:x8} detailsPtr=0x{dp:x} detailsVer=0x{dver:x8} scaling={scaling}");
            }
        }
    }
}
