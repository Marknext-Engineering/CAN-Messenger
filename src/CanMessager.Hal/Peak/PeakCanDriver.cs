// =====================================================================
//  PeakCanDriver — PEAK PCAN-Basic 기반 CAN-FD 드라이버 (ICanDriver 구현)
//  개요   : PCANBasic.dll(64-bit)로 채널 InitializeFD → 송신/수신.
//  핵심   : - OpenAsync : InitializeFD(비트레이트 문자열) + PCAN_RECEIVE_EVENT 등록
//           - ReceiveLoop: 수신 이벤트 + 20ms 폴링 안전망으로 큐 드레인
//           - SendFrameAsync: WriteFD, 큐 풀(QXMTFULL) 시 스핀 재시도(처리량 유지)
//  추후 개선:
//           - 비트레이트는 PeakBitrate 프리셋(80MHz) 기반 — 임의 조합은 동적 계산 필요
//           - SetValue 수신이벤트 핸들을 32-bit로 전달(공식 샘플 방식) — 항상 안전한지 주의
//           - 에러 프레임/버스오류 카운터 노출 미구현(진단 강화 여지)
// =====================================================================
using static CanMessager.Hal.Peak.PeakInterop;

namespace CanMessager.Hal.Peak;

/// <summary>
/// PEAK PCAN-Basic 기반 CAN-FD 드라이버. PCANBasic.dll(64-bit)을 사용한다.
/// 수신은 PCAN_RECEIVE_EVENT + 백그라운드 스레드로 처리한다.
/// </summary>
public sealed class PeakCanDriver : ICanDriver
{
    private ushort _handle;
    private volatile bool _open;
    private Thread? _rxThread;
    private AutoResetEvent? _rxEvent;
    private volatile bool _running;

    public bool IsOpen => _open;

    public event EventHandler<CanFdFrameReceivedEventArgs>? FrameReceived;

    public Task OpenAsync(CanDriverConfig config, CancellationToken ct = default)
    {
        if (_open) return Task.CompletedTask;

        _handle = config.NativeChannelId != 0
            ? (ushort)config.NativeChannelId
            : (ushort)(PCAN_USBBUS1 + config.ChannelIndex);

        string bitrate = config.FdBitrateString
                         ?? PeakBitrate.Get(config.NominalBitrate, config.DataBitrate, out _);

        uint st = CAN_InitializeFD(_handle, bitrate);
        if (st != PCAN_ERROR_OK)
            throw new InvalidOperationException(
                $"PCAN 채널 0x{_handle:X2} 초기화 실패: {ErrorText(st)}");

        // 수신 이벤트 등록 (managed AutoResetEvent 핸들 사용).
        _rxEvent = new AutoResetEvent(false);
        uint evtHandle = (uint)_rxEvent.SafeWaitHandle.DangerousGetHandle().ToInt32();
        st = CAN_SetValue(_handle, PCAN_RECEIVE_EVENT, ref evtHandle, sizeof(uint));
        if (st != PCAN_ERROR_OK)
        {
            CAN_Uninitialize(_handle);
            _rxEvent.Dispose();
            _rxEvent = null;
            throw new InvalidOperationException($"PCAN 수신 이벤트 설정 실패: {ErrorText(st)}");
        }

        _open = true;
        _running = true;
        _rxThread = new Thread(ReceiveLoop) { IsBackground = true, Name = $"PeakRx-0x{_handle:X2}" };
        _rxThread.Start();
        return Task.CompletedTask;
    }

    public Task CloseAsync()
    {
        if (!_open) return Task.CompletedTask;
        _running = false;
        _rxEvent?.Set(); // 스레드 깨우기
        _rxThread?.Join(1000);
        CAN_Uninitialize(_handle);
        _rxEvent?.Dispose();
        _rxEvent = null;
        _open = false;
        return Task.CompletedTask;
    }

    public Task SendFrameAsync(CanFdFrame frame, CancellationToken ct = default)
    {
        if (!_open) throw new InvalidOperationException("드라이버가 열려있지 않습니다.");

        var data = frame.Data.Span;
        var msg = new TPCANMsgFD
        {
            ID = frame.CanId,
            DLC = CanFdLength.LengthToDlc(data.Length),
            DATA = new byte[64]
        };
        data.CopyTo(msg.DATA);

        byte type = PCAN_MESSAGE_FD;
        if (frame.ExtendedId) type |= PCAN_MESSAGE_EXTENDED;
        if (frame.BitRateSwitch) type |= PCAN_MESSAGE_BRS;
        msg.MSGTYPE = type;

        // 송신 큐가 가득 차면(QXMTFULL) 스핀하며 자리가 나는 즉시 재시도한다.
        // (Thread.Sleep(1)은 Windows 타이머 해상도상 ~1~15ms 지연 → 8Mbit/s 처리량을 크게 깎으므로 사용하지 않음)
        long deadline = Environment.TickCount64 + 5000; // 5초 안전 타임아웃
        int spins = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            uint st = CAN_WriteFD(_handle, ref msg);
            if (st == PCAN_ERROR_OK) return Task.CompletedTask;
            if ((st & PCAN_ERROR_QXMTFULL) != 0)
            {
                if (Environment.TickCount64 > deadline)
                    throw new TimeoutException("PCAN 송신 큐가 계속 가득 차 있습니다 (QXMTFULL).");
                // 가끔만 양보하고 대부분은 짧게 스핀 → 큐에 자리 나는 즉시 채워 버스를 포화 유지.
                if ((spins++ & 0x7F) == 0) Thread.Yield();
                else Thread.SpinWait(48);
                continue;
            }
            throw new InvalidOperationException($"PCAN 송신 실패: {ErrorText(st)}");
        }
    }

    private void ReceiveLoop()
    {
        while (_running)
        {
            if (_rxEvent is null) break;
            // 수신 이벤트로 깨어나되, 신호가 없어도 20ms마다 폴링하여 큐를 비운다(안전망).
            _rxEvent.WaitOne(20);

            // 이벤트 1회에 여러 프레임이 쌓일 수 있으므로 빌 때까지 모두 읽는다.
            while (_running)
            {
                uint st = CAN_ReadFD(_handle, out var msg, out _);
                if ((st & PCAN_ERROR_QRCVEMPTY) != 0) break;
                if (st != PCAN_ERROR_OK) break;

                // 상태/에러/에코 프레임은 무시.
                if ((msg.MSGTYPE & (PCAN_MESSAGE_STATUS | PCAN_MESSAGE_ERRFRAME | PCAN_MESSAGE_ECHO)) != 0)
                    continue;

                int len = CanFdLength.DlcToLength(msg.DLC);
                var data = new byte[len];
                Array.Copy(msg.DATA, data, len);

                bool ext = (msg.MSGTYPE & PCAN_MESSAGE_EXTENDED) != 0;
                bool brs = (msg.MSGTYPE & PCAN_MESSAGE_BRS) != 0;
                uint id = msg.ID & (ext ? 0x1FFFFFFFu : 0x7FFu);

                var frame = new CanFdFrame(id, data, ext, fd: true, brs)
                {
                    // 수신 타임스탬프.
                };
                var stamped = frame with { Timestamp = DateTimeOffset.UtcNow };

                try { FrameReceived?.Invoke(this, new CanFdFrameReceivedEventArgs(stamped)); }
                catch { /* 구독자 예외가 수신 루프를 멈추지 않도록 */ }
            }
        }
    }

    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);
}
