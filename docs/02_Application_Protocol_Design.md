# Application Protocol 설계 문서

> 문서 버전: 0.1 (Draft)
> 대상 프로젝트: CAN Messager (CAN-FD 기반 1:1 파일 송수신 Messenger)
> 작성일: 2026-06-07
> 선행 문서: [01_ISO-TP_Layer_Design.md](01_ISO-TP_Layer_Design.md)

---

## 1. 개요 (Overview)

본 문서는 ISO-TP(Transport Layer) **위에서 동작하는 애플리케이션 프로토콜**(이하 **CMAP**, *CAN Messager Application Protocol*)을 정의한다.
ISO-TP는 "가변 길이 메시지 1개"를 신뢰성 있게 전달하지만, **메시지 단위의 실패(타임아웃/Abort)** 는 막지 못한다. CMAP은 그 위에서 다음을 책임진다.

- 파일을 **청크(chunk)** 단위로 분할하여 순서대로 전송
- **윈도우(block) 기반 ACK/재전송**으로 메시지 손실 복구
- **SHA-256** 전체 무결성 검증 + (옵션) 청크별 CRC32 빠른 검증
- 핸드셰이크 / 메타데이터 교환 / 종료 절차
- 진행률·속도·예상 잔여시간 산출에 필요한 정보 제공

### 1.1 계층 관계

```
┌──────────────────────────────────────────┐
│  FileSendService / FileReceiveService      │  ← 애플리케이션 서비스
├──────────────────────────────────────────┤
│  CMAP (본 문서)                            │  ← 메시지/청크/ACK/재전송
│  - 1 CMAP PDU = 1 ISO-TP 메시지            │
├──────────────────────────────────────────┤
│  ISO-TP (IIsoTpChannel)                    │  ← 문서 01
├──────────────────────────────────────────┤
│  ICanDriver (CAN-FD 64byte frame)          │  ← HAL
└──────────────────────────────────────────┘
```

> **핵심 매핑**: **CMAP PDU 1개 = ISO-TP 메시지 1개**(`IIsoTpChannel.SendAsync` 1회 호출). 따라서 CMAP은 프레임 분할을 신경 쓰지 않고 "메시지"만 다룬다.

---

## 2. 설계 결정 사항 (요약)

| 항목 | 결정 | 비고 |
|---|---|---|
| PDU = ISO-TP 메시지 | 1:1 | CMAP은 프레임을 직접 다루지 않음 |
| 청크 크기 (ChunkSize) | 기본 **4096 byte**, 협상 가능 (512 ~ 4 MiB-헤더) | 한 청크 = 한 DATA 메시지 |
| 재전송 단위 | **청크(chunk)** | NAK 비트맵으로 누락 청크만 재전송 |
| 흐름 제어 | **블록(윈도우) ACK** | sender가 WindowSize 만큼 보내고 ACK 대기 |
| 전체 무결성 | **SHA-256** (파일 전체) | META에 명시, FIN 후 수신측 검증 |
| 청크 무결성 (옵션) | CRC32 (per chunk) | Flags로 on/off |
| 엔디안 | **Big-Endian** | ISO-TP/자동차 관행과 일치 |
| 문자 인코딩 | **UTF-8** | 파일명 등 |
| 이어받기(Resume) | META_ACK의 ResumeChunk로 지원 | 선택 사용 |

---

## 3. 공통 PDU 헤더

모든 CMAP PDU는 공통 8-byte 고정 헤더로 시작한다.

```
Offset  Size  Field        설명
------  ----  -----------  ----------------------------------------
0       2     Magic        0x43 0x4D  ("CM")
2       1     Version      프로토콜 버전 (현재 0x01)
3       1     MsgType      메시지 종류 (4장)
4       1     Flags        비트 플래그 (3.1)
5       1     Reserved     0x00 (정렬/확장용)
6       4     TransferId   전송 세션 식별자 (송신측이 난수 생성)
------  ----  -----------
10      ...   Body         MsgType별 가변 본문 (4장)
```

