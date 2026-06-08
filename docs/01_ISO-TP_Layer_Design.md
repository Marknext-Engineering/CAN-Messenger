# ISO-TP Layer 설계 문서

> 문서 버전: 0.1 (Draft)
> 대상 프로젝트: CAN Messager (CAN-FD 기반 1:1 파일 송수신 Messenger)
> 작성일: 2026-06-07

---

## 1. 개요 (Overview)

본 문서는 CAN Messager의 **Transport Layer**인 ISO-TP(ISO 15765-2) 구현 설계를 정의한다.
상위 애플리케이션 프로토콜은 ISO-TP가 제공하는 **신뢰성 있는 가변 길이 메시지 전송** 기능을 사용하여 ZIP 등 대용량 파일 청크를 송수신한다.

### 1.1 범위
- **포함**: CAN-FD 전용 ISO-TP (ISO 15765-2:2016) 구현, Normal addressing, Sender/Receiver 상태머신, Flow Control, 타이밍 파라미터, 에러 처리.
- **제외**: Classic CAN(8 byte) 지원, Extended/Mixed/Functional addressing, Remote Diagnostic Service 관련 기능.

### 1.2 핵심 결정 사항
| 항목 | 결정 |
|---|---|
| 기반 표준 | ISO 15765-2:2016 (CAN-FD 확장 포함) |
| 프레임 페이로드 | CAN-FD 최대 **64 byte/frame** (DLC = 15) |
| Addressing | **Normal Addressing** (송신/수신 CAN ID 분리) |
| 최대 메시지 길이 | **최대 4 GiB** (Escape Sequence 사용: 32-bit length) |
| 동시 메시지 | 단일 채널당 1 송신 + 1 수신 (Half-duplex per direction) |

---

## 2. ISO-TP 프레임 타입

ISO-TP는 4가지 PCI(Protocol Control Information) 타입을 사용한다.

### 2.1 Frame Type 요약

| Type | PCI | 용도 |
|---|---|---|
| SF (Single Frame) | `0x0` | 단일 프레임에 들어가는 짧은 메시지 |
| FF (First Frame) | `0x1` | 멀티 프레임 전송의 첫 프레임 (전체 길이 포함) |
| CF (Consecutive Frame) | `0x2` | 멀티 프레임의 이어지는 프레임 |
| FC (Flow Control) | `0x3` | 수신측 → 송신측 흐름 제어 |

### 2.2 CAN-FD에서의 페이로드 구조

