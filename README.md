# CAN File Manager

CAN-FD 기반 1:1 파일 송수신 Messenger (Windows / .NET 9 / WPF).
Vector VN16xx, PEAK PCAN-USB FD 인터페이스를 지원하며 ISO-TP(ISO 15765-2) 위에
자체 애플리케이션 프로토콜(CMAP)로 대용량 파일(ZIP 등)을 신뢰성 있게 전송합니다.

## 주요 기능
- **CAN-FD 64byte** 프레임, **ISO-TP** 세그멘테이션/재조립 + Flow Control
- **CMAP** 앱 프로토콜: 청크 분할, 윈도우(블록) ACK 재전송, **SHA-256** 무결성 + 청크 CRC32
- 역할 선택: **송신처 / 수신처 / 루프백 데모(Fake)**
- 인터페이스: **Fake(가상) / PEAK / Vector**, 시리얼·물리채널 스캔/선택
- 진행률·속도·예상 잔여시간 표시, **CAN 통신 트레이스**(프레임 단위)
- 수신 저장 폴더/파일명 지정, 송신 파일 요약

## 솔루션 구조
```
src/
  CanMessager.Hal   하드웨어 추상화(ICanDriver) + Fake/PEAK/Vector + 채널 스캔
  CanMessager.Core  Transport(ISO-TP) + Application(CMAP, 파일 송수신)
  CanMessager.UI    WPF(MVVM) — 제품명 "CANoe HEX ReFlash Manager"
tests/CanMessager.Tests   xUnit 63개
tools/HwProbe             하드웨어 진단 콘솔
docs/                     설계/코드 가이드 문서
Markdown/                 일별 작업 로그
```

## 빌드 & 실행
```powershell
dotnet build CanMessager.sln -c Release
dotnet test  tests/CanMessager.Tests
dotnet run   --project src/CanMessager.UI -c Release
```

### Portable(자체 포함 단일 exe) 게시
```powershell
dotnet publish src/CanMessager.UI -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

### 하드웨어 진단
```powershell
dotnet run --project tools/HwProbe peak        # PEAK 채널 스캔
dotnet run --project tools/HwProbe vector       # Vector 채널 스캔
dotnet run --project tools/HwProbe peaktest     # PEAK 2채널 송수신
dotnet run --project tools/HwProbe vectortest   # Vector 가상채널 송수신
```

## 요구 사항
- Windows 10/11 x64, .NET 9 SDK(빌드 시)
- 실제 하드웨어 사용 시 벤더 드라이버 설치: PEAK(PCANBasic.dll) / Vector(vxlapi64.dll)

## 문서
- [docs/01_ISO-TP_Layer_Design.md](docs/01_ISO-TP_Layer_Design.md)
- [docs/02_Application_Protocol_Design.md](docs/02_Application_Protocol_Design.md)
- [docs/03_Code_Guide.md](docs/03_Code_Guide.md)