> 헤더 길이 = **10 byte**. (Magic 2 + Version 1 + MsgType 1 + Flags 1 + Reserved 1 + TransferId 4)

### 3.1 Flags 비트

| 비트 | 이름 | 의미 |
|---|---|---|
| 0 | `CHUNK_CRC` | DATA에 청크별 CRC32 포함 |
| 1 | `RESUME` | 이어받기 요청/허용 |
| 2 | `COMPRESSED` | (예약) 페이로드 추가 압축됨 |
| 3 | `ACK_REQUEST` | 이 DATA가 블록의 마지막 → 수신측은 즉시 ACK 송신 (구현 추가, 5.2 참조) |
| 4~7 | reserved | 0 |

### 3.2 TransferId
- 하나의 파일 전송 세션을 식별. 송신측이 시작 시 32-bit 난수로 생성.
- 동일 세션의 모든 PDU(META/DATA/ACK/FIN…)는 같은 TransferId를 가진다.
- 수신측은 진행 중이지 않은 TransferId의 DATA를 받으면 무시(또는 ERROR 응답).

---

## 4. 메시지 종류 (MsgType) 및 본문

| 값 | 이름 | 방향 | 설명 |
|---|---|---|---|
| 0x01 | `HELLO` | S→R | 핸드셰이크 시작, 능력 교환 |
| 0x02 | `HELLO_ACK` | R→S | 핸드셰이크 응답 |
| 0x10 | `META` | S→R | 파일 메타데이터 |
| 0x11 | `META_ACK` | R→S | 수락/거절/이어받기 위치 |
| 0x20 | `DATA` | S→R | 파일 청크 |
| 0x30 | `ACK` | R→S | 블록 수신 결과(비트맵) |
| 0x31 | `NAK` | R→S | 누락 청크 재전송 요청(비트맵) |
| 0x40 | `FIN` | S→R | 모든 청크 송신 완료 통지 |
| 0x41 | `FIN_ACK` | R→S | 전체 SHA-256 검증 결과 |
| 0x7E | `ABORT` | 양방향 | 전송 취소 |
| 0x7F | `ERROR` | 양방향 | 프로토콜 에러 통지 |

> S = Sender(송신측), R = Receiver(수신측). 모든 값은 Big-Endian.

### 4.1 HELLO (0x01)
```
Offset Size Field            설명
0      1    MaxVersion       지원 최대 프로토콜 버전
1      2    MaxIsoTpBytes    수신 가능한 ISO-TP 최대 메시지 크기
3      4    PreferredChunk   선호 청크 크기
7      2    PreferredWindow  선호 윈도우 크기(블록당 청크 수)
9      1    CapFlags         능력 비트(예: bit0=CRC 지원)
```

### 4.2 HELLO_ACK (0x02)
```
Offset Size Field            설명
0      1    AgreedVersion    합의된 버전
1      4    AgreedChunk      합의된 청크 크기
5      2    AgreedWindow     합의된 윈도우 크기
7      1    CapFlags         수신측 능력 비트
```
> 협상 규칙: `AgreedChunk = min(sender.Preferred, receiver.MaxIsoTpBytes - 헤더여유)`, `AgreedWindow = min(양측 선호)`.

### 4.3 META (0x10)
```
Offset Size Field            설명
0      4    ChunkSize        청크 크기 (HELLO_ACK 합의값과 동일)
4      8    TotalSize        파일 전체 크기 (byte)
12     4    TotalChunks      총 청크 수 = ceil(TotalSize / ChunkSize)
16     32   FileSha256       파일 전체 SHA-256
48     2    WindowSize       블록당 청크 수
50     2    FileNameLen      파일명 UTF-8 바이트 길이 (N)
52     N    FileName         파일명 (UTF-8, 경로 제외)
```

### 4.4 META_ACK (0x11)
```
Offset Size Field            설명
0      1    Status           0x00=Accept, 0x01=Reject, 0x02=ResumeAccept
1      4    ResumeChunk      Status=Resume일 때 이어받기 시작 청크 인덱스
5      1    RejectReason     Status=Reject일 때 사유 코드(5.4)
```

