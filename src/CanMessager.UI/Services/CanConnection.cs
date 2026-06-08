// =====================================================================
//  CanConnection — UI↔코어 스택 조립/수명 관리(오케스트레이션)
//  개요   : 설정(ConnectionSettings)에 따라 드라이버+ISO-TP+송수신 서비스를 구성.
//           역할(송신/수신/루프백) + 하드웨어(Fake/PEAK/Vector) 분기.
//  핵심   : - ConnectAsync : 드라이버 생성→TracingCanDriver로 래핑→ISO-TP 채널 시작
//                            역할별 SendService/ReceiveService 구성, 루프백은 가상 peer 생성
//           - ReceiveLoopAsync : 수신 1건씩 반복 처리
//           - FrameTraced : 트레이스 한 줄 포맷하여 상위(UI)로 전달
//  추후 개선:
//           - 설정 영속화(최근 채널/ID/속도 저장) 미구현
//           - 전송 취소/일시정지 API 미노출
//           - 다중 동시 전송(큐) 미지원
// =====================================================================
using System.IO;
using CanMessager.Core.Application;
using CanMessager.Core.Transport;
using CanMessager.Hal;
using CanMessager.Hal.Fake;

namespace CanMessager.UI.Services;

/// <summary>이 인스턴스가 수행할 역할.</summary>
public enum TransferRole
{
    /// <summary>송신처 — 파일을 전송한다.</summary>
    Sender,
    /// <summary>수신처 — 파일을 수신/저장한다.</summary>
    Receiver,
    /// <summary>루프백 데모 — 한 프로세스 안에서 송신+수신을 모두 시연 (Fake 전용).</summary>
    LoopbackDemo
}

/// <summary>연결 설정값 (UI → 스택 조립).</summary>
public sealed record ConnectionSettings
{
    public TransferRole Role { get; init; } = TransferRole.LoopbackDemo;
    public CanHardwareType Hardware { get; init; } = CanHardwareType.Fake;
    public int ChannelIndex { get; init; }

    /// <summary>Vector 전용: 선택한 채널의 xlOpenPort 채널 마스크 (실드라이버용).</summary>
    public ulong VectorChannelMask { get; init; }

    /// <summary>PEAK 전용: 선택한 채널 핸들 (실드라이버용).</summary>
    public ushort PeakHandle { get; init; }
    public int NominalBitrate { get; init; } = 1_000_000;  // 중재 최대 1 Mbit/s
    public int DataBitrate { get; init; } = 8_000_000;     // 데이터 최대 8 Mbit/s (CAN-FD)

    public uint TxCanId { get; init; } = 0x101;   // 11-bit 표준 ID
    public uint RxCanId { get; init; } = 0x102;   // 11-bit 표준 ID
    public bool ExtendedId { get; init; } = false; // 기본: 표준(11-bit)

    public string SaveDirectory { get; init; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CanMessager", "Received");

    /// <summary>수신 파일명 지정 (비우면 송신측 원본 파일명 사용).</summary>
    public string? SaveFileNameOverride { get; init; }

    /// <summary>연결 시 CAN 트레이스 활성화 여부.</summary>
    public bool EnableTracing { get; init; }
}

/// <summary>
/// 드라이버 + ISO-TP + 송수신 서비스를 조립/보유하는 연결 객체.
/// Fake 하드웨어 선택 시 같은 프로세스 안에 가상 상대(peer)를 만들어 루프백 데모를 제공한다.
/// 실제 하드웨어(Vector/PEAK)는 단일 드라이버에 송/수신 서비스를 함께 올린다.
/// </summary>
public sealed class CanConnection : IAsyncDisposable
{
    private readonly ConnectionSettings _settings;
    private readonly CmapConfig _cmap;

    private ICanDriver? _localDriver;
    private ICanDriver? _peerDriver;          // 루프백 데모 전용
    private TracingCanDriver? _tracer;
    private bool _tracingEnabled;
    private IsoTpChannel? _localChannel;
    private IsoTpChannel? _peerChannel;        // 루프백 데모 전용
    private FileSendService? _sendService;
    private FileReceiveService? _receiveService;
    private CancellationTokenSource? _receiveLoopCts;
    private Task? _receiveLoop;

    public CanConnection(ConnectionSettings settings, CmapConfig? cmap = null)
    {
        _settings = settings;
        _cmap = cmap ?? new CmapConfig();
        _tracingEnabled = settings.EnableTracing;
    }

