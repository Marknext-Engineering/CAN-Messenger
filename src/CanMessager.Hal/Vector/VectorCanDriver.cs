// =====================================================================
//  VectorCanDriver — Vector XL Driver Library 기반 CAN-FD 드라이버 (ICanDriver)
//  개요   : vxlapi64.dll로 채널(마스크) 포트 오픈 → FD 설정 → 활성화 → 송수신.
//  핵심   : - OpenAsync : xlOpenPort(init access)→xlCanFdSetConfiguration→xlActivateChannel
//           - SendFrameAsync : xlCanTransmitEx(EDL/BRS), 큐풀 시 스핀 재시도
//           - ReceiveLoop : xlSetNotification 핸들 + WaitForSingleObject, xlCanReceive 드레인
//           - 자기 TX 확인(TX_OK) 이벤트는 무시, RX_OK만 상위로 전달
//  추후 개선:
//           - FD 타이밍(BuildFdConf)은 80MHz 가정 — 다른 클럭/임의 비트레이트는 계산 필요
//           - XLportHandle을 int로 가정(Win32 long=32bit) — vxlapi 버전 변화 시 검증
//           - 다채널 동시 사용/타임스탬프 동기화(xlSetTimerRate) 미사용
// =====================================================================
using System.Runtime.InteropServices;
using static CanMessager.Hal.Vector.VectorInterop;

namespace CanMessager.Hal.Vector;

