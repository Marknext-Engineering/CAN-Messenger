using CanMessager.Hal;
using CanMessager.Hal.Discovery;
using CanMessager.Hal.Peak;
using CanMessager.Hal.Vector;

// 사용법:
//   dotnet run --project tools/HwProbe [fake|peak|vector]   → 채널 스캔
//   dotnet run --project tools/HwProbe peaktest             → 양 채널(0x51/0x52) 송수신 검증
if (args.Length > 0 && args[0].Equals("peaktest", StringComparison.OrdinalIgnoreCase))
{
    await PeakTwoChannelTest();
    return;
}

if (args.Length > 0 && args[0].Equals("vectordump", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("=== Vector xlGetDriverConfig 원시 분석 ===");
    Console.WriteLine(CanMessager.Hal.Discovery.VectorChannelEnumerator.DumpRawConfig());
    return;
}

if (args.Length > 0 && args[0].Equals("vectortest", StringComparison.OrdinalIgnoreCase))
{
    // 기본: 가상 채널 1(0x20) ↔ 가상 채널 2(0x40). 인자로 마스크 지정 가능.
    ulong txMask = args.Length > 1 ? Convert.ToUInt64(args[1], 16) : 0x20;
    ulong rxMask = args.Length > 2 ? Convert.ToUInt64(args[2], 16) : 0x40;
    await VectorTwoChannelTest(txMask, rxMask);
    return;
}

var hw = args.Length > 0 ? args[0].ToLowerInvariant() : "peak";
var hardware = hw switch
{
    "fake" => CanHardwareType.Fake,
    "vector" => CanHardwareType.Vector,
    _ => CanHardwareType.Peak
};

Console.WriteLine($"=== {hardware} 채널 스캔 ===");
var channels = CanChannelScanner.Scan(hardware, out var diag);
if (!string.IsNullOrEmpty(diag)) Console.WriteLine($"진단: {diag}");

if (channels.Count == 0)
{
    Console.WriteLine("발견된 채널이 없습니다.");
    return;
}

int i = 0;
foreach (var ch in channels)
{
    Console.WriteLine($"[{i++}] {ch.Display}");
    Console.WriteLine($"     ChannelIndex={ch.ChannelIndex}  PhysCh={ch.PhysicalChannel}  " +
                      $"PeakHandle=0x{ch.PeakHandle:X2}  VectorMask=0x{ch.VectorChannelMask:X}  Available={ch.IsAvailable}");
}
return;

static async Task PeakTwoChannelTest()
{
    Console.WriteLine("=== PEAK 양 채널 송수신 테스트 (500k/2M CAN-FD) ===");
    Console.WriteLine("주의: CH1↔CH2를 CAN 케이블(120Ω 종단)로 연결해야 수신이 확인됩니다.\n");

    const ushort PCAN_USBBUS1 = 0x51;
    var cfg1 = new CanDriverConfig { Hardware = CanHardwareType.Peak, NativeChannelId = PCAN_USBBUS1 };
    var cfg2 = new CanDriverConfig { Hardware = CanHardwareType.Peak, NativeChannelId = PCAN_USBBUS1 + 1 };

    await using var ch1 = new PeakCanDriver();
    await using var ch2 = new PeakCanDriver();

    int rxOn2 = 0, rxOn1 = 0;
    ch2.FrameReceived += (_, e) => { Interlocked.Increment(ref rxOn2); Print("CH2 수신", e.Frame); };
    ch1.FrameReceived += (_, e) => { Interlocked.Increment(ref rxOn1); Print("CH1 수신", e.Frame); };

    try
    {
        await ch1.OpenAsync(cfg1);
        Console.WriteLine("CH1(0x51) 초기화 성공");
        await ch2.OpenAsync(cfg2);
        Console.WriteLine("CH2(0x52) 초기화 성공\n");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"채널 열기 실패: {ex.Message}");
        return;
    }

    const int n = 5;
    for (int k = 0; k < n; k++)
    {
        var payload = new byte[64];
        for (int j = 0; j < payload.Length; j++) payload[j] = (byte)(k * 16 + j);
        var frame = new CanFdFrame(0x18DA01F1, payload, extendedId: true, fd: true, bitRateSwitch: true);
        await ch1.SendFrameAsync(frame);
        Console.WriteLine($"CH1 송신 #{k} (64 byte)");
        await Task.Delay(50);
    }

    await Task.Delay(500);
    Console.WriteLine($"\n결과: CH1 송신 {n}개 → CH2 수신 {rxOn2}개 (CH1 자체수신 {rxOn1}개)");
    Console.WriteLine(rxOn2 > 0
        ? "✅ 실제 CAN-FD 송수신 동작 확인!"
        : "⚠ 수신 0 — 두 채널이 케이블로 연결되어 있는지 확인하세요 (초기화/송신 자체는 정상).");

    await ch1.CloseAsync();
    await ch2.CloseAsync();
}

static void Print(string tag, CanFdFrame f)
    => Console.WriteLine($"   {tag}: ID=0x{f.CanId:X}  len={f.Data.Length}  data0={(f.Data.Length > 0 ? f.Data.Span[0] : 0)}");

static async Task VectorTwoChannelTest(ulong txMask, ulong rxMask)
{
    Console.WriteLine($"=== Vector 송수신 테스트 (TX mask=0x{txMask:X}, RX mask=0x{rxMask:X}, 1M/8M) ===");
    Console.WriteLine("가상 채널은 Vector Hardware Config에서 같은 가상 버스에 있어야 합니다.\n");

    var txCfg = new CanDriverConfig { Hardware = CanHardwareType.Vector, NativeChannelId = txMask, NominalBitrate = 1_000_000, DataBitrate = 8_000_000 };
    var rxCfg = new CanDriverConfig { Hardware = CanHardwareType.Vector, NativeChannelId = rxMask, NominalBitrate = 1_000_000, DataBitrate = 8_000_000 };

    await using var tx = new VectorCanDriver();
    await using var rx = new VectorCanDriver();

    int rxCount = 0;
    rx.FrameReceived += (_, e) => { Interlocked.Increment(ref rxCount); Print("RX 수신", e.Frame); };

    try
    {
        await rx.OpenAsync(rxCfg);
        Console.WriteLine($"RX 채널(0x{rxMask:X}) 열기 성공");
        await tx.OpenAsync(txCfg);
        Console.WriteLine($"TX 채널(0x{txMask:X}) 열기 성공\n");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"채널 열기 실패: {ex.Message}");
        return;
    }

    const int n = 5;
    for (int k = 0; k < n; k++)
    {
        var payload = new byte[64];
        for (int j = 0; j < payload.Length; j++) payload[j] = (byte)(k * 16 + j);
        await tx.SendFrameAsync(new CanFdFrame(0x101, payload, extendedId: false, fd: true, bitRateSwitch: true));
        Console.WriteLine($"TX 송신 #{k} (64 byte)");
        await Task.Delay(50);
    }

    await Task.Delay(500);
    Console.WriteLine($"\n결과: TX {n}개 → RX {rxCount}개");
    Console.WriteLine(rxCount > 0
        ? "✅ Vector CAN-FD 송수신 동작 확인!"
        : "⚠ 수신 0 — 두 채널이 같은 버스(가상 버스/물리 배선)에 있는지 확인하세요.");

    await tx.CloseAsync();
    await rx.CloseAsync();
}
