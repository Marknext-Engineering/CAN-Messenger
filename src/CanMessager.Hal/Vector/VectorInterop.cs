// =====================================================================
//  VectorInterop — vxlapi64.dll P/Invoke 정의 (CAN-FD)
//  개요   : 상태코드/버스타입/이벤트태그/메시지플래그 상수,
//           XLcanFdConf / XLcanTxEvent / XLcanRxEvent(Pack=8) 구조체,
//           xlOpenPort/xlCanFdSetConfiguration/xlActivateChannel/xlCanTransmitEx/xlCanReceive 선언.
//  추후 개선:
//           - 구조체 오프셋은 VN1640A에서 검증 — 타 vxlapi 버전 변화 시 재확인 필요
//           - 에러/칩상태(XL_CAN_EV_TAG_*) 상세 처리 미구현
// =====================================================================
using System.Runtime.InteropServices;

namespace CanMessager.Hal.Vector;

/// <summary>Vector XL Driver Library(vxlapi64.dll) CAN-FD P/Invoke 정의.</summary>
internal static class VectorInterop
{
    private const string Dll = "vxlapi64.dll";

    // 상태 코드
    public const int XL_SUCCESS = 0;
    public const int XL_ERR_QUEUE_IS_EMPTY = 10;
    public const int XL_ERR_QUEUE_IS_FULL = 11;
    public const int XL_ERR_TX_NOT_POSSIBLE = 12;

    public const int XL_INVALID_PORTHANDLE = -1;

    // 버스/인터페이스
    public const uint XL_BUS_TYPE_CAN = 0x00000001;
    public const uint XL_INTERFACE_VERSION_V4 = 4; // CAN-FD
    public const uint XL_ACTIVATE_RESET_CLOCK = 8;

    // 이벤트 태그
    public const ushort XL_CAN_EV_TAG_RX_OK = 0x0400;
    public const ushort XL_CAN_EV_TAG_TX_OK = 0x0404;
    public const ushort XL_CAN_EV_TAG_TX_MSG = 0x0440;

    // TX 메시지 플래그
    public const uint XL_CAN_TXMSG_FLAG_EDL = 0x0001; // FD 포맷
    public const uint XL_CAN_TXMSG_FLAG_BRS = 0x0002; // Bit Rate Switch
    public const uint XL_CAN_TXMSG_FLAG_RTR = 0x0010;

    // RX 메시지 플래그
    public const uint XL_CAN_RXMSG_FLAG_EDL = 0x0001;
    public const uint XL_CAN_RXMSG_FLAG_BRS = 0x0002;

    // CAN ID 플래그
    public const uint XL_CAN_EXT_MSG_ID = 0x80000000;

    public const int XL_CAN_MAX_DATA_LEN = 64;

    /// <summary>xlCanFdSetConfiguration 용 FD 비트 타이밍 (80 MHz 클럭 기준).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct XLcanFdConf
    {
        public uint arbitrationBitRate;
        public uint sjwAbr;
        public uint tseg1Abr;
        public uint tseg2Abr;
        public uint dataBitRate;
        public uint sjwDbr;
        public uint tseg1Dbr;
        public uint tseg2Dbr;
        public byte reserved;
        public byte options;
        public byte reserved1_0;
        public byte reserved1_1;
        public uint reserved2;
    }

    /// <summary>송신 이벤트 (헤더 + XL_CAN_TX_MSG 평탄화).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct XLcanTxEvent
    {
        public ushort tag;            // XL_CAN_EV_TAG_TX_MSG
        public ushort transId;
        public byte channelIndex;
        public byte reserved0;
        public byte reserved1;
        public byte reserved2;
        // --- XL_CAN_TX_MSG ---
        public uint canId;
        public uint msgFlags;
        public byte dlc;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 7)] public byte[] reservedTx;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = XL_CAN_MAX_DATA_LEN)] public byte[] data;
    }

    /// <summary>수신 이벤트 (헤더 + XL_CAN_EV_RX_MSG 평탄화).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct XLcanRxEvent
    {
        public uint size;
        public ushort tag;
        public ushort channelIndex;
        public uint userHandle;
        public ushort flagsChip;
        public ushort reserved0;
        public ulong reserved1;
        public ulong timeStamp;
        // --- XL_CAN_EV_RX_MSG ---
        public uint canId;
        public uint msgFlags;
        public uint crc;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 12)] public byte[] msgReserved1;
        public ushort totalBitCount;
        public byte dlc;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)] public byte[] msgReserved2;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = XL_CAN_MAX_DATA_LEN)] public byte[] data;
    }

    [DllImport(Dll)] public static extern int xlOpenDriver();
    [DllImport(Dll)] public static extern int xlCloseDriver();

    [DllImport(Dll, CharSet = CharSet.Ansi)]
    public static extern int xlOpenPort(ref int portHandle, string userName, ulong accessMask,
        ref ulong permissionMask, uint rxQueueSize, uint xlInterfaceVersion, uint busType);

    [DllImport(Dll)]
    public static extern int xlCanFdSetConfiguration(int portHandle, ulong accessMask, ref XLcanFdConf canFdConf);

    [DllImport(Dll)]
    public static extern int xlActivateChannel(int portHandle, ulong accessMask, uint busType, uint flags);

    [DllImport(Dll)]
    public static extern int xlDeactivateChannel(int portHandle, ulong accessMask);

    [DllImport(Dll)]
    public static extern int xlClosePort(int portHandle);

    [DllImport(Dll)]
    public static extern int xlCanTransmitEx(int portHandle, ulong accessMask, uint msgCount,
        ref uint msgCountSent, [In] XLcanTxEvent[] events);

    [DllImport(Dll)]
    public static extern int xlCanReceive(int portHandle, ref XLcanRxEvent ev);

    [DllImport(Dll)]
    public static extern int xlSetNotification(int portHandle, ref nint handle, int queueLevel);

    [DllImport(Dll, CharSet = CharSet.Ansi)]
    public static extern nint xlGetErrorString(int err);

    public static string ErrorText(int status)
    {
        try
        {
            var p = xlGetErrorString(status);
            string? s = p != nint.Zero ? Marshal.PtrToStringAnsi(p) : null;
            return $"{status} ({s})";
        }
        catch { return status.ToString(); }
    }
}
