// =====================================================================
//  MainViewModel — 메인 화면 ViewModel (MVVM)
//  개요   : 연결 설정/역할/채널 스캔/파일 전송/진행률/로그/트레이스를 바인딩.
//  핵심   : - ConnectAsync : 역할 기반 TX/RX ID 자동 배정 후 CanConnection 연결
//           - ScanChannels : 하드웨어 변경 시 채널 자동 스캔
//           - 트레이스는 ConcurrentQueue + DispatcherTimer(200ms)로 일괄 반영(UI 부하↓)
//           - RunOnUi : 백그라운드 이벤트를 UI 스레드로 마샬링
//  추후 개선:
//           - 속도(중재/데이터) 선택 콤보 UI 노출, 설정 저장/복원
//           - 전송 취소 버튼, 다중 파일 큐
// =====================================================================
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CanMessager.Core.Application;
using CanMessager.Hal;
using CanMessager.Hal.Discovery;
using CanMessager.UI.Mvvm;
using CanMessager.UI.Services;
using Microsoft.Win32;

namespace CanMessager.UI.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private CanConnection? _connection;
    private readonly ConcurrentQueue<string> _traceBuffer = new();
    private readonly DispatcherTimer _traceTimer;

    public MainViewModel()
    {
        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !IsConnected && !IsBusy && SelectedChannel is not null);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, () => IsConnected && !IsBusy);
        BrowseCommand = new RelayCommand(Browse, () => !IsBusy);
        BrowseSaveFolderCommand = new RelayCommand(BrowseSaveFolder, () => !IsConnected);
        ClearTraceCommand = new RelayCommand(() => TraceEntries.Clear());
        ScanCommand = new RelayCommand(ScanChannels, () => !IsConnected);
        SendCommand = new AsyncRelayCommand(SendAsync,
            () => IsConnected && !IsBusy && File.Exists(SelectedFilePath));

        ScanChannels(); // 시작 시 기본 하드웨어(Fake) 채널 스캔

        // 트레이스는 대량 발생하므로 200ms마다 일괄 반영 (UI 부하 방지).
        _traceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _traceTimer.Tick += (_, _) => FlushTrace();
        _traceTimer.Start();
    }

    // ===== 연결 설정 =====
    public IReadOnlyList<TransferRole> Roles { get; } = Enum.GetValues<TransferRole>();

    private TransferRole _selectedRole = TransferRole.LoopbackDemo;
    public TransferRole SelectedRole
    {
        get => _selectedRole;
        set
        {
            if (SetProperty(ref _selectedRole, value,
                    also: new[] { nameof(ShowSendPanel), nameof(ShowReceivePanel), nameof(RoleHint) }))
                RefreshCommands();
        }
    }

    /// <summary>송신 패널 표시 여부 (송신처 / 루프백).</summary>
    public bool ShowSendPanel => SelectedRole is TransferRole.Sender or TransferRole.LoopbackDemo;

    /// <summary>수신 패널 표시 여부 (수신처 / 루프백).</summary>
    public bool ShowReceivePanel => SelectedRole is TransferRole.Receiver or TransferRole.LoopbackDemo;

    public string RoleHint => SelectedRole switch
    {
        TransferRole.Sender => "이 PC는 파일을 보냅니다. 상대 PC는 '수신' 모드 · 동일한 ID 쌍으로 설정하세요.",
        TransferRole.Receiver => "이 PC는 파일을 받아 저장합니다. 상대 PC는 '송신' 모드 · 동일한 ID 쌍으로 설정하세요.",
        TransferRole.LoopbackDemo => "한 PC에서 송신+수신을 함께 시연합니다 (Fake 전용).",
        _ => ""
    };

    public IReadOnlyList<CanHardwareType> HardwareTypes { get; } =
        Enum.GetValues<CanHardwareType>();

    private CanHardwareType _selectedHardware = CanHardwareType.Fake;
    public CanHardwareType SelectedHardware
    {
        get => _selectedHardware;
        set { if (SetProperty(ref _selectedHardware, value)) ScanChannels(); }
    }

    // ===== 장치/채널 스캔 =====
    public ObservableCollection<CanChannelInfo> AvailableChannels { get; } = new();

    private CanChannelInfo? _selectedChannel;
    public CanChannelInfo? SelectedChannel
    {
        get => _selectedChannel;
        set { if (SetProperty(ref _selectedChannel, value)) RefreshCommands(); }
    }

    private string _txCanIdHex = "101";
    public string TxCanIdHex { get => _txCanIdHex; set => SetProperty(ref _txCanIdHex, value); }

    private string _rxCanIdHex = "102";
    public string RxCanIdHex { get => _rxCanIdHex; set => SetProperty(ref _rxCanIdHex, value); }

    private bool _extendedId;
    public bool ExtendedId { get => _extendedId; set => SetProperty(ref _extendedId, value); }

    private string _dataBitrate = "8000000";
    public string DataBitrate { get => _dataBitrate; set => SetProperty(ref _dataBitrate, value); }

    // ===== 상태 =====
    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        private set { if (SetProperty(ref _isConnected, value)) RefreshCommands(); }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (SetProperty(ref _isBusy, value)) RefreshCommands(); }
    }

    private string _connectionStatus = "연결 안 됨";
    public string ConnectionStatus { get => _connectionStatus; private set => SetProperty(ref _connectionStatus, value); }

    // ===== 파일 선택 (송신) =====
    private string _selectedFilePath = "";
    public string SelectedFilePath
    {
        get => _selectedFilePath;
        set
        {
            if (SetProperty(ref _selectedFilePath, value, also: new[] { nameof(HasFileSummary) }))
            {
                UpdateFileSummary();
                RefreshCommands();
            }
        }
    }

    private string _fileSummary = "";
    public string FileSummary { get => _fileSummary; private set => SetProperty(ref _fileSummary, value, also: new[] { nameof(HasFileSummary) }); }
    public bool HasFileSummary => !string.IsNullOrEmpty(_fileSummary);

    // ===== 수신 저장 설정 =====
    private string _saveDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CanMessager", "Received");
    public string SaveDirectory { get => _saveDirectory; set => SetProperty(ref _saveDirectory, value); }

    private string _saveFileNameOverride = "";
    public string SaveFileNameOverride { get => _saveFileNameOverride; set => SetProperty(ref _saveFileNameOverride, value); }

    // ===== CAN 트레이스 =====
    private bool _enableTracing;
    public bool EnableTracing
    {
        get => _enableTracing;
        set
        {
            if (SetProperty(ref _enableTracing, value) && _connection is not null)
                _connection.TracingEnabled = value;
        }
    }

    public ObservableCollection<string> TraceEntries { get; } = new();

    // ===== 전송 진행 =====
    private double _sendPercent;
    public double SendPercent { get => _sendPercent; private set => SetProperty(ref _sendPercent, value); }

    private string _sendStatus = "";
    public string SendStatus { get => _sendStatus; private set => SetProperty(ref _sendStatus, value); }

    // ===== 수신 진행 =====
    private double _receivePercent;
    public double ReceivePercent { get => _receivePercent; private set => SetProperty(ref _receivePercent, value); }

    private string _receiveStatus = "";
    public string ReceiveStatus { get => _receiveStatus; private set => SetProperty(ref _receiveStatus, value); }

    // ===== 로그 / 수신 목록 =====
    public ObservableCollection<string> LogEntries { get; } = new();
    public ObservableCollection<ReceivedFileItem> ReceivedFiles { get; } = new();

    // ===== 명령 =====
    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand DisconnectCommand { get; }
    public RelayCommand BrowseCommand { get; }
    public RelayCommand BrowseSaveFolderCommand { get; }
    public RelayCommand ClearTraceCommand { get; }
    public RelayCommand ScanCommand { get; }
    public AsyncRelayCommand SendCommand { get; }

    private void ScanChannels()
    {
        var list = CanChannelScanner.Scan(SelectedHardware, out var diag);
        AvailableChannels.Clear();
        foreach (var ch in list) AvailableChannels.Add(ch);
        SelectedChannel = AvailableChannels.FirstOrDefault(c => c.IsAvailable) ?? AvailableChannels.FirstOrDefault();

        if (!string.IsNullOrEmpty(diag))
            AppendLog($"[{SelectedHardware}] 채널 스캔: {diag}");
        else
            AppendLog($"[{SelectedHardware}] 채널 {AvailableChannels.Count}개 발견");
        RefreshCommands();
    }

    private async Task ConnectAsync()
    {
        IsBusy = true;
        try
        {
            if (!TryParseHex(TxCanIdHex, out uint forwardId) || !TryParseHex(RxCanIdHex, out uint returnId))
            {
                AppendLog("CAN ID 형식 오류 (16진수로 입력하세요).");
                return;
            }

            // ID 범위 검증: 표준 11-bit ≤ 0x7FF, 확장 29-bit ≤ 0x1FFFFFFF.
            uint maxId = ExtendedId ? 0x1FFFFFFFu : 0x7FFu;
            if (forwardId > maxId || returnId > maxId)
            {
                AppendLog($"CAN ID 범위 초과: {(ExtendedId ? "확장 29-bit(≤0x1FFFFFFF)" : "표준 11-bit(≤0x7FF)")} 범위로 입력하세요.");
                return;
            }

            int.TryParse(DataBitrate, out int dataBr);

            // 역할에 따라 송/수신 ID 방향 자동 배정 (양쪽에 동일한 ID 쌍을 넣어도 동작).
            //  forwardId = 송신처→수신처,  returnId = 수신처→송신처
            uint localTx, localRx;
            if (SelectedRole == TransferRole.Receiver)
            {
                localTx = returnId;   // 수신처는 응답(R→S)으로 송신
                localRx = forwardId;  // 데이터(S→R)를 수신
            }
            else // Sender / LoopbackDemo
            {
                localTx = forwardId;
                localRx = returnId;
            }

            var settings = new ConnectionSettings
            {
                Role = SelectedRole,
                Hardware = SelectedHardware,
                ChannelIndex = SelectedChannel?.ChannelIndex ?? 0,
                VectorChannelMask = SelectedChannel?.VectorChannelMask ?? 0,
                PeakHandle = SelectedChannel?.PeakHandle ?? 0,
                TxCanId = localTx,
                RxCanId = localRx,
                ExtendedId = ExtendedId,
                NominalBitrate = 1_000_000,
                DataBitrate = dataBr <= 0 ? 8_000_000 : dataBr,
                SaveDirectory = string.IsNullOrWhiteSpace(SaveDirectory) ? _saveDirectory : SaveDirectory.Trim(),
                SaveFileNameOverride = SaveFileNameOverride,
                EnableTracing = EnableTracing
            };

            _connection = new CanConnection(settings);
            _connection.Log += (_, m) => RunOnUi(() => AppendLog(m));
            _connection.FrameTraced += (_, line) => _traceBuffer.Enqueue(line);
            _connection.SendProgress += (_, p) => RunOnUi(() =>
            {
                SendPercent = p.Percentage;
                SendStatus = FormatProgress(p);
            });
            _connection.ReceiveProgress += (_, p) => RunOnUi(() =>
            {
                ReceivePercent = p.Percentage;
                ReceiveStatus = FormatProgress(p);
            });
            _connection.FileReceived += (_, e) => RunOnUi(() =>
                ReceivedFiles.Insert(0, new ReceivedFileItem(e.FileName, e.SavedPath,
                    CanConnection.FormatBytes(e.TotalBytes), e.HashVerified)));

            await _connection.ConnectAsync();
            IsConnected = true;
            AppendLog($"적용된 CAN ID → 내 송신(TX)=0x{localTx:X}, 내 수신(RX)=0x{localRx:X}");
            ConnectionStatus = SelectedRole switch
            {
                TransferRole.Sender => $"연결됨 · 송신처 ({SelectedHardware})",
                TransferRole.Receiver => $"연결됨 · 수신처 ({SelectedHardware})",
                _ => "연결됨 · 루프백 데모"
            };
        }
        catch (Exception ex)
        {
            AppendLog($"연결 실패: {ex.Message}");
            if (_connection is not null) { await _connection.DisposeAsync(); _connection = null; }
        }
        finally { IsBusy = false; }
    }

    private async Task DisconnectAsync()
    {
        IsBusy = true;
        try
        {
            if (_connection is not null) { await _connection.DisposeAsync(); _connection = null; }
            IsConnected = false;
            ConnectionStatus = "연결 안 됨";
            SendPercent = ReceivePercent = 0;
            SendStatus = ReceiveStatus = "";
            AppendLog("연결 해제됨.");
        }
        finally { IsBusy = false; }
    }

    private void Browse()
    {
        var dlg = new OpenFileDialog
        {
            Title = "전송할 파일 선택",
            Filter = "압축 파일 (*.zip;*.7z;*.rar)|*.zip;*.7z;*.rar|모든 파일 (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
            SelectedFilePath = dlg.FileName;
    }

    private void BrowseSaveFolder()
    {
        var dlg = new OpenFolderDialog { Title = "수신 파일 저장 폴더 선택" };
        if (Directory.Exists(SaveDirectory)) dlg.InitialDirectory = SaveDirectory;
        if (dlg.ShowDialog() == true)
            SaveDirectory = dlg.FolderName;
    }

    private void UpdateFileSummary()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(SelectedFilePath) || !File.Exists(SelectedFilePath))
            {
                FileSummary = "";
                return;
            }

            var fi = new FileInfo(SelectedFilePath);
            string ext = fi.Extension.TrimStart('.').ToUpperInvariant();
            string type = FriendlyType(fi.Extension);
            int chunkSize = 4096;
            long chunks = (fi.Length + chunkSize - 1) / chunkSize;

            FileSummary =
                $"파일명: {fi.Name}\n" +
                $"형식: {type}{(string.IsNullOrEmpty(ext) ? "" : $" (.{ext})")}\n" +
                $"크기: {CanConnection.FormatBytes(fi.Length)}  ({fi.Length:N0} bytes)\n" +
                $"수정일: {fi.LastWriteTime:yyyy-MM-dd HH:mm:ss}\n" +
                $"예상 청크: {chunks:N0}개 (4 KB/청크 기준)";
        }
        catch (Exception ex)
        {
            FileSummary = $"파일 정보를 읽을 수 없습니다: {ex.Message}";
        }
    }

    private static string FriendlyType(string extension) => extension.ToLowerInvariant() switch
    {
        ".zip" => "ZIP 압축 파일",
        ".7z" => "7-Zip 압축 파일",
        ".rar" => "RAR 압축 파일",
        ".tar" => "TAR 아카이브",
        ".gz" or ".gzip" => "GZip 압축 파일",
        ".bin" => "바이너리 파일",
        ".hex" => "Intel HEX 파일",
        ".pdf" => "PDF 문서",
        ".txt" or ".log" => "텍스트 파일",
        ".csv" => "CSV 데이터",
        "" => "확장자 없음",
        _ => "일반 파일"
    };

    private void FlushTrace()
    {
        if (_traceBuffer.IsEmpty) return;
        int budget = 300; // 틱당 최대 반영 수
        while (budget-- > 0 && _traceBuffer.TryDequeue(out var line))
            TraceEntries.Insert(0, line);
        while (TraceEntries.Count > 1000) TraceEntries.RemoveAt(TraceEntries.Count - 1);
    }

    private async Task SendAsync()
    {
        if (_connection is null) return;
        IsBusy = true;
        SendPercent = 0;
        try
        {
            await _connection.SendFileAsync(SelectedFilePath);
            SendPercent = 100;
        }
        catch (Exception ex)
        {
            AppendLog($"전송 실패: {ex.Message}");
        }
        finally { IsBusy = false; }
    }

    private void RefreshCommands()
    {
        ConnectCommand.RaiseCanExecuteChanged();
        DisconnectCommand.RaiseCanExecuteChanged();
        BrowseCommand.RaiseCanExecuteChanged();
        BrowseSaveFolderCommand.RaiseCanExecuteChanged();
        ScanCommand.RaiseCanExecuteChanged();
        SendCommand.RaiseCanExecuteChanged();
    }

    private void AppendLog(string message)
    {
        LogEntries.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");
        while (LogEntries.Count > 500) LogEntries.RemoveAt(LogEntries.Count - 1);
    }

    private static string FormatProgress(FileTransferProgress p)
        => $"{p.Percentage:0.0}%  {CanConnection.FormatBytes(p.BytesTransferred)} / {CanConnection.FormatBytes(p.TotalBytes)}"
           + $"  ·  {CanConnection.FormatBytes((long)p.BytesPerSecond)}/s"
           + (p.Eta > TimeSpan.Zero ? $"  ·  남은시간 {p.Eta:mm\\:ss}" : "");

    private static bool TryParseHex(string s, out uint value)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private static void RunOnUi(Action action)
    {
        var app = Application.Current;
        if (app is null) { action(); return; }
        if (app.Dispatcher.CheckAccess()) action();
        else app.Dispatcher.Invoke(action);
    }
}

public sealed record ReceivedFileItem(string FileName, string SavedPath, string Size, bool HashVerified)
{
    public string HashStatus => HashVerified ? "✓ 검증됨" : "✗ 불일치";
}