### 4.5 DATA (0x20)
```
Offset Size Field            설명
0      4    ChunkIndex       청크 인덱스 (0-based)
4      4    ChunkLen         이 청크 데이터 길이 (마지막 청크는 < ChunkSize 가능)
8      4    Crc32            Flags.CHUNK_CRC=1 일 때만 존재 (없으면 생략)
8/12   M    Data             청크 데이터 (M = ChunkLen)
```
> CRC32 포함 시 본문은 12+M, 미포함 시 8+M.

### 4.6 ACK (0x30) / NAK (0x31)
블록(윈도우) 단위 수신 상태를 비트맵으로 통지한다.
```
Offset Size Field            설명
0      4    BlockBaseIndex   이 블록의 첫 청크 인덱스
4      2    BitCount         비트맵이 표현하는 청크 수 (≤ WindowSize)
6      K    Bitmap           K = ceil(BitCount/8) byte
```
- **ACK**: 비트 = 1 → 해당 청크 **정상 수신**.
- **NAK**: 비트 = 1 → 해당 청크 **재전송 필요**(누락/CRC 실패).
- 비트 순서: LSB-first (bit i of byte j → 청크 BlockBaseIndex + j*8 + i).

> 구현 단순화를 위해 **ACK 한 종류만 사용**하고 sender가 "수신=1" 비트맵의 0인 청크를 재전송하는 방식을 권장한다. NAK(0x31)는 부분 손실을 명시적으로 알릴 때 사용하는 선택적 보조 수단.

### 4.7 FIN (0x40)
```
Offset Size Field            설명
0      4    TotalChunks      재확인용 총 청크 수
(본문 없음에 가까움 — 전체 해시는 META에서 이미 전달)
```

### 4.8 FIN_ACK (0x41)
```
Offset Size Field            설명
0      1    Result           0x00=Success(해시 일치), 0x01=HashMismatch, 0x02=Incomplete
1      4    MissingCount     Result≠Success일 때 누락 청크 수(참고용)
```

### 4.9 ABORT (0x7E) / ERROR (0x7F)
```
Offset Size Field            설명
0      1    Code             사유 코드 (5.4)
1      2    MsgLen           뒤따르는 메시지 길이 (N)
3      N    Message          UTF-8 사람용 설명 (디버그/로그)
```

---

## 5. 전송 절차 (Flow)

### 5.1 정상 흐름 (Happy Path)

```
Sender (S)                         Receiver (R)
   │                                   │
   │ HELLO ───────────────────────────►│
   │◄─────────────────────── HELLO_ACK │  (버전/청크/윈도우 협상)
   │                                   │
   │ META ────────────────────────────►│  (파일명, 크기, SHA-256)
   │◄─────────────────────── META_ACK  │  (Accept / Resume)
   │                                   │
   │ ── 블록 1 ──                       │
   │ DATA[0] ─────────────────────────►│
   │ DATA[1] ─────────────────────────►│
   │   ...   (WindowSize 개)            │
   │ DATA[W-1] ───────────────────────►│
   │◄───────────────────────────  ACK  │  (블록1 비트맵)
   │ (누락분 재전송 후 다음 블록)        │
   │                                   │
   │ ── 블록 N ── ... 반복 ...           │
   │                                   │
   │ FIN ─────────────────────────────►│
   │◄─────────────────────── FIN_ACK   │  (SHA-256 검증 결과)
   │                                   │
  완료                                완료
```

### 5.2 윈도우/블록 ACK 알고리즘

**송신측:**
1. `base = ResumeChunk`(없으면 0)부터 시작.
2. `[base, base+WindowSize)` 범위의 미확인 청크를 순서대로 DATA로 전송. **마지막 DATA에는 `ACK_REQUEST` 플래그를 세팅.**
3. ACK 수신 대기 (`T_ack` 타임아웃).
4. ACK 비트맵에서 0(미수신)인 청크를 재전송하고 다시 3으로. 모든 비트가 1이면 `base += WindowSize`.
5. `base >= TotalChunks` 가 되면 FIN 전송.

