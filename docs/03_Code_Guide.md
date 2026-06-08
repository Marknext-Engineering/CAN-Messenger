# 코드 가이드 & 추후 개선 백로그

> 프로젝트: CAN Messager (CAN-FD 기반 1:1 파일 송수신 Messenger)
> 목적: 각 소스 파일의 역할을 한눈에 파악하고, 개선 포인트를 추적한다.
> 선행 문서: [01_ISO-TP_Layer_Design.md](01_ISO-TP_Layer_Design.md), [02_Application_Protocol_Design.md](02_Application_Protocol_Design.md)
>
> ※ 핵심 로직 파일 상단에는 `// ===` 배너 주석(개요/핵심/추후 개선)이 삽입되어 있습니다.
>   본 문서는 그 전체 색인 + 통합 개선 목록입니다.

---

## 1. 아키텍처 계층

```
┌───────────────────────────────────────────────┐
│ CanMessager.UI (WPF, MVVM)                       │  화면/사용자 상호작용
│   ViewModels ─ Services(CanConnection) ─ Mvvm     │
├───────────────────────────────────────────────┤
│ CanMessager.Core                                  │  프로토콜
│   Application(CMAP) ── Transport(ISO-TP)           │
├───────────────────────────────────────────────┤
│ CanMessager.Hal                                   │  하드웨어 추상화
│   ICanDriver ─ Fake / Peak / Vector ─ Discovery   │
└───────────────────────────────────────────────┘
의존 방향: UI → Core → Hal (역참조 없음)
```

---

## 2. 파일별 요약

### 2.1 HAL (CanMessager.Hal) — 하드웨어 추상화

| 파일 | 역할 |
|---|---|
| `ICanDriver.cs` | CAN 드라이버 공통 인터페이스 (Open/Close/Send/FrameReceived) |
| `CanFdFrame.cs` | 단일 CAN-FD 프레임 불변 값 타입 (ID/확장/BRS/Data/타임스탬프) |
| `CanFdLength.cs` | CAN-FD 길이 유틸 (유효길이 Ceil, **DLC↔바이트 변환**) |
| `CanDriverConfig.cs` | 드라이버 열기 설정 (HW종류/채널/비트레이트/네이티브ID/FD문자열) |
| `CanFdFrameReceivedEventArgs.cs` | 수신 이벤트 인자 |
| `TracingCanDriver.cs` | ICanDriver 데코레이터 — 송/수신 프레임 트레이스 노출(런타임 on/off) |
| `Fake/FakeCanBus.cs` | 테스트용 가상 버스(연결된 드라이버 간 프레임 브로드캐스트) |
| `Fake/FakeCanDriver.cs` | 가상 드라이버(루프백 데모/단위테스트용) |
| `Discovery/CanChannelInfo.cs` | 채널 정보 모델(장치명/시리얼/물리채널/마스크/핸들) |
| `Discovery/ICanChannelEnumerator.cs` | 채널 열거 인터페이스 |
| `Discovery/CanChannelScanner.cs` | HW별 열거자 팩토리 + Scan 진입점 |
| `Discovery/FakeChannelEnumerator.cs` | 가상 채널 2개 반환 |
| `Discovery/PeakChannelEnumerator.cs` | PCAN_ATTACHED_CHANNELS로 PEAK 채널 스캔 |
| `Discovery/VectorChannelEnumerator.cs` | xlGetDriverConfig 파싱(오프셋 VN1640A 검증) + DumpRawConfig 진단 |
| `Peak/PeakInterop.cs` | PCANBasic.dll P/Invoke + TPCANMsgFD |
| `Peak/PeakBitrate.cs` | (nominal,data)→FD 비트레이트 문자열 프리셋(80MHz) |
| `Peak/PeakCanDriver.cs` | PEAK CAN-FD 드라이버 (수신이벤트+폴링, 송신 스핀 페이싱) |
| `Vector/VectorNative.cs` | vxlapi64.dll 동적 로더(설치폴더 자동탐색, 1회 등록) |
| `Vector/VectorInterop.cs` | vxlapi P/Invoke + XLcanFdConf/XLcanTxEvent/XLcanRxEvent |
| `Vector/VectorCanDriver.cs` | Vector CAN-FD 드라이버 (xlOpenPort→FD설정→활성화→송수신) |

### 2.2 Core — Transport (ISO-TP, 문서 01)

| 파일 | 역할 |
|---|---|
| `IIsoTpChannel.cs` | ISO-TP 채널 인터페이스(Start/Stop/Send/이벤트) |
| `IsoTpConfig.cs` | ISO-TP 설정(TX/RX ID, 타임아웃 N_Bs/N_Cr/N_A, BS, STmin, 패딩) |
| `IsoTpPci.cs` | SF/FF/CF/FC 인코딩·디코딩, STmin 해석 |
| `IsoTpEvents.cs` | 메시지 수신/에러/진행 이벤트 인자 |
| `IsoTpError.cs` | 에러 종류 enum + IsoTpException |
| `IsoTpChannel.cs` | **송/수신 상태머신 + Flow Control + 타이밍** (핵심) |

