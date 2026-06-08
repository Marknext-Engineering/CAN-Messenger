// =====================================================================
//  PeakChannelEnumerator — PEAK 채널 스캔(PCAN_ATTACHED_CHANNELS)
//  개요   : PCANBasic으로 연결된 채널 목록(장치명/device_id/물리채널/핸들) 반환.
//  추후 개선:
//           - device_id가 0xFFFFFFFF(미설정)면 시리얼 N/A — 실시리얼은 장치별 추가 조회 필요
//           - 채널 상태(occupied 등) 세분화 표시 여지
// =====================================================================
using System.Runtime.InteropServices;

namespace CanMessager.Hal.Discovery;

/// <summary>
/// PEAK PCAN-Basic 기반 채널 열거자. PCANBasic.dll의 attached-channels 정보를 조회한다.
/// DLL이 없거나 장치가 없으면 빈 목록 + 진단 메시지를 반환한다(예외 없음).
/// </summary>
public sealed class PeakChannelEnumerator : ICanChannelEnumerator
{
    public CanHardwareType Hardware => CanHardwareType.Peak;

    private const int MaxLenHardwareName = 33;
    private const ushort PCAN_NONEBUS = 0x00;
    private const byte PCAN_ATTACHED_CHANNELS_COUNT = 0x2A;
    private const byte PCAN_ATTACHED_CHANNELS = 0x2B;
    private const uint PCAN_CHANNEL_AVAILABLE = 0x01; // 상태 비트: 사용 가능

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct TPCANChannelInformation
    {
        public ushort channel_handle;
        public byte device_type;
        public byte controller_number;
        public uint device_features;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxLenHardwareName)]
        public string device_name;
        public uint device_id;
        public uint channel_condition;
    }

    [DllImport("PCANBasic.dll", EntryPoint = "CAN_GetValue")]
    private static extern uint CAN_GetValue(ushort channel, byte parameter, byte[] buffer, uint bufferLength);

    public IReadOnlyList<CanChannelInfo> Enumerate(out string? diagnostic)
    {
        diagnostic = null;
        try
        {
            var countBuf = new byte[4];
            uint st = CAN_GetValue(PCAN_NONEBUS, PCAN_ATTACHED_CHANNELS_COUNT, countBuf, 4);
            if (st != 0)
            {
                diagnostic = $"PCAN_ATTACHED_CHANNELS_COUNT 실패 (status=0x{st:X}).";
                return Array.Empty<CanChannelInfo>();
            }

            int count = BitConverter.ToInt32(countBuf, 0);
            if (count <= 0)
            {
                diagnostic = "연결된 PEAK 장치가 없습니다.";
                return Array.Empty<CanChannelInfo>();
            }

            int structSize = Marshal.SizeOf<TPCANChannelInformation>();
            var buffer = new byte[structSize * count];
            st = CAN_GetValue(PCAN_NONEBUS, PCAN_ATTACHED_CHANNELS, buffer, (uint)buffer.Length);
            if (st != 0)
            {
                diagnostic = $"PCAN_ATTACHED_CHANNELS 조회 실패 (status=0x{st:X}).";
                return Array.Empty<CanChannelInfo>();
            }

            var result = new List<CanChannelInfo>(count);
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                nint basePtr = handle.AddrOfPinnedObject();
                for (int i = 0; i < count; i++)
                {
                    var info = Marshal.PtrToStructure<TPCANChannelInformation>(basePtr + i * structSize);
                    bool usable = (info.channel_condition & PCAN_CHANNEL_AVAILABLE) != 0;
                    result.Add(new CanChannelInfo
                    {
                        Hardware = CanHardwareType.Peak,
                        ChannelIndex = info.controller_number,
                        DeviceName = string.IsNullOrWhiteSpace(info.device_name) ? "PCAN 장치" : info.device_name.Trim(),
                        SerialNumber = info.device_id is 0 or 0xFFFFFFFF ? "N/A" : $"0x{info.device_id:X8}",
                        PhysicalChannel = info.controller_number + 1,
                        PeakHandle = info.channel_handle,
                        IsAvailable = usable
                    });
                }
            }
            finally { handle.Free(); }

            if (result.Count == 0) diagnostic = "연결된 PEAK 채널이 없습니다.";
            return result;
        }
        catch (DllNotFoundException)
        {
            diagnostic = "PCANBasic.dll을 찾을 수 없습니다. PEAK 드라이버를 설치하세요.";
            return Array.Empty<CanChannelInfo>();
        }
        catch (Exception ex)
        {
            diagnostic = $"PEAK 채널 조회 오류: {ex.Message}";
            return Array.Empty<CanChannelInfo>();
        }
    }
}