    /// <summary>CAN 트레이스 on/off (연결 중에도 토글 가능).</summary>
    public bool TracingEnabled
    {
        get => _tracer?.Enabled ?? _tracingEnabled;
        set
        {
            _tracingEnabled = value;
            if (_tracer is not null) _tracer.Enabled = value;
        }
    }

    /// <summary>트레이스 한 줄(포맷 완료) 발생.</summary>
    public event EventHandler<string>? FrameTraced;

    public TransferRole Role => _settings.Role;
    public bool IsLoopback => _settings.Role == TransferRole.LoopbackDemo;
    public bool CanSend => _sendService is not null;
    public bool IsConnected { get; private set; }

    public event EventHandler<string>? Log;
    public event EventHandler<FileTransferProgress>? SendProgress;
    public event EventHandler<FileTransferProgress>? ReceiveProgress;
    public event EventHandler<FileReceivedEventArgs>? FileReceived;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (IsConnected) return;

        if (_settings.Role == TransferRole.LoopbackDemo && _settings.Hardware != CanHardwareType.Fake)
            throw new NotSupportedException("루프백 데모는 Fake 인터페이스에서만 사용할 수 있습니다.");

        Directory.CreateDirectory(_settings.SaveDirectory);
        var driverCfg = new CanDriverConfig
        {
            Hardware = _settings.Hardware,
            ChannelIndex = _settings.ChannelIndex,
            NominalBitrate = _settings.NominalBitrate,
            DataBitrate = _settings.DataBitrate,
            NativeChannelId = _settings.Hardware == CanHardwareType.Peak
                ? _settings.PeakHandle
                : _settings.VectorChannelMask
        };

        bool loopback = _settings.Role == TransferRole.LoopbackDemo;
        FakeCanBus? bus = _settings.Hardware == CanHardwareType.Fake ? new FakeCanBus() : null;

        // 로컬 드라이버를 트레이서로 감싸 송/수신 프레임을 노출.
        _tracer = new TracingCanDriver(CreateDriver(driverCfg, bus)) { Enabled = _tracingEnabled };
        _tracer.FrameTraced += (_, e) => FrameTraced?.Invoke(this, FormatTrace(e));
        _localDriver = _tracer;
        await _localDriver.OpenAsync(driverCfg, ct).ConfigureAwait(false);

        _localChannel = new IsoTpChannel(_localDriver, new IsoTpConfig
        {
            TxCanId = _settings.TxCanId,
            RxCanId = _settings.RxCanId,
            ExtendedId = _settings.ExtendedId
        });
        await _localChannel.StartAsync(ct).ConfigureAwait(false);

        // 역할별 서비스 구성.
        bool wantSend = _settings.Role is TransferRole.Sender or TransferRole.LoopbackDemo;
        bool wantReceive = _settings.Role is TransferRole.Receiver or TransferRole.LoopbackDemo;

        if (wantSend)
        {
            _sendService = new FileSendService(_localChannel, _cmap);
            _sendService.Progress += (_, p) => SendProgress?.Invoke(this, p);
        }

        if (wantReceive)
        {
            // 루프백: 같은 버스에 가상 상대(peer) 채널을 만들어 로컬 송신을 수신.
            // 단일 역할(수신처): 로컬 채널에서 직접 수신.
            IsoTpChannel rxChannel;
            if (loopback)
            {
                _peerDriver = CreateDriver(driverCfg, bus);
                await _peerDriver.OpenAsync(driverCfg, ct).ConfigureAwait(false);
                _peerChannel = new IsoTpChannel(_peerDriver, new IsoTpConfig
                {
                    TxCanId = _settings.RxCanId,
                    RxCanId = _settings.TxCanId,
                    ExtendedId = _settings.ExtendedId
                });
                await _peerChannel.StartAsync(ct).ConfigureAwait(false);
                rxChannel = _peerChannel;
            }
            else
            {
                rxChannel = _localChannel;
            }

            _receiveService = new FileReceiveService(rxChannel, _settings.SaveDirectory, _cmap);
            _receiveService.Progress += (_, p) => ReceiveProgress?.Invoke(this, p);
            _receiveService.IncomingFile += (_, e) =>
            {
                // 사용자가 파일명을 지정한 경우 적용 (확장자가 없으면 원본 확장자 유지).
                if (!string.IsNullOrWhiteSpace(_settings.SaveFileNameOverride))
                {
                    var name = _settings.SaveFileNameOverride!.Trim();
                    if (!Path.HasExtension(name))
                        name += Path.GetExtension(e.FileName);
                    e.SavePath = Path.Combine(_settings.SaveDirectory, name);
                    RaiseLog($"수신 시작: {e.FileName} ({FormatBytes(e.TotalBytes)}) → 저장명 '{name}'");
                }
                else
                {
                    RaiseLog($"수신 시작: {e.FileName} ({FormatBytes(e.TotalBytes)})");
                }
            };
            _receiveService.FileReceived += (_, e) =>
            {
                RaiseLog($"수신 완료: {e.FileName} → {e.SavedPath} (SHA-256 {(e.HashVerified ? "검증됨" : "불일치")})");
                FileReceived?.Invoke(this, e);
            };

            _receiveLoopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _receiveLoop = Task.Run(() => ReceiveLoopAsync(_receiveLoopCts.Token));
        }