**수신측:**
1. META_ACK 후 DATA 수신.
2. 각 DATA에 대해: TransferId/ChunkIndex 검증 → (옵션) CRC32 검증 → 파일 오프셋에 기록 → 수신 비트맵에 마크.
3. **`ACK_REQUEST` 플래그가 있는 DATA를 받으면** 현재 블록 `[blockBase, blockBase+WindowSize)`의 비트맵을 ACK로 전송. 블록이 모두 수신되었으면 `blockBase += WindowSize`로 전진(송신측 `base`와 lockstep).
4. FIN 수신 시 전체 SHA-256 계산 → FIN_ACK(결과) 전송.

> **구현 비고**: 설계 초안의 "T_block 정적 대기" 대신 `ACK_REQUEST` 플래그 기반 트리거를 채택했다. 결정적(deterministic)이며 불필요한 대기 지연이 없다. ACK_REQUEST DATA 자체가 유실되면 송신측 `T_ack` 타임아웃으로 재전송된다.

> **재전송 정책 (ISO-TP 문서의 Open Question #2 해소)**: ISO-TP 메시지(=청크 1개)가 실패(Abort/타임아웃)하면 해당 청크는 수신 비트맵에 마크되지 않으므로, **블록 ACK 단계에서 자동으로 재전송 대상**이 된다. 별도의 즉시 재전송 로직 불필요.

### 5.3 타임아웃 / 재시도 파라미터

| 파라미터 | 의미 | 기본값 |
|---|---|---|
| `T_hello` | HELLO_ACK 대기 | 3,000 ms |
| `T_meta` | META_ACK 대기 | 3,000 ms |
| `T_ack` | 블록 ACK 대기 | 5,000 ms |
| `T_block` | 수신측 ACK 송신 전 최대 대기 | 1,000 ms |
| `T_fin` | FIN_ACK 대기 | 5,000 ms |
| `MaxRetriesPerBlock` | 블록 재전송 최대 횟수 | 5 |
| `MaxHandshakeRetries` | HELLO/META 재시도 | 3 |

> 청크별 ISO-TP 타임아웃(N_Bs/N_Cr)은 문서 01에서 처리. CMAP 타임아웃은 그보다 길게 설정.

### 5.4 사유 코드 (Reason / Reject / Error Code)

| 코드 | 이름 | 설명 |
|---|---|---|
| 0x00 | `None` | 정상 |
| 0x01 | `UnsupportedVersion` | 버전 불일치 |
| 0x02 | `FileTooLarge` | 수신측 용량 부족 |
| 0x03 | `Busy` | 수신측이 다른 전송 진행 중 |
| 0x04 | `UserRejected` | 사용자가 수신 거절 |
| 0x05 | `HashMismatch` | 전체 해시 불일치 |
| 0x06 | `ChunkCrcError` | 청크 CRC 반복 실패 |
| 0x07 | `Timeout` | 타임아웃 |
| 0x08 | `Canceled` | 사용자 취소 |
| 0x09 | `ProtocolError` | 헤더/포맷 오류 |

---

## 6. 상태머신

### 6.1 송신측 (Sender)

```
IDLE
  │ StartSend(file)
  ▼
HANDSHAKE ── HELLO → HELLO_ACK ──► META_EXCHANGE
  │ (timeout×N → ERROR)
  ▼
META_EXCHANGE ── META → META_ACK ──► SENDING
  │ Reject → ABORTED
  ▼
SENDING (블록 루프)
  │  블록 전송 → ACK → 누락 재전송 → 다음 블록
  │  재시도 초과 → ABORTED
  ▼
FINISHING ── FIN → FIN_ACK ──►
  │ Success → DONE
  │ HashMismatch/Incomplete → (재전송 or ABORTED)
  ▼
DONE
```

### 6.2 수신측 (Receiver)

```
IDLE
  │ HELLO 수신
  ▼
HANDSHAKE → HELLO_ACK 송신
  ▼
META_WAIT ── META 수신 → (사용자/정책 확인) → META_ACK
  │ 거절 → IDLE
  ▼
RECEIVING (DATA 수신 + 블록 ACK)
  │  파일 기록, 비트맵 갱신
  ▼
VERIFYING ── FIN 수신 → SHA-256 계산
  │ 일치 → FIN_ACK(Success) → DONE
  │ 불일치/누락 → FIN_ACK(실패) → (재전송 대기 or ABORTED)
  ▼
DONE
```

---

## 7. 파일 처리 세부

### 7.1 청크 분할
- `TotalChunks = ceil(TotalSize / ChunkSize)`
- 청크 i의 파일 오프셋 = `i * ChunkSize`
- 마지막 청크 길이 = `TotalSize - (TotalChunks-1)*ChunkSize`

### 7.2 무결성
- **전체**: 송신측이 전송 전 파일 전체 SHA-256 계산 → META에 기록. 수신측은 FIN 시점에 기록된 파일로 SHA-256 재계산 후 비교.
- **청크(옵션)**: `CHUNK_CRC` 플래그 시 DATA에 CRC32 포함. 수신측이 즉시 검증, 실패 시 해당 청크를 비트맵에 마크하지 않음(→ 재전송 유발).

### 7.3 디스크 기록
- 수신측은 임시 파일(`*.cmpart`)에 기록하고, 검증 성공(FIN_ACK Success) 후 최종 파일명으로 rename.
- 이어받기: 임시 파일 크기/수신 비트맵을 보존하면 `RESUME`로 중단 지점부터 재개 가능.

---

## 8. C# API 설계 (예고)

다음 구현 단계에서 작성할 타입 (Core 프로젝트):

```csharp
namespace CanMessager.Core.Application;

public sealed record CmapHeader(byte Version, CmapMsgType Type, byte Flags, uint TransferId);

public enum CmapMsgType : byte
{
    Hello = 0x01, HelloAck = 0x02,
    Meta = 0x10, MetaAck = 0x11,
    Data = 0x20, Ack = 0x30, Nak = 0x31,
    Fin = 0x40, FinAck = 0x41,
    Abort = 0x7E, Error = 0x7F
}

public interface IFileSendService
{
    Task SendFileAsync(string path, IProgress<FileTransferProgress>? progress, CancellationToken ct);
}

public interface IFileReceiveService
{
    event EventHandler<IncomingFileEventArgs> IncomingFile;     // 수락/거절 콜백
    event EventHandler<FileTransferProgress>  Progress;
    event EventHandler<FileReceivedEventArgs>  FileReceived;
}

public sealed record FileTransferProgress(
    uint TransferId, string FileName,
    long BytesTransferred, long TotalBytes,
    double BytesPerSecond, TimeSpan Eta);
```

각 PDU의 인코딩/디코딩은 `CmapCodec`(static)로 구현하고, ISO-TP의 `IsoTpPci`와 동일한 스타일(span 기반)으로 작성한다.

---

## 9. 미해결 / 추후 결정 사항 (Open Questions)

1. **ChunkSize 기본값 튜닝**: 4096 byte가 적정한가? ISO-TP 메시지가 클수록 프레임당 오버헤드는 줄지만 한 번의 실패 시 재전송 비용이 커진다. 실측(HIL) 후 결정.
2. **WindowSize 기본값**: 블록당 청크 수. 메모리(수신 비트맵·미확인 버퍼) vs ACK 빈도 트레이드오프. 제안: 32.
3. **양방향 동시 전송**: 현재 ISO-TP 채널은 송/수신 각 1개. CMAP은 한 방향 파일 전송을 가정. 동시 양방향 파일 전송은 범위 외(추후).
4. **ACK 단독 vs NAK 병용**: 5.2 권장(ACK 비트맵 단독). NAK 실제 사용 여부 확정 필요.
5. **압축(COMPRESSED 플래그)**: ZIP은 이미 압축본 → 추가 압축 의미 적음. 일반 파일 대비용으로 둘지 결정.

---

## 10. 참고
- 선행 문서: [01_ISO-TP_Layer_Design.md](01_ISO-TP_Layer_Design.md)
- ISO 15765-2:2016
- CRC-32 (IEEE 802.3) / SHA-256 (FIPS 180-4)
