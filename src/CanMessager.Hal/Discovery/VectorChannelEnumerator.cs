// =====================================================================
//  VectorChannelEnumerator — Vector 채널 스캔(시리얼/물리채널/채널마스크)
//  개요   : xlGetDriverConfig 원시 버퍼를 파싱해 채널 목록 반환.
//  핵심   : 오프셋 상수(name@0, mask@+42, serial@+151, STRIDE=227)는
//           VN1640A 실하드웨어로 검증된 값. DumpRawConfig()는 오프셋 재보정용 진단.
//  추후 개선:
//           - vxlapi 버전에 따라 XLchannelConfig 크기(STRIDE)가 달라질 수 있음 → DumpRawConfig로 재확인
//           - hwType로 가상/물리 채널 구분 필터 추가 여지(현재 이름으로만 구분)
// =====================================================================
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using CanMessager.Hal.Vector;

namespace CanMessager.Hal.Discovery;

/// <summary>
/// Vector XL Driver Library(vxlapi64.dll) 기반 채널 열거자.
/// xlOpenDriver → xlGetDriverConfig로 채널 구성을 읽어 장치명/물리채널/시리얼/채널마스크를 보고한다.
/// vxlapi64.dll이 표준 경로에 없으면 Vector 설치 폴더에서 동적 탐색해 로드한다.
///
/// ⚠ 주의: XLchannelConfig의 일부 오프셋(특히 다중 채널 STRIDE, serialNumber 위치)은
/// vxlapi 버전에 따라 다를 수 있어 실제 Vector 하드웨어에서 검증이 필요하다.
/// (장치/버스 데이터를 망가뜨리지 않도록 버퍼는 충분히 크게 할당하여 안전하게 파싱한다.)
/// </summary>
public sealed class VectorChannelEnumerator : ICanChannelEnumerator
{
    public CanHardwareType Hardware => CanHardwareType.Vector;

    // XLdriverConfig 헤더: dllVersion(4) + channelCount(4) + reserved[10](40) → channel[0] @ 48
    private const int HeaderSize = 48;
    private const int ChannelCountOffset = 4;

    // XLchannelConfig 내부 오프셋 (Pack=1 기준, 선두 안정 필드).
    private const int OffName = 0;          // char[32]
    private const int OffHwChannel = 34;    // byte
    private const int OffChannelIndex = 41; // byte
    private const int OffChannelMask = 42;  // uint64
    private const int OffSerialNumber = 151;// uint — 실HW(VN1640A)에서 검증 완료
    private const int ChannelStride = 227;  // sizeof(XLchannelConfig) — 실HW(VN1640A) 측정값

    private const int XL_SUCCESS = 0;
    // vxlapi64.dll 로더는 Vector.VectorNative(ModuleInitializer)에서 어셈블리 전역으로 등록됨.

    [DllImport("vxlapi64.dll")] private static extern int xlOpenDriver();
    [DllImport("vxlapi64.dll")] private static extern int xlCloseDriver();
    [DllImport("vxlapi64.dll")] private static extern int xlGetDriverConfig(nint pDriverConfig);

    public IReadOnlyList<CanChannelInfo> Enumerate(out string? diagnostic)
    {
        diagnostic = null;
        nint buffer = nint.Zero;
        bool opened = false;
        try
        {
            VectorNative.EnsureRegistered();
            if (xlOpenDriver() != XL_SUCCESS)
            {
                diagnostic = "xlOpenDriver 실패 — Vector XL 드라이버/하드웨어를 확인하세요.";
                return Array.Empty<CanChannelInfo>();
            }
            opened = true;

            // 충분히 크게 할당해 드라이버가 채워도 넘치지 않게 함 (안전).
            const int bufSize = 64 * 4096;
            var raw = new byte[bufSize]; // 0으로 초기화됨
            buffer = Marshal.AllocHGlobal(bufSize);
            Marshal.Copy(raw, 0, buffer, bufSize); // 언매니지드 버퍼도 0으로 초기화

            if (xlGetDriverConfig(buffer) != XL_SUCCESS)
            {
                diagnostic = "xlGetDriverConfig 실패.";
                return Array.Empty<CanChannelInfo>();
            }

            Marshal.Copy(buffer, raw, 0, bufSize);

            int channelCount = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(ChannelCountOffset, 4));
            if (channelCount <= 0)
            {
                diagnostic = "Vector 채널이 없습니다.";
                return Array.Empty<CanChannelInfo>();
            }
            channelCount = Math.Min(channelCount, 64);

            var result = new List<CanChannelInfo>(channelCount);
            for (int i = 0; i < channelCount; i++)
            {
                int b = HeaderSize + i * ChannelStride;
                if (b + ChannelStride > raw.Length) break;

                string name = ReadAnsi(raw, b + OffName, 32);
                if (string.IsNullOrWhiteSpace(name)) continue;

                byte hwChannel = raw[b + OffHwChannel];
                ulong mask = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(b + OffChannelMask, 8));
                uint serial = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(b + OffSerialNumber, 4));
                byte channelIndex = raw[b + OffChannelIndex];