        RaiseLog(loopback
            ? $"루프백 데모 모드로 연결됨. 수신 폴더: {_settings.SaveDirectory}"
            : $"{RoleLabel(_settings.Role)} 모드 · {_settings.Hardware} 채널 {_settings.ChannelIndex} 연결됨"
              + (wantReceive ? $" · 수신 폴더: {_settings.SaveDirectory}" : ""));

        IsConnected = true;
    }

    private static string RoleLabel(TransferRole role) => role switch
    {
        TransferRole.Sender => "송신처",
        TransferRole.Receiver => "수신처",
        TransferRole.LoopbackDemo => "루프백 데모",
        _ => role.ToString()
    };

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _receiveService!.ReceiveOneAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                RaiseLog($"수신 오류: {ex.Message}");
            }
        }
    }

    public async Task SendFileAsync(string path, CancellationToken ct = default)
    {
        if (!IsConnected)
            throw new InvalidOperationException("연결되지 않았습니다.");
        if (_sendService is null)
            throw new InvalidOperationException("현재 역할(수신처)에서는 전송할 수 없습니다.");

        RaiseLog($"전송 시작: {Path.GetFileName(path)} ({FormatBytes(new FileInfo(path).Length)})");
        await _sendService.SendFileAsync(path, ct).ConfigureAwait(false);
        RaiseLog($"전송 완료: {Path.GetFileName(path)}");
    }

    private static ICanDriver CreateDriver(CanDriverConfig cfg, FakeCanBus? bus) => cfg.Hardware switch
    {
        CanHardwareType.Fake => new FakeCanDriver(bus
            ?? throw new InvalidOperationException("Fake 드라이버에는 버스가 필요합니다.")),
        CanHardwareType.Peak => new CanMessager.Hal.Peak.PeakCanDriver(),
        CanHardwareType.Vector => new CanMessager.Hal.Vector.VectorCanDriver(),
        _ => throw new ArgumentOutOfRangeException(nameof(cfg))
    };

    private void RaiseLog(string message) => Log?.Invoke(this, message);

    private static string FormatTrace(CanTraceEventArgs e)
    {
        var f = e.Frame;
        string id = f.ExtendedId ? $"{f.CanId:X8}" : $"{f.CanId:X3}";
        string dir = e.Direction == CanTraceDirection.Tx ? "TX →" : "RX ←";
        string data = Convert.ToHexString(f.Data.Span).Chunk(2)
            .Aggregate(new System.Text.StringBuilder(), (sb, c) => sb.Append(c).Append(' '))
            .ToString().TrimEnd();
        return $"{e.Timestamp:HH:mm:ss.fff}  {dir}  ID={id}  FD  [{f.Data.Length,2}]  {data}";
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return $"{v:0.##} {units[u]}";
    }

    public async ValueTask DisposeAsync()
    {
        IsConnected = false;
        if (_receiveLoopCts is not null) await _receiveLoopCts.CancelAsync().ConfigureAwait(false);
        if (_receiveLoop is not null)
        {
            try { await _receiveLoop.ConfigureAwait(false); } catch { /* ignore */ }
        }

        if (_localChannel is not null) await _localChannel.DisposeAsync().ConfigureAwait(false);
        if (_peerChannel is not null) await _peerChannel.DisposeAsync().ConfigureAwait(false);
        if (_localDriver is not null) await _localDriver.DisposeAsync().ConfigureAwait(false);
        if (_peerDriver is not null) await _peerDriver.DisposeAsync().ConfigureAwait(false);
        _receiveLoopCts?.Dispose();
    }
}