/// <summary>
/// Vector XL Driver Library 기반 CAN-FD 드라이버.
/// 채널은 채널 마스크(CanDriverConfig.NativeChannelId)로 지정한다.
/// </summary>
public sealed class VectorCanDriver : ICanDriver
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    private int _port = XL_INVALID_PORTHANDLE;
    private ulong _accessMask;
    private nint _notifyHandle;
    private volatile bool _open;
    private volatile bool _running;
    private Thread? _rxThread;

    public bool IsOpen => _open;

    public event EventHandler<CanFdFrameReceivedEventArgs>? FrameReceived;

    public Task OpenAsync(CanDriverConfig config, CancellationToken ct = default)
    {
        if (_open) return Task.CompletedTask;

        _accessMask = config.NativeChannelId;
        if (_accessMask == 0)
            throw new InvalidOperationException("Vector 채널 마스크가 지정되지 않았습니다 (스캔에서 채널을 선택하세요).");

        VectorNative.EnsureRegistered();
        Check(xlOpenDriver(), "xlOpenDriver");

        ulong permissionMask = _accessMask; // init access 요청
        int st = xlOpenPort(ref _port, "CanMessager", _accessMask, ref permissionMask,
            524288, XL_INTERFACE_VERSION_V4, XL_BUS_TYPE_CAN);
        if (st != XL_SUCCESS || _port == XL_INVALID_PORTHANDLE)
            throw new InvalidOperationException($"xlOpenPort 실패: {ErrorText(st)}");

        bool hasInit = (permissionMask & _accessMask) == _accessMask;
        if (!hasInit)
        {
            xlClosePort(_port); _port = XL_INVALID_PORTHANDLE;
            throw new InvalidOperationException(
                "채널 초기화 권한(init access)을 얻지 못했습니다. 다른 앱(CANoe 등)이 채널을 점유 중인지 확인하세요.");
        }

        var fd = BuildFdConf(config.NominalBitrate, config.DataBitrate);
        st = xlCanFdSetConfiguration(_port, _accessMask, ref fd);
        if (st != XL_SUCCESS)
        {
            xlClosePort(_port); _port = XL_INVALID_PORTHANDLE;
            throw new InvalidOperationException($"xlCanFdSetConfiguration 실패: {ErrorText(st)}");
        }

        // 수신 알림 핸들.
        _notifyHandle = nint.Zero;
        xlSetNotification(_port, ref _notifyHandle, 1);

        st = xlActivateChannel(_port, _accessMask, XL_BUS_TYPE_CAN, XL_ACTIVATE_RESET_CLOCK);
        if (st != XL_SUCCESS)
        {
            xlClosePort(_port); _port = XL_INVALID_PORTHANDLE;
            throw new InvalidOperationException($"xlActivateChannel 실패: {ErrorText(st)}");
        }

        _open = true;
        _running = true;
        _rxThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "VectorRx" };
        _rxThread.Start();
        return Task.CompletedTask;
    }

    public Task CloseAsync()
    {
        if (!_open) return Task.CompletedTask;
        _running = false;
        _rxThread?.Join(1000);
        if (_port != XL_INVALID_PORTHANDLE)
        {
            xlDeactivateChannel(_port, _accessMask);
            xlClosePort(_port);
            _port = XL_INVALID_PORTHANDLE;
        }
        xlCloseDriver();
        _open = false;
        return Task.CompletedTask;
    }

    public Task SendFrameAsync(CanFdFrame frame, CancellationToken ct = default)
    {
        if (!_open) throw new InvalidOperationException("드라이버가 열려있지 않습니다.");

        var span = frame.Data.Span;
        var ev = new XLcanTxEvent
        {
            tag = XL_CAN_EV_TAG_TX_MSG,
            canId = frame.ExtendedId ? (frame.CanId | XL_CAN_EXT_MSG_ID) : frame.CanId,
            dlc = CanFdLength.LengthToDlc(span.Length),
            reservedTx = new byte[7],
            data = new byte[XL_CAN_MAX_DATA_LEN]
        };
        uint flags = XL_CAN_TXMSG_FLAG_EDL;
        if (frame.BitRateSwitch) flags |= XL_CAN_TXMSG_FLAG_BRS;
        ev.msgFlags = flags;
        span.CopyTo(ev.data);

        var arr = new[] { ev };
        long deadline = Environment.TickCount64 + 5000;
        int spins = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            uint sent = 0;
            int st = xlCanTransmitEx(_port, _accessMask, 1, ref sent, arr);
            if (st == XL_SUCCESS && sent == 1) return Task.CompletedTask;
            if (st == XL_ERR_QUEUE_IS_FULL || (st == XL_SUCCESS && sent == 0))
            {
                if (Environment.TickCount64 > deadline)
                    throw new TimeoutException("Vector 송신 큐가 계속 가득 차 있습니다.");
                if ((spins++ & 0x7F) == 0) Thread.Yield(); else Thread.SpinWait(48);
                continue;
            }
            throw new InvalidOperationException($"xlCanTransmitEx 실패: {ErrorText(st)}");
        }
    }

    private void ReceiveLoop()
    {
        while (_running)
        {
            // 알림 핸들이 있으면 대기, 없으면 짧게 쉼.
            if (_notifyHandle != nint.Zero) WaitForSingleObject(_notifyHandle, 20);
            else Thread.Sleep(1);

            while (_running)
            {
                var ev = new XLcanRxEvent
                {
                    msgReserved1 = new byte[12],
                    msgReserved2 = new byte[5],
                    data = new byte[XL_CAN_MAX_DATA_LEN]
                };
                int st = xlCanReceive(_port, ref ev);
                if (st == XL_ERR_QUEUE_IS_EMPTY) break;
                if (st != XL_SUCCESS) break;

                if (ev.tag != XL_CAN_EV_TAG_RX_OK) continue; // 자기 TX 확인(TX_OK)/에러 등은 무시

                int len = CanFdLength.DlcToLength(ev.dlc);
                if (len < 0 || len > XL_CAN_MAX_DATA_LEN) continue;
                var data = new byte[len];
                Array.Copy(ev.data, data, len);

                bool ext = (ev.canId & XL_CAN_EXT_MSG_ID) != 0;
                bool brs = (ev.msgFlags & XL_CAN_RXMSG_FLAG_BRS) != 0;
                uint id = ev.canId & (ext ? 0x1FFFFFFFu : 0x7FFu);

                var frame = new CanFdFrame(id, data, ext, fd: true, brs) { };
                var stamped = frame with { Timestamp = DateTimeOffset.UtcNow };

                try { FrameReceived?.Invoke(this, new CanFdFrameReceivedEventArgs(stamped)); }
                catch { /* 구독자 예외 무시 */ }
            }
        }
    }

    private static XLcanFdConf BuildFdConf(int nominal, int data)
    {
        // 80 MHz 클럭 기준. tseg는 time quanta, 드라이버가 brp = clock/(rate*(1+tseg1+tseg2)) 계산.
        // arbitration: 1+12+3=16 tq → 1M:brp5, 500k:brp10, 250k:brp20
        // data: 8M/4M/2M/1M → 1+7+2=10 tq, 5M → 1+5+2=8 tq
        uint dTseg1 = 7, dTseg2 = 2;
        if (data == 5_000_000) { dTseg1 = 5; dTseg2 = 2; }

        return new XLcanFdConf
        {
            arbitrationBitRate = (uint)nominal,
            sjwAbr = 1,
            tseg1Abr = 12,
            tseg2Abr = 3,
            dataBitRate = (uint)data,
            sjwDbr = 1,
            tseg1Dbr = dTseg1,
            tseg2Dbr = dTseg2
        };
    }

    private static void Check(int status, string what)
    {
        if (status != XL_SUCCESS)
            throw new InvalidOperationException($"{what} 실패: {ErrorText(status)}");
    }

    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);
}
