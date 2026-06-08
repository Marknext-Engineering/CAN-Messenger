// =====================================================================
//  PeakInterop — PCANBasic.dll P/Invoke 정의 (CAN-FD)
//  개요   : 상태코드/채널핸들/메시지플래그 상수, TPCANMsgFD 구조체,
//           CAN_InitializeFD/ReadFD/WriteFD/SetValue/GetErrorText 선언.
//  추후 개선:
//           - 비트레이트 SetValue, 버스부하/통계 등 추가 파라미터 미선언(필요시 확장)
//           - 32-bit/64-bit DLL 혼용 환경 가드 미구현(현재 x64 전제)
// =====================================================================
using System.Runtime.InteropServices;
using System.Text;

namespace CanMessager.Hal.Peak;

/// <summary>PCANBasic.dll P/Invoke 정의 (CAN-FD 부분).</summary>
internal static class PeakInterop
{
    private const string Dll = "PCANBasic.dll";

    // 상태 코드
    public const uint PCAN_ERROR_OK = 0x00000;
    public const uint PCAN_ERROR_QRCVEMPTY = 0x00020;
    public const uint PCAN_ERROR_QXMTFULL = 0x00080;

    // 채널 핸들
    public const ushort PCAN_NONEBUS = 0x00;
    public const ushort PCAN_USBBUS1 = 0x51;

    // 파라미터
    public const byte PCAN_RECEIVE_EVENT = 0x03;

    // 메시지 타입 플래그
    public const byte PCAN_MESSAGE_STANDARD = 0x00;
    public const byte PCAN_MESSAGE_RTR = 0x01;
    public const byte PCAN_MESSAGE_EXTENDED = 0x02;
    public const byte PCAN_MESSAGE_FD = 0x04;
    public const byte PCAN_MESSAGE_BRS = 0x08;
    public const byte PCAN_MESSAGE_ESI = 0x10;
    public const byte PCAN_MESSAGE_ECHO = 0x20;
    public const byte PCAN_MESSAGE_ERRFRAME = 0x40;
    public const byte PCAN_MESSAGE_STATUS = 0x80;

    [StructLayout(LayoutKind.Sequential)]
    public struct TPCANMsgFD
    {
        public uint ID;
        public byte MSGTYPE;  // 플래그 조합
        public byte DLC;      // DLC 코드(0..15)
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public byte[] DATA;
    }

    [DllImport(Dll, EntryPoint = "CAN_InitializeFD", CharSet = CharSet.Ansi)]
    public static extern uint CAN_InitializeFD(ushort channel, string bitrateFD);

    [DllImport(Dll, EntryPoint = "CAN_Uninitialize")]
    public static extern uint CAN_Uninitialize(ushort channel);

    [DllImport(Dll, EntryPoint = "CAN_ReadFD")]
    public static extern uint CAN_ReadFD(ushort channel, out TPCANMsgFD msg, out ulong timestamp);

    [DllImport(Dll, EntryPoint = "CAN_WriteFD")]
    public static extern uint CAN_WriteFD(ushort channel, ref TPCANMsgFD msg);

    [DllImport(Dll, EntryPoint = "CAN_SetValue")]
    public static extern uint CAN_SetValue(ushort channel, byte parameter, ref uint value, uint length);

    [DllImport(Dll, EntryPoint = "CAN_GetErrorText", CharSet = CharSet.Ansi)]
    public static extern uint CAN_GetErrorText(uint error, ushort language, StringBuilder buffer);

    public static string ErrorText(uint status)
    {
        var sb = new StringBuilder(256);
        if (CAN_GetErrorText(status, 0, sb) == PCAN_ERROR_OK)
            return $"0x{status:X} ({sb.ToString().Trim()})";
        return $"0x{status:X}";
    }
}