CAN-FD는 DLC가 9~15일 때 페이로드가 12/16/20/24/32/48/**64** byte. 본 프로젝트는 항상 **64 byte (DLC=15)** 를 사용한다 (FF, CF에 한함). SF/FC는 데이터 길이에 따라 가변 DLC 사용 가능.

#### 2.2.1 Single Frame (SF) — CAN-FD

데이터 길이에 따라 두 가지 포맷:

**SF (≤ 7 byte 데이터)** — 1-byte PCI
```
Byte 0:  [0x0 : 4bit][SF_DL : 4bit]   // SF_DL = 1..7
Byte 1..: Data
```

**SF (8 ~ 62 byte 데이터, CAN-FD escape)** — 2-byte PCI
```
Byte 0:  [0x0 : 4bit][0x0 : 4bit]     // SF_DL = 0 → escape
Byte 1:  SF_DL (1 byte, 1..62)
Byte 2..: Data
```

#### 2.2.2 First Frame (FF) — CAN-FD

전체 메시지 길이가 4095 byte 이하인 경우와 그 이상인 경우가 분리됨.

**FF (length ≤ 4095)** — 2-byte PCI
```
Byte 0:  [0x1 : 4bit][FF_DL_hi : 4bit]
Byte 1:  FF_DL_lo (8bit)
Byte 2..: Data (최대 62 byte)
```

**FF (length > 4095, escape)** — 6-byte PCI
```
Byte 0:  [0x1 : 4bit][0x0 : 4bit]
Byte 1:  0x00
Byte 2..5: FF_DL (32bit big-endian, 4096 .. 2^32-1)
Byte 6..: Data (최대 58 byte)
```

> **본 프로젝트는 대용량 파일을 다루므로 사실상 모든 FF는 32-bit length escape 형식을 사용한다.**

#### 2.2.3 Consecutive Frame (CF) — CAN-FD

```
Byte 0:  [0x2 : 4bit][SN : 4bit]      // SN = Sequence Number (0..15, wrap)
Byte 1..: Data (최대 63 byte)
```

- 첫 CF는 SN=1로 시작, 이후 1씩 증가하며 15 다음은 0으로 wrap.
- 마지막 CF는 페이로드 패딩 필요 (선택). 본 프로젝트는 **패딩 바이트 = 0xCC** 사용.

#### 2.2.4 Flow Control (FC)

```
Byte 0:  [0x3 : 4bit][FS : 4bit]      // FS = Flow Status
Byte 1:  BS                            // Block Size (0 = 무제한)
Byte 2:  STmin                         // Separation Time
Byte 3..: (don't care, 패딩)
```

**FS 값:**
- `0x0` CTS (Continue To Send)
- `0x1` WAIT (송신측 대기)
- `0x2` OVFLW (수신 버퍼 오버플로우 → 전송 중단)

**STmin 인코딩:**
- `0x00 ~ 0x7F` → 0 ~ 127 ms
- `0xF1 ~ 0xF9` → 100 ~ 900 μs

---

## 3. Addressing 방식

### 3.1 Normal Addressing (채택)

각 노드는 송신용 CAN ID와 수신용 CAN ID 두 개를 가진다.

| 역할 | 노드 A | 노드 B |
|---|---|---|
| TX (송신) | `0x18DA{B}{A}` 형태 (예: 0x18DAAA01) | `0x18DA{A}{B}` 형태 (예: 0x18DA01AA) |
| RX (수신) | 노드 B의 TX와 동일 | 노드 A의 TX와 동일 |

- **CAN ID는 사용자 설정(Settings)에서 변경 가능**해야 함.
- 기본값: A → B = `0x18DA01F1`, B → A = `0x18DAF101` (J1939/UDS 관행 차용).
- 11-bit / 29-bit 둘 다 지원, 기본값은 **29-bit Extended Frame**.

### 3.2 향후 확장 여지
1:N 토폴로지가 미래에 필요해질 경우 Extended Addressing(Target Address 1 byte in payload) 또는 Mixed Addressing 추가 가능. 현재는 구현 안 함.

---

## 4. 송신측 상태머신 (Sender State Machine)

```
       ┌────────┐
       │  IDLE  │◄──────────────────────────┐
       └───┬────┘                            │
           │ SendRequest(msg)                │
           ▼                                 │
     ┌────────────┐  msg.Length ≤ 62        │
     │ Classify   ├────────────► SendSF ────┤
     └─────┬──────┘                          │
           │ msg.Length > 62                 │
           ▼                                 │
       ┌────────┐                            │
       │SendFF │──────► WaitFC               │
       └────────┘         │                  │
                          ▼                  │
                  ┌──────────────┐           │
                  │  FC received │           │
                  └──────┬───────┘           │
                FS=CTS   │   FS=WAIT → 재대기 (N_Bs)
                         │   FS=OVFLW → Abort
                         ▼                   │
                  ┌──────────────┐           │
                  │   SendCFs    │           │
                  │  (block_size │           │
                  │   에 따라)    │           │
                  └──────┬───────┘           │
                         │                   │
              남은데이터 0 ─────► Done ──────┘
              block_size 도달 ──► WaitFC
              STmin 간격 준수
```

### 4.1 송신 의사 코드 (요약)

```csharp
async Task SendAsync(byte[] payload, CancellationToken ct)
{
    if (payload.Length <= 62) {
        await SendSingleFrameAsync(payload, ct);
        return;
    }

    await SendFirstFrameAsync(payload, ct);
    var fc = await WaitFlowControlAsync(N_Bs, ct);
    HandleFc(fc); // WAIT/OVFLW 처리

    int offset = FF_DATA_SIZE; // 58 or 62
    byte sn = 1;
    int sentInBlock = 0;

    while (offset < payload.Length) {
        await SendConsecutiveFrameAsync(payload, offset, sn, ct);
        offset += CF_DATA_SIZE;     // 63
        sn = (byte)((sn + 1) & 0x0F);
        sentInBlock++;

        if (fc.BlockSize != 0 && sentInBlock == fc.BlockSize && offset < payload.Length) {
            fc = await WaitFlowControlAsync(N_Bs, ct);
            HandleFc(fc);
            sentInBlock = 0;
        } else {
            await DelaySTminAsync(fc.STmin, ct);
        }
    }
}
```

---

## 5. 수신측 상태머신 (Receiver State Machine)

```
       ┌────────┐
       │  IDLE  │◄──────────────────────┐
       └───┬────┘                        │
       SF 수신 → Deliver → IDLE          │
       FF 수신                            │
           │                              │
           ▼                              │
     ┌────────────────┐                   │
     │ AllocateBuffer │                   │
     │ Send FC(CTS)   │                   │
     └──────┬─────────┘                   │
            ▼                             │
     ┌────────────────┐                   │
     │ ReceivingCFs   │                   │
     └──────┬─────────┘                   │
            │ Last CF                     │
            ▼                             │
        Deliver ────────────────────────►─┘

타임아웃 (N_Cr 초과) → Abort + 에러 통지
SN 불일치           → Abort + 에러 통지
```

### 5.1 수신 동작 규칙
- FF 수신 시 메시지 전체 길이(FF_DL)만큼 버퍼 할당. **최대 허용 길이 (`MaxAllowedMessageLength`)** 초과 시 즉시 FC(OVFLW) 송신 후 Abort.
- 본 프로젝트는 파일 청크 단위로 메시지를 보내므로 ISO-TP 메시지 최대 길이를 **4 MiB**로 제한 (Settings에서 조정 가능).
- 마지막 CF의 패딩 바이트는 무시 (FF_DL 기준으로만 데이터 길이 판정).

---

## 6. 타이밍 파라미터

| 파라미터 | 의미 | 기본값 | 비고 |
|---|---|---|---|
| `N_As` | 송신측 CAN 송신 타임아웃 | 1000 ms | 드라이버 송신 완료 대기 |
| `N_Ar` | 수신측 CAN 송신 타임아웃 | 1000 ms | FC 송신 완료 대기 |
| `N_Bs` | 송신측 FC 수신 타임아웃 | 1000 ms | FF/CF block 후 FC 대기 |
| `N_Br` | 수신측 FC 송신 지연 한계 | 900 ms | 권장 N_Bs - 100ms |
| `N_Cs` | 송신측 CF 송신 지연 한계 | STmin 준수 | 권장 N_Cr - 100ms |
| `N_Cr` | 수신측 CF 수신 타임아웃 | 1000 ms | 다음 CF 대기 |
| `STmin` | CF 간 최소 간격 | 송신측이 FC로 지정 | 본 프로젝트 기본: 0 (최대 속도) |
| `BS` | Block Size | 송신측이 FC로 지정 | 본 프로젝트 기본: 0 (무제한) |

> **본 프로젝트 권장값**: `STmin = 0`, `BS = 0` (CAN-FD 5Mbps Data phase에서 최대 throughput 확보). 실측 후 버스 부하/에러율에 따라 조정.

---

## 7. 에러 처리

| 에러 | 트리거 | 대응 |
|---|---|---|
| `N_TIMEOUT_A` | 드라이버 송신 타임아웃 | Abort, 상위 통지 |
| `N_TIMEOUT_Bs` | FC 수신 타임아웃 | Abort |
| `N_TIMEOUT_Cr` | CF 수신 타임아웃 | Abort |
| `N_WRONG_SN` | CF Sequence Number 불일치 | Abort |
| `N_BUFFER_OVFLW` | 수신 버퍼 초과 | FC(OVFLW) 송신 후 Abort |
| `N_UNEXP_PDU` | 진행 중 예상치 못한 PCI | Abort |
| `N_ERROR` | 그 외 | Abort |

모든 에러는 상위 레이어에 `TpException` 또는 이벤트로 전달.

---

## 8. C# API 설계

### 8.1 인터페이스

```csharp
namespace CanMessager.Core.Transport;

public interface IIsoTpChannel : IAsyncDisposable
{
    IsoTpConfig Config { get; }

    // Application → Transport
    Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default);

    // Transport → Application
    event EventHandler<IsoTpMessageReceivedEventArgs>? MessageReceived;
    event EventHandler<IsoTpErrorEventArgs>? ErrorOccurred;
    event EventHandler<IsoTpProgressEventArgs>? Progress; // 송/수신 진행률

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync();
}

public sealed record IsoTpConfig(
    uint TxCanId,
    uint RxCanId,
    bool ExtendedId           = true,
    int  MaxMessageBytes      = 4 * 1024 * 1024,
    byte DefaultBlockSize     = 0,
    byte DefaultSTmin         = 0,
    int  N_As_ms              = 1000,
    int  N_Ar_ms              = 1000,
    int  N_Bs_ms              = 1000,
    int  N_Cr_ms              = 1000,
    byte PaddingByte          = 0xCC
);

public sealed class IsoTpMessageReceivedEventArgs : EventArgs
{
    public required ReadOnlyMemory<byte> Payload { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}
```

### 8.2 의존성

ISO-TP 레이어는 하위 HAL이 제공하는 `ICanDriver`에 의존한다:

```csharp
public interface ICanDriver : IAsyncDisposable
{
    Task SendFrameAsync(in CanFdFrame frame, CancellationToken ct);
    event EventHandler<CanFdFrameReceivedEventArgs> FrameReceived;
    Task OpenAsync(CanDriverConfig config, CancellationToken ct);
    Task CloseAsync();
}

public readonly record struct CanFdFrame(
    uint CanId,
    bool ExtendedId,
    bool Fd,             // 항상 true
    bool BitRateSwitch,  // BRS, 권장 true
    ReadOnlyMemory<byte> Data
);
```

### 8.3 동시성/스레딩
- 송신은 `Channel<>` 기반 큐 + 단일 워커 Task로 직렬화.
- 수신은 드라이버 콜백 스레드 → 내부 큐 → 처리 Task로 분리 (콜백 블로킹 방지).
- 모든 public API는 `async`/`CancellationToken` 지원.

---

## 9. 테스트 전략

| 레벨 | 대상 | 도구 |
|---|---|---|
| Unit | PCI 인코딩/디코딩, 상태머신 전이 | xUnit + FluentAssertions |
| Integration | `FakeCanDriver`로 양방향 루프백 | xUnit + Loopback Fixture |
| HIL | 실제 VN1630/PCAN-FD 2대 연결 | 수동 테스트 + 로그 캡처 |

### 9.1 주요 테스트 케이스
- SF 7 byte, SF 62 byte (escape) 송수신
- FF/CF 정상 흐름: 100 byte, 1 KiB, 1 MiB, 4 MiB
- FC BlockSize = 1, 8, 0(무제한) 각각
- STmin = 0, 0xF1(100us), 0x14(20ms)
- 에러: CF 누락 (N_Cr 타임아웃), SN 불일치, FF_DL > MaxMessageBytes
- 동시 송수신 (양방향)
- 송신 도중 취소 (CancellationToken)

---

## 10. 미해결 / 추후 결정 사항 (Open Questions)

1. **WAIT FC 최대 횟수 제한**: ISO 표준은 `N_WFTmax`(기본 0~10) 권장. 본 프로젝트 정책 = ? (제안: 4회 초과 시 Abort)
2. **재전송 정책**: ISO-TP는 자체 재전송 없음. 에러 시 상위 애플리케이션이 청크 단위로 재요청하는 구조 → 애플리케이션 프로토콜 문서에서 확정 필요.
3. **로깅 포맷**: 프레임 단위 trace 로그를 파일로 남길지(`.asc`/`.trc` 호환?) → 향후 결정.
4. **하드웨어 타임스탬프**: VN1630/PCAN 모두 HW 타임스탬프 제공. ISO-TP 레이어에서 활용할지 (현재는 SW 타임스탬프만 사용 가정).

---

## 11. 참고 문헌
- ISO 15765-2:2016 — Road vehicles — Diagnostic communication over Controller Area Network (DoCAN) — Part 2: Transport protocol and network layer services
- Vector Informatik, *XL Driver Library Manual* (vxlapi)
- PEAK-System, *PCAN-Basic API Documentation*
