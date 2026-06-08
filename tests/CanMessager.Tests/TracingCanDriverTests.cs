using CanMessager.Hal;
using CanMessager.Hal.Fake;

namespace CanMessager.Tests;

public class TracingCanDriverTests
{
    [Fact]
    public async Task Traces_Tx_And_Rx_When_Enabled()
    {
        var bus = new FakeCanBus();
        await using var raw = new FakeCanDriver(bus);
        await using var tracer = new TracingCanDriver(raw) { Enabled = true };
        await using var peer = new FakeCanDriver(bus);

        await raw.OpenAsync(new CanDriverConfig());
        await peer.OpenAsync(new CanDriverConfig());

        var traces = new List<CanTraceEventArgs>();
        tracer.FrameTraced += (_, e) => traces.Add(e);

        // TX: tracer를 통해 송신.
        await tracer.SendFrameAsync(new CanFdFrame(0x123, new byte[] { 1, 2, 3 }, extendedId: false));

        // RX: peer가 송신 → raw가 수신 → tracer가 트레이스.
        var rxArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tracer.FrameReceived += (_, _) => rxArrived.TrySetResult();
        await peer.SendFrameAsync(new CanFdFrame(0x456, new byte[] { 9 }, extendedId: false));
        await rxArrived.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Contains(traces, t => t.Direction == CanTraceDirection.Tx && t.Frame.CanId == 0x123);
        Assert.Contains(traces, t => t.Direction == CanTraceDirection.Rx && t.Frame.CanId == 0x456);
    }

    [Fact]
    public async Task Does_Not_Trace_When_Disabled()
    {
        var bus = new FakeCanBus();
        await using var raw = new FakeCanDriver(bus);
        await using var tracer = new TracingCanDriver(raw) { Enabled = false };
        await raw.OpenAsync(new CanDriverConfig());

        var traced = false;
        tracer.FrameTraced += (_, _) => traced = true;
        await tracer.SendFrameAsync(new CanFdFrame(0x1, new byte[] { 0 }, extendedId: false));

        Assert.False(traced);
    }

    [Fact]
    public async Task Forwards_FrameReceived_To_Subscribers()
    {
        var bus = new FakeCanBus();
        await using var raw = new FakeCanDriver(bus);
        await using var tracer = new TracingCanDriver(raw); // Enabled=false 이어도 전달은 되어야 함
        await using var peer = new FakeCanDriver(bus);
        await raw.OpenAsync(new CanDriverConfig());
        await peer.OpenAsync(new CanDriverConfig());

        var got = new TaskCompletionSource<CanFdFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        tracer.FrameReceived += (_, e) => got.TrySetResult(e.Frame);

        await peer.SendFrameAsync(new CanFdFrame(0x7FF, new byte[] { 0xAB }, extendedId: false));
        var frame = await got.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0x7FFu, frame.CanId);
    }
}