### 2.3 Core — Application (CMAP, 문서 02)

| 파일 | 역할 |
|---|---|
| `CmapProtocol.cs` | 메시지 타입/Flags/상태/사유 코드, 프로토콜 상수 |
| `CmapMessages.cs` | 디코딩된 메시지 레코드 타입(Hello/Meta/Data/Ack/…) |
| `CmapCodec.cs` | PDU 인코딩/디코딩(10-byte 헤더 + 본문, Big-Endian) |
| `Crc32.cs` | CRC-32(IEEE 802.3) — 청크 빠른 무결성 |
| `CmapConfig.cs` | CMAP 파라미터(청크크기/윈도우/타임아웃/재시도/CRC) |
| `CmapInbox.cs` | ISO-TP 수신 → CMAP PDU 큐잉/타입대기 |
| `FileTransferTypes.cs` | 진행률/수신이벤트/예외 등 공용 타입 |
| `FileSendService.cs` | **송신 절차**(HELLO→META→블록 DATA/ACK→FIN) |
| `FileReceiveService.cs` | **수신 절차**(수락정책→DATA수신/ACK→SHA-256 검증) |

### 2.4 UI (CanMessager.UI, WPF/MVVM)

| 파일 | 역할 |
|---|---|
| `App.xaml(.cs)` | 앱 진입점 + 전역 예외 핸들러(조용한 종료 방지) |
| `MainWindow.xaml(.cs)` | 메인 화면 레이아웃(역할/연결/송수신/진행/로그·트레이스) |
| `Mvvm/ObservableObject.cs` | INotifyPropertyChanged 경량 베이스 |
| `Mvvm/RelayCommand.cs` | 동기/비동기 ICommand |
| `Services/CanConnection.cs` | **스택 조립/수명 관리**(드라이버+ISO-TP+서비스) |
| `ViewModels/MainViewModel.cs` | 화면 상태/명령/바인딩 |
| `ViewModels/Converters.cs` | BoolNot / EnumKorean 표시 컨버터 |

### 2.5 도구 / 테스트

| 파일 | 역할 |
|---|---|
| `tools/HwProbe` | 진단 콘솔: `peak`/`vector` 스캔, `peaktest`/`vectortest` 송수신, `vectordump` 오프셋 분석 |
| `tests/CanMessager.Tests` | xUnit 63개 (PCI/코덱/루프백 E2E/DLC/스캐너/트레이서) |

---

## 3. 통합 개선 백로그 (추후 개선)

### 우선순위 높음
- [ ] **이어받기(Resume)**: 수신 임시파일(`*.cmpart`)+비트맵 보존 → RESUME로 재개 (설계는 docs/02에 존재, 구현 미완)
- [ ] **전송 취소/일시정지**: CancellationToken 시 ABORT PDU 송신 + UI 취소 버튼
- [ ] **설정 영속화**: 최근 역할/채널/ID/속도 저장·복원

### 처리량/성능
- [ ] 청크 단위 **선별 재전송**(현재 블록 단위 반복) — 손실 환경 효율↑
- [ ] **스트리밍 SHA-256**(송/수신 모두 전체 재해시 → 점진적 계산)으로 시작/완료 지연 감소
- [ ] 임의 비트레이트 **동적 비트타이밍 계산**(현재 80MHz 프리셋 의존)

### 드라이버/HAL
- [ ] Vector `XLchannelConfig` 오프셋은 VN1640A 기준 — 타 vxlapi 버전 대비 `DumpRawConfig`로 재검증 절차화
- [ ] PEAK/Vector **버스 오류·통계**(error frame, bus load) 노출로 진단 강화
- [ ] 트레이스를 **.asc/.blf 표준 로그**로 저장

### 프로토콜/견고성
- [ ] **버전 협상** 정교화(하위호환 정책)
- [ ] **다중 동시 전송**(TransferId별 세션 큐) — 현재 1세션 가정
- [ ] 손실/지연 **주입 테스트**(FakeCanBus 드롭 시뮬레이션)로 재전송 경로 자동 검증

### UI/배포
- [ ] 속도(중재/데이터) 선택 콤보 노출, 다중 파일 큐
- [ ] (선택) PublishTrimmed로 패키지 축소 — WPF/인터롭 트리밍 안정성 검증 필요

---

## 4. 빠른 빌드/실행 메모

```powershell
# 빌드
dotnet build CanMessager.sln -c Release

# 테스트
dotnet test tests/CanMessager.Tests

# 앱 실행
dotnet run --project src/CanMessager.UI -c Release

# 하드웨어 진단
dotnet run --project tools/HwProbe peak        # PEAK 채널 스캔
dotnet run --project tools/HwProbe vector       # Vector 채널 스캔
dotnet run --project tools/HwProbe peaktest     # PEAK 2채널 송수신
dotnet run --project tools/HwProbe vectortest   # Vector 가상채널 송수신
dotnet run --project tools/HwProbe vectordump   # Vector 구조체 오프셋 분석

# Portable 패키지(자체 포함 단일 exe)
dotnet publish src/CanMessager.UI -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```
