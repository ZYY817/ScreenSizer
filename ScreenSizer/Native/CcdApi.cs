using System.Runtime.InteropServices;

namespace ScreenSizer.Native;

/// <summary>
/// Windows CCD（Connecting and Configuring Displays）API。
/// 用于非 NVIDIA 显卡：把缩放模式设为 DISPLAYCONFIG_SCALING_CENTERED，
/// 配合驱动已支持的分辨率实现「居中、四边黑边」的效果。
/// </summary>
internal static class CcdApi
{
    public const int ERROR_SUCCESS = 0;

    // QueryDisplayConfig flags
    public const uint QDC_ALL_PATHS = 0x10;
    public const uint QDC_ONLY_ACTIVE_PATHS = 0x02;
    public const uint QDC_DATABASE_CURRENT = 0x04;

    // SetDisplayConfig flags
    public const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20;
    public const uint SDC_VALIDATE = 0x40;
    public const uint SDC_APPLY = 0x80;
    public const uint SDC_SAVE_TO_DATABASE = 0x200;
    public const uint SDC_ALLOW_CHANGES = 0x400;

    public const uint DISPLAYCONFIG_PATH_ACTIVE = 0x00000001;

    public enum DISPLAYCONFIG_MODE_INFO_TYPE : uint
    {
        SOURCE = 1,
        TARGET = 2,
        DESKTOP_IMAGE = 3,
    }

    public enum DISPLAYCONFIG_DEVICE_INFO_TYPE : uint
    {
        GET_SOURCE_NAME = 1,
        GET_TARGET_NAME = 2,
        GET_TARGET_PREFERRED_MODE = 3,
        GET_ADAPTER_NAME = 4,
        SET_TARGET_PERSISTENCE = 5,
        GET_TARGET_BASE_TYPE = 6,
    }

    public enum DISPLAYCONFIG_SCALING : uint
    {
        IDENTITY = 1,
        CENTERED = 2,
        STRETCHED = 3,
        ASPECTRATIOCENTEREDMAX = 4,
        CUSTOM = 5,
        PREFERRED = 128,
    }

    public enum DISPLAYCONFIG_ROTATION : uint
    {
        IDENTITY = 0,
        ROTATE90 = 1,
        ROTATE180 = 2,
        ROTATE270 = 3,
    }

    public enum DISPLAYCONFIG_SCANLINE_ORDERING : uint
    {
        UNSPECIFIED = 0,
        PROGRESSIVE = 1,
        INTERLACED = 2,
        INTERLACED_LOWERFIELDFIRST = 3,
    }

    public enum DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY : uint
    {
        OTHER = 0xFFFFFFFF,
        HD15 = 0,
        SVIDEO = 1,
        COMPOSITE_VIDEO = 2,
        COMPONENT_VIDEO = 3,
        DVI = 4,
        HDMI = 5,
        LVDS = 6,
        D_JPN = 8,
        SDI = 9,
        DISPLAYPORT_EXTERNAL = 10,
        DISPLAYPORT_EMBEDDED = 11,
        UDI_EXTERNAL = 12,
        UDI_EMBEDDED = 13,
        SDTVDONGLE = 14,
        MIRACAST = 15,
        INDIRECT_WIRED = 16,
        INDIRECT_VIRTUAL = 17,
        WIRE = 0xFFFFFFFE,
        INTERNAL = 0x80000000,
    }

    public enum DISPLAYCONFIG_PIXELFORMAT : uint
    {
        _8BPP = 1,
        _16BPP = 2,
        _24BPP = 3,
        _32BPP = 4,
        NONGDI = 5,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINTL
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_2DREGION
    {
        public uint cx;
        public uint cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_VIDEO_SIGNAL_INFO
    {
        public ulong pixelRate;
        public DISPLAYCONFIG_RATIONAL hSyncFreq;
        public DISPLAYCONFIG_RATIONAL vSyncFreq;
        public DISPLAYCONFIG_2DREGION activeSize;
        public DISPLAYCONFIG_2DREGION totalSize;
        public uint videoStandard;
        public DISPLAYCONFIG_SCANLINE_ORDERING scanLineOrdering;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_TARGET_MODE
    {
        public DISPLAYCONFIG_VIDEO_SIGNAL_INFO targetVideoSignalInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_SOURCE_MODE
    {
        public uint width;
        public uint height;
        public DISPLAYCONFIG_PIXELFORMAT pixelFormat;
        public POINTL position;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_DESKTOP_IMAGE_INFO
    {
        public POINTL PathSourceSize;
        public NativeMethods.RECT DesktopImageRegion;
        public NativeMethods.RECT DesktopImageClip;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct DISPLAYCONFIG_MODE_INFO
    {
        [FieldOffset(0)] public DISPLAYCONFIG_MODE_INFO_TYPE infoType;
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public LUID adapterId;
        [FieldOffset(16)] public DISPLAYCONFIG_TARGET_MODE targetMode;
        [FieldOffset(16)] public DISPLAYCONFIG_SOURCE_MODE sourceMode;
        [FieldOffset(16)] public DISPLAYCONFIG_DESKTOP_IMAGE_INFO desktopImage;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY outputTechnology;
        public DISPLAYCONFIG_ROTATION rotation;
        public DISPLAYCONFIG_SCALING scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public DISPLAYCONFIG_SCANLINE_ORDERING scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public DISPLAYCONFIG_DEVICE_INFO_TYPE type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName;
    }

    [DllImport("user32.dll")]
    public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    public static extern int QueryDisplayConfig(uint flags,
        ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        out uint currentTopologyId);

    [DllImport("user32.dll")]
    public static extern int SetDisplayConfig(uint numPathArrayElements,
        [In] DISPLAYCONFIG_PATH_INFO[] pathArray,
        uint numModeInfoArrayElements,
        [In] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        uint flags);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);
}
