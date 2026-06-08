using System.Security.Cryptography;
using CanMessager.Core.Application;
using CanMessager.Core.Transport;
using CanMessager.Hal;
using CanMessager.Hal.Fake;

namespace CanMessager.Tests;

public class FileTransferEndToEndTests : IDisposable
{
    private const uint IdAtoB = 0x18DA01F1;
    private const uint IdBtoA = 0x18DAF101;

    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "cmap_test_" + Guid.NewGuid().ToString("N"));

    public FileTransferEndToEndTests() => Directory.CreateDirectory(_workDir);

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { /* ignore */ }
    }

    private async Task<byte[]> RoundTripAsync(byte[] content, CmapConfig cfg, TimeSpan timeout)
    {
        var bus = new FakeCanBus();
        await using var driverA = new FakeCanDriver(bus);
        await using var driverB = new FakeCanDriver(bus);
        await driverA.OpenAsync(new CanDriverConfig());
        await driverB.OpenAsync(new CanDriverConfig());

        await using var chA = new IsoTpChannel(driverA, new IsoTpConfig { TxCanId = IdAtoB, RxCanId = IdBtoA });
        await using var chB = new IsoTpChannel(driverB, new IsoTpConfig { TxCanId = IdBtoA, RxCanId = IdAtoB });
        await chA.StartAsync();
        await chB.StartAsync();

        var srcPath = Path.Combine(_workDir, "src.bin");
        await File.WriteAllBytesAsync(srcPath, content);

        var saveDir = Path.Combine(_workDir, "recv");
        var sender = new FileSendService(chA, cfg);
        var receiver = new FileReceiveService(chB, saveDir, cfg);

        var receivedPath = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.FileReceived += (_, e) => receivedPath.TrySetResult(e.SavedPath);

        using var cts = new CancellationTokenSource(timeout);
        var recvTask = receiver.ReceiveOneAsync(cts.Token);

        await sender.SendFileAsync(srcPath, cts.Token);
        await recvTask;

        var path = await receivedPath.Task.WaitAsync(timeout);
        return await File.ReadAllBytesAsync(path);
    }

    private static byte[] MakeContent(int size)
    {
        var data = new byte[size];
        Random.Shared.NextBytes(data);
        return data;
    }

    [Theory]
    [InlineData(0)]        // 빈 파일
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(4096)]     // 정확히 1 청크
    [InlineData(4097)]     // 1청크 + 1바이트
    public async Task SmallFiles_TransferIntact(int size)
    {
        var content = MakeContent(size);
        var cfg = new CmapConfig { ChunkSize = 4096, WindowSize = 8, ProgressIntervalMs = 0 };
        var got = await RoundTripAsync(content, cfg, TimeSpan.FromSeconds(15));
        Assert.Equal(content, got);
    }

    [Fact]
    public async Task MultiBlock_SmallChunks()
    {
        // 작은 청크 + 작은 윈도우로 여러 블록/ACK 사이클을 강제.
        var content = MakeContent(10_000);
        var cfg = new CmapConfig { ChunkSize = 256, WindowSize = 4, ProgressIntervalMs = 0 };
        var got = await RoundTripAsync(content, cfg, TimeSpan.FromSeconds(20));
        Assert.Equal(content, got);
    }

    [Fact]
    public async Task LargeFile_1MiB_HashMatches()
    {
        var content = MakeContent(1024 * 1024);
        var cfg = new CmapConfig { ChunkSize = 4096, WindowSize = 32, ProgressIntervalMs = 0 };
        var got = await RoundTripAsync(content, cfg, TimeSpan.FromSeconds(30));

        Assert.Equal(content.Length, got.Length);
        Assert.Equal(SHA256.HashData(content), SHA256.HashData(got));
    }

    [Fact]
    public async Task WithoutChunkCrc_StillWorks()
    {
        var content = MakeContent(20_000);
        var cfg = new CmapConfig { ChunkSize = 1024, WindowSize = 8, UseChunkCrc = false, ProgressIntervalMs = 0 };
        var got = await RoundTripAsync(content, cfg, TimeSpan.FromSeconds(20));
        Assert.Equal(content, got);
    }

    [Fact]
    public async Task Receiver_CanReject()
    {
        var bus = new FakeCanBus();
        await using var driverA = new FakeCanDriver(bus);
        await using var driverB = new FakeCanDriver(bus);
        await driverA.OpenAsync(new CanDriverConfig());
        await driverB.OpenAsync(new CanDriverConfig());

        await using var chA = new IsoTpChannel(driverA, new IsoTpConfig { TxCanId = IdAtoB, RxCanId = IdBtoA });
        await using var chB = new IsoTpChannel(driverB, new IsoTpConfig { TxCanId = IdBtoA, RxCanId = IdAtoB });
        await chA.StartAsync();
        await chB.StartAsync();

        var srcPath = Path.Combine(_workDir, "reject_src.bin");
        await File.WriteAllBytesAsync(srcPath, MakeContent(5000));

        var cfg = new CmapConfig { ProgressIntervalMs = 0 };
        var sender = new FileSendService(chA, cfg);
        var receiver = new FileReceiveService(chB, Path.Combine(_workDir, "recv2"), cfg);
        receiver.IncomingFile += (_, e) => { e.Accept = false; e.RejectReason = CmapReason.UserRejected; };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var recvTask = receiver.ReceiveOneAsync(cts.Token);

        var ex = await Assert.ThrowsAsync<CmapException>(() => sender.SendFileAsync(srcPath, cts.Token));
        Assert.Equal(CmapReason.UserRejected, ex.Reason);
        await recvTask;
    }
}