                // 채널 마스크가 0이면 실제 채널이 아님(가상/미할당)으로 간주하고 건너뜀.
                if (mask == 0) continue;

                result.Add(new CanChannelInfo
                {
                    Hardware = CanHardwareType.Vector,
                    ChannelIndex = channelIndex,
                    DeviceName = name,
                    SerialNumber = serial == 0 ? "N/A" : serial.ToString(),
                    PhysicalChannel = hwChannel + 1,
                    VectorChannelMask = mask,
                    IsAvailable = true
                });
            }

            if (result.Count == 0) diagnostic = "사용 가능한 Vector CAN 채널이 없습니다.";
            return result;
        }
        catch (DllNotFoundException)
        {
            diagnostic = "vxlapi64.dll을 찾을 수 없습니다. Vector XL Driver를 설치하세요.";
            return Array.Empty<CanChannelInfo>();
        }
        catch (Exception ex)
        {
            diagnostic = $"Vector 채널 조회 오류: {ex.Message}";
            return Array.Empty<CanChannelInfo>();
        }
        finally
        {
            if (buffer != nint.Zero) Marshal.FreeHGlobal(buffer);
            if (opened) { try { xlCloseDriver(); } catch { /* ignore */ } }
        }
    }

    /// <summary>
    /// 진단용: xlGetDriverConfig 원시 버퍼를 분석해 channelCount, 각 채널 name 시작 오프셋과
    /// 그 간격(=실제 STRIDE 추정), base+151 위치의 시리얼 후보를 출력한다.
    /// </summary>
    public static string DumpRawConfig()
    {
        var sb = new StringBuilder();
        nint buffer = nint.Zero;
        bool opened = false;
        try
        {
            VectorNative.EnsureRegistered();
            if (xlOpenDriver() != XL_SUCCESS) return "xlOpenDriver 실패";
            opened = true;

            const int bufSize = 64 * 4096;
            var raw = new byte[bufSize];
            buffer = Marshal.AllocHGlobal(bufSize);
            Marshal.Copy(raw, 0, buffer, bufSize);
            if (xlGetDriverConfig(buffer) != XL_SUCCESS) return "xlGetDriverConfig 실패";
            Marshal.Copy(buffer, raw, 0, bufSize);

            int count = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(ChannelCountOffset, 4));
            sb.AppendLine($"channelCount = {count}");
            sb.AppendLine($"(현재 가정) HeaderSize={HeaderSize}, STRIDE={ChannelStride}, serialOff={OffSerialNumber}");

            // 'Channel' 문자열 위치로 name 시작 오프셋 추정 → 간격이 실제 STRIDE.
            var word = Encoding.ASCII.GetBytes("Channel");
            var nameStarts = new List<int>();
            for (int i = 0; i < bufSize - word.Length; i++)
            {
                bool m = true;
                for (int j = 0; j < word.Length; j++) if (raw[i + j] != word[j]) { m = false; break; }
                if (!m) continue;
                int s = i;
                while (s > 0 && raw[s - 1] != 0) s--;   // name 시작(직전 null)까지 역주행
                if (nameStarts.Count == 0 || nameStarts[^1] != s) nameStarts.Add(s);
            }

            sb.AppendLine($"발견된 채널 name 후보 = {nameStarts.Count}");
            int? prev = null;
            foreach (var s in nameStarts)
            {
                string name = ReadAnsi(raw, s, 40);
                uint mask = s + 42 + 8 <= bufSize ? BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(s + 42, 8)) is var mm ? (uint)mm : 0 : 0;
                string delta = prev.HasValue ? $"  Δ(STRIDE 추정)={s - prev.Value}" : "";
                sb.AppendLine($"  nameStart={s}  name='{name}'  mask@+42=0x{mask:X}{delta}");
                prev = s;
            }

            // 첫 채널 base(48) 기준으로 serial 후보 오프셋 몇 개 출력.
            sb.AppendLine("첫 채널 base=48 기준 serial 후보:");
            foreach (int off in new[] { 147, 151, 155, 159, 163 })
            {
                if (48 + off + 4 <= bufSize)
                    sb.AppendLine($"   base+{off}: {BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(48 + off, 4))}");
            }
            return sb.ToString();
        }
        catch (Exception ex) { return $"덤프 오류: {ex.Message}"; }
        finally
        {
            if (buffer != nint.Zero) Marshal.FreeHGlobal(buffer);
            if (opened) { try { xlCloseDriver(); } catch { } }
        }
    }

    private static string ReadAnsi(byte[] data, int offset, int maxLen)
    {
        int end = offset;
        int limit = Math.Min(offset + maxLen, data.Length);
        while (end < limit && data[end] != 0) end++;
        return Encoding.ASCII.GetString(data, offset, end - offset).Trim();
    }
}
