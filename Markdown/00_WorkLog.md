# CAN Messager — 작업 로그 인덱스

> 프로젝트: CAN-FD 기반 1:1 파일 송수신 Messenger
> 일별 작업 로그는 `Markdown/YYYY-MM-DD.md` 파일로 관리합니다.
> 본 문서는 전체 개요 · 일별 로그 인덱스 · 누적 To-Do를 담습니다.

---

## 프로젝트 개요

차량용 **CAN-FD 프로토콜**을 활용하여 ZIP 등 대용량 압축 파일을 송수신하는 1:1 파일 공유 Messenger 프로그램.

### 확정된 기술 스택 / 요구사항

| 항목 | 결정 |
|---|---|
| 언어/프레임워크 | C# .NET WPF (Windows 데스크톱) |
| 지원 CAN 인터페이스 | Vector VN1630 / VN1640 (XL Driver Library), PEAK PCAN-FD (PCAN-Basic.NET) |
| 통신 토폴로지 | 1:1 Point-to-Point |
| 전송 프로토콜 | ISO-TP (ISO 15765-2) 표준 + 대용량 확장 |
| CAN-FD Payload | 64 byte/frame (ISO-TP로 세그멘테이션) |
| 무결성 검증 | SHA-256 |
| UI 부가 기능 | 전송 진행률 / 속도(KB/s) / 예상 잔여시간 표시 |

---

## 일별 작업 로그 (Daily Logs)

| 날짜 | 요약 | 링크 |
|---|---|---|
| 2026-06-07 | 킥오프·요구사항 확정, ISO-TP·CMAP 설계 문서, HAL/ISO-TP/CMAP 구현 완료 (테스트 45개 통과, 1 MiB E2E 검증) | [2026-06-07.md](2026-06-07.md) |
| 2026-06-08 | PEAK CAN-FD 실드라이버(PeakCanDriver) 구현 + 실카드(PCAN-USB Pro FD) 초기화·송신 검증, 테스트 63개 | [2026-06-08.md](2026-06-08.md) |

---

## 누적 To-Do

- [ ] **Open Questions 결정** (docs/01 문서 10장)
  - [ ] WAIT FC 최대 허용 횟수 (제안: 4회)
  - [ ] 재전송 정책 (애플리케이션 레이어에서 청크 단위 재요청)
  - [ ] 프레임 trace 로깅 포맷 (`.asc`/`.trc` 호환 여부)
  - [ ] HW 타임스탬프 활용 여부
- [x] 솔루션 스캐폴딩 (.NET 9 WPF) — 2026-06-07
- [x] HAL 계층 구현 (`ICanDriver` + Fake 드라이버) — 2026-06-07
- [x] ISO-TP 레이어 구현 + 단위 테스트 24개 통과 — 2026-06-07
- [x] **애플리케이션 프로토콜(CMAP) 설계 문서** (`docs/02_...`) — 2026-06-07
- [x] CMAP 코덱 구현 (`Crc32`, `CmapCodec`) — 2026-06-07
- [x] 파일 송수신 서비스 (`FileSendService`/`FileReceiveService`) + E2E 테스트 45개 통과 — 2026-06-07
- [x] WPF UI (MVVM, FakeDriver 루프백 데모, 진행률/속도/ETA/SHA-256) — 2026-06-07
- [x] 장치/채널 스캔(시리얼·물리채널 선택) + UI — 2026-06-07
- [x] PEAK 실드라이버(`PeakCanDriver`) 구현 + 실카드 초기화·송신 검증 — 2026-06-08
- [ ] PEAK 실 CAN-FD 파일 전송 E2E (CH1↔CH2 배선 후)
- [ ] Vector 실드라이버(`VectorCanDriver`) 구현

---

## 문서 인덱스

| 문서 | 설명 |
|---|---|
| [Markdown/00_WorkLog.md](00_WorkLog.md) | 작업 로그 인덱스 (본 문서) |
| [Markdown/YYYY-MM-DD.md](.) | 일별 작업 로그 |
| [docs/01_ISO-TP_Layer_Design.md](../docs/01_ISO-TP_Layer_Design.md) | ISO-TP Transport Layer 설계 |

---

## 로그 작성 규칙

1. 새 작업일에는 `Markdown/YYYY-MM-DD.md` 파일을 새로 만든다.
2. 각 일별 로그에는 **완료 작업 / 결정 사항 / 다음 작업**을 기록한다.
3. 작업 종료 시 본 인덱스의 **일별 로그 표**와 **누적 To-Do**를 갱신한다.
