using CanMessager.Core.Transport;
using CanMessager.Hal;
using CanMessager.Hal.Fake;

namespace CanMessager.Tests;

public class IsoTpLoopbackTests
{
    private const uint IdAtoB = 0x18DA01F1;
    private const uint IdBtoA = 0x18DAF101;

    /// <summary>A→B 단방향으로 payload를 보내고 B가 받은 바이트를 반환한다.</summary>
    private static async Task<byte[]> SendReceiveAsync(
        byte[] payload, byte blockSize = 0, byte stMin = 0, TimeSpan? frameDelay = null)
    {
        var bus = new FakeCanBus();
        if (frameDelay is { } d) bus.FrameDelay = d;

        await using var driverA = new FakeCanDriver(bus);
        await using var driverB = new FakeCanDriver(bus);
        await driverA.OpenAsync(new CanDriverConfig());
        await driverB.OpenAsync(new CanDriverConfig());

        var cfgA = new IsoTpConfig { TxCanId = IdAtoB, RxCanId = IdBtoA };
        var cfgB = new IsoTpConfig
        {
            TxCanId = IdBtoA, RxCanId = IdAtoB,
            BlockSize = blockSize, STmin = stMin
        };

        await using var chA = new IsoTpChannel(driverA, cfgA);
        await using var chB = new IsoTpChannel(driverB, cfgB);

        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        chB.MessageReceived += (_, e) => received.TrySetResult(e.Payload.ToArray());
        chB.ErrorOccurred += (_, e) => received.TrySetException(
            new IsoTpException(e.Error, $"{e.Direction}: {e.Message}"));
        chA.ErrorOccurred += (_, e) => received.TrySetException(
            new IsoTpException(e.Error, $"{e.Direction}: {e.Message}"));

        await chA.StartAsync();
        await chB.StartAsync();

        await chA.SendAsync(payload);

        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(completed == received.Task, "수신 타임아웃 (10s).");
        return await received.Task;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]    // SF 1-byte PCI 경계
    [InlineData(62)]   // SF escape 최대
    public async Task SingleFrame_Sizes(int size)
    {
        var payload = MakePayload(size);
        var got = await SendReceiveAsync(payload);
        Assert.Equal(payload, got);
    }

    [Theory]
    [InlineData(63)]      // SF 불가 → 멀티프레임 최소
    [InlineData(100)]
    [InlineData(1024)]
    [InlineData(4095)]    // FF small length 경계
    [InlineData(4096)]    // FF escape 시작
    [InlineData(65_536)]
    public async Task MultiFrame_Sizes(int size)
    {
        var payload = MakePayload(size);
        var got = await SendReceiveAsync(payload);
        Assert.Equal(payload, got);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    public async Task MultiFrame_WithBlockSize(byte blockSize)
    {
        var payload = MakePayload(10_000);
        var got = await SendReceiveAsync(payload, blockSize: blockSize);
        Assert.Equal(payload, got);
    }

    [Fact]
    public async Task MultiFrame_WithSTmin()
    {
        var payload = MakePayload(2_000);
        var got = await SendReceiveAsync(payload, blockSize: 0, stMin: 0xF1); // 100us
        Assert.Equal(payload, got);
    }

    [Fact]
    public async Task LargePayload_1MiB()
    {
        var payload = MakePayload(1024 * 1024);
        var got = await SendReceiveAsync(payload, blockSize: 16);
        Assert.Equal(payload.Length, got.Length);
        Assert.True(payload.AsSpan().SequenceEqual(got));
    }

    private static byte[] MakePayload(int size)
    {
        var data = new byte[size];
        for (int i = 0; i < size; i++)
            data[i] = (byte)(i * 31 + 7);
        return data;
    }
}
