# PRD — AnyClip macOS / Windows 앱화 (v1.0)

## Problem Statement

AnyClip은 LAN 안에서 macOS ↔ Windows 텍스트/이미지/파일 클립보드를 양방향 동기화하는 단일 파일 Python 데몬이다. 현재 사용자는 직접 다음을 수행해야 한다.

- 저장소를 `git clone` 한다.
- Python 3.9+와 virtualenv를 만든다.
- `pip install -r requirements.txt` 한다.
- `ANYCLIP_TOKEN` 환경변수를 손으로 설정한다.
- 터미널에서 `python anyclip.py --verbose`를 직접 실행한다.
- macOS는 `~/Library/LaunchAgents/com.anyclip.plist`, Windows는 작업 스케줄러를 손으로 등록해야 부팅 후 자동 시작이 된다.
- 새 버전이 나오면 `git pull` 후 위 단계를 다시 거친다.
- mDNS가 막혔거나 macOS Local Network 권한을 거부했을 때 왜 안 되는지 스스로 진단해야 한다 (로그 파일을 직접 열어서).

이 모든 작업은 터미널/Python 생태계에 익숙한 사용자만 통과할 수 있다. AnyClip이 일반 사용자에게 "그냥 동작하는 클립보드 동기화 앱"이 되려면, 두 OS 모두에서 더블클릭으로 설치·실행·자동시작·자동업데이트되는 진짜 앱 형태가 되어야 한다.

## Solution

AnyClip을 macOS arm64용 `.dmg`와 Windows x64용 `.exe`로 패키징해 GitHub Releases로 배포한다. 두 빌드 모두 `anyclip.py`의 검증된 코어를 그대로 in-process로 띄우고, 그 위에 OS-native menubar/tray UI 쉘만 얹는다.

사용자 경험은 다음과 같다.

- 다운로드 → 더블클릭 → 첫 실행 창에서 "새 토큰 생성"(첫 기기) 또는 "토큰 입력"(두 번째 기기) 한 번. 이후 menubar/tray의 클립보드 아이콘 하나로 압축된다.
- 메뉴를 열면 현재 연결 상태(Linked: 상대 이름), 마지막 동기화 시간, "Start at Login" 토글, "Open Logs", "Quit"만 보인다.
- macOS Local Network 권한이 거부됐거나 mDNS가 30초간 잠잠하면 menubar 아이콘이 경고 상태로 바뀌고 "Local Network blocked → 설정 열기" 버튼이 나타난다.
- 자동 업데이트(Sparkle/WinSparkle)가 GitHub Pages의 appcast.xml을 주기적으로 확인해 새 버전을 백그라운드 다운로드 후 사용자에게 재시작 권유. EdDSA로 서명되어 위·변조 차단.
- 한쪽 기기만 먼저 업데이트되어 프로토콜 메이저 버전이 다르면 link를 거부하고 menubar에 "Peer needs update: v1.5 vs v1.3" 표시.
- 기존 CLI 사용자를 위해 같은 바이너리에 `--headless` 옵션을 두어 GUI 없이 기존 데몬 모드 동작 유지.

배포 파이프라인은 `v*` 태그를 push하면 GitHub Actions가 `macos-14`와 `windows-latest` 러너에서 PyInstaller(Windows)와 py2app(macOS)로 빌드해 Release asset과 appcast.xml을 함께 발행한다. 코드 서명은 v1.0 범위에서 하지 않으며, 첫 실행 시 macOS Gatekeeper / Windows SmartScreen 우회 절차를 README에 명시한다.

## User Stories

1. As a 새 사용자, I want 다운로드한 `.dmg`/`.exe`를 더블클릭하면 설치가 끝나기를 want, so that 터미널이나 Python을 만지지 않고도 AnyClip을 쓸 수 있다.
2. As a 첫 기기에서 처음 실행하는 사용자, I want 앱이 "새 토큰 생성" 버튼을 제공하기를, so that 비밀번호를 직접 고민하지 않아도 안전한 토큰이 만들어진다.
3. As a 두 번째 기기에서 처음 실행하는 사용자, I want 첫 기기에서 만든 토큰을 붙여넣을 입력칸이 제공되기를, so that 두 기기를 한 번에 연결할 수 있다.
4. As a 사용자, I want 토큰이 `~/.anyclip/config.json`에 0600 권한으로 저장되기를, so that 매 실행마다 다시 입력하지 않아도 된다.
5. As a 사용자, I want menubar/tray 아이콘 하나로 앱 상태를 확인할 수 있기를, so that 어떤 창도 따로 열어두지 않아도 된다.
6. As a 사용자, I want menubar 메뉴에서 현재 연결된 피어 이름과 마지막 동기화 시각을 볼 수 있기를, so that 동기화가 살아있는지 안 보고도 확인할 수 있다.
7. As a 사용자, I want menubar 메뉴의 "Start at Login" 토글을 켜면 로그인할 때 자동 시작되기를, so that launchd plist나 작업 스케줄러를 손으로 등록하지 않아도 된다.
8. As a 사용자, I want "Start at Login"을 다시 끄면 자동 시작이 제거되기를, so that 마음이 바뀌었을 때 OS 설정을 직접 뒤지지 않아도 된다.
9. As a 사용자, I want menubar 메뉴의 "Open Logs" 항목으로 로그 파일을 바로 열 수 있기를, so that 문제가 생겼을 때 경로를 외우지 않아도 된다.
10. As a 사용자, I want menubar 메뉴의 "Quit"으로 앱을 깨끗하게 종료할 수 있기를, so that 백그라운드 데몬이 남는 일이 없다.
11. As a macOS 사용자, I want 앱이 처음 실행될 때 Local Network 권한 팝업이 표시되기를, so that 권한 부여 기회를 명시적으로 받는다.
12. As a macOS 사용자, I want 권한을 실수로 거부했거나 잊었을 때 앱이 그 사실을 감지해 menubar에 "Local Network blocked" 경고를 띄우기를, so that 왜 연결이 안 되는지 추측하지 않아도 된다.
13. As a macOS 사용자, I want 위 경고에서 "Open Settings" 버튼을 누르면 시스템 설정의 Local Network 섹션으로 바로 이동하기를, so that 설정 경로를 모르고도 권한을 줄 수 있다.
14. As a Windows 사용자, I want 앱이 처음 실행될 때 방화벽 팝업이 자연스럽게 뜨기를, so that 24816/TCP 인바운드를 한 번에 허용할 수 있다.
15. As a 사용자, I want 앱이 LAN에서 피어를 탐색 중일 때 menubar 아이콘이 "탐색 중" 상태로 보이기를, so that 단순히 연결 안 됨이 아니라 작업 중임을 인지한다.
16. As a 사용자, I want 피어가 발견되어 link가 성립하면 아이콘이 "연결됨" 상태로 바뀌기를, so that 동기화가 활성화되었다는 시각적 확인을 받는다.
17. As a 사용자, I want 오류 상태(권한 거부, 토큰 불일치, 포트 점유 등)일 때 아이콘이 명확히 다른 모양/색으로 보이기를, so that 한눈에 문제를 인지한다.
18. As a 사용자, I want 새 버전이 나오면 앱이 알아서 백그라운드로 다운로드하고 재시작 시점만 묻기를, so that 업데이트를 잊고 살아도 된다.
19. As a 사용자, I want 자동 업데이트가 EdDSA 서명으로 검증되기를, so that 신뢰할 수 없는 바이너리가 깔리는 일이 없다.
20. As a 사용자, I want 한쪽 기기만 먼저 업데이트되어 프로토콜 메이저 버전이 달라졌을 때 link가 깨지는 대신 menubar에 "Peer needs update" 경고가 뜨기를, so that 동기화가 조용히 망가지지 않는다.
21. As a 사용자, I want 마이너 버전 차이는 호환되기를, so that 양쪽 기기의 업데이트 타이밍이 살짝 어긋나도 계속 동작한다.
22. As a 기존 CLI 사용자, I want 같은 바이너리에 `--headless` 옵션을 두어 GUI 없이 기존 데몬 모드로 돌릴 수 있기를, so that 이미 등록해둔 launchd plist / 작업 스케줄러가 계속 동작한다.
23. As a 기존 CLI 사용자, I want `--token`, `--peer`, `--port`, `--verbose` 같은 기존 옵션이 `--headless` 모드에서 그대로 동작하기를, so that 운영 방식을 바꿀 필요가 없다.
24. As a 사용자, I want 같은 기기에서 앱을 중복 실행하면 이전 인스턴스가 깔끔하게 종료되고 새 인스턴스가 살아남기를, so that 두 개의 데몬이 충돌하지 않는다.
25. As a 사용자, I want 앱이 PyInstaller/py2app로 자체 포함된 Python 런타임을 포함하기를, so that 시스템 Python을 따로 설치하지 않아도 된다.
26. As a 사용자, I want 코드 서명이 없는 빌드라도 첫 실행 우회 절차가 README에 명확히 적혀있기를, so that Gatekeeper/SmartScreen에 막혀도 다음에 무엇을 누를지 안다.
27. As a 메인테이너, I want `v*` 태그를 push하면 GitHub Actions가 자동으로 macOS arm64와 Windows x64 빌드를 만들어 Release asset으로 올리기를, so that 매 릴리스마다 두 OS를 직접 빌드하지 않아도 된다.
28. As a 메인테이너, I want 같은 워크플로가 appcast.xml을 갱신해 GitHub Pages에 발행하기를, so that 기존 사용자들이 자동으로 새 버전을 받는다.
29. As a 메인테이너, I want EdDSA private key가 GitHub Actions Secret(`SPARKLE_PRIVATE_KEY`)으로만 존재하기를, so that 키가 git 히스토리에 노출되지 않는다.
30. As a 메인테이너, I want 토큰 저장 형식이 KEY=VALUE 평문 JSON임을 명시적으로 결정하기를, so that OS keychain 통합은 phase 2에서 별도 판단할 수 있다.
31. As a 메인테이너, I want 5개 깊은 모듈에 유닛 테스트가 들어가기를, so that 회귀 위험이 가장 큰 부분(config 영속화·상태 머신·권한 진단·자동 시작 등록·버전 협상)이 자동 검증된다.
32. As a 메인테이너, I want PRD에 잡힌 범위가 한 v1.0 릴리스로 묶이기를, so that 사용자가 "앱이 됐다"고 느끼는 시점이 명확하다.

## Implementation Decisions

다음 모듈로 분해한다. 깊은(상태가 단순한) 모듈은 OS와 무관한 순수 로직으로 격리해 유닛 테스트 대상으로 삼고, 얇은 어댑터(tray·daemon·updater·onboarding 다이얼로그)는 통합/수동 검증에 맡긴다.

### 깊은 모듈 (테스트 대상)

- **`config_store`** — 토큰·설정의 영속화. `load() -> Config`, `save(Config) -> None`, `generate_token() -> str`. 파일은 `~/.anyclip/config.json`, 권한 0600, JSON 평문. keychain 통합은 phase 2.
- **`peer_state`** — 데몬 이벤트(`PeerDiscovered`, `LinkUp`, `LinkDown`, `HandshakeFailed`, `PermissionMissing`)를 받아 UI 상태 머신을 굴린다. 상태는 `Idle`, `Searching`, `Linked(peer_name, since)`, `Error(reason)` 4종. 순수 함수형 reducer로 구현해 골든 테스트 가능.
- **`permission_probe`** — macOS에서 데몬 시작 후 30초 동안 mDNS 발견/광고 이벤트가 한 번도 없으면 "Local Network blocked"로 판정. 결과: `{ok, blocked_local_network, no_network}` 세 갈래. fake clock으로 시간 진행 테스트.
- **`autostart`** — 로그인 시 자동 시작 등록/해제. macOS는 `~/Library/LaunchAgents/com.anyclip.plist`를 직접 쓰고 `launchctl load/unload` 호출. Windows는 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 레지스트리 키. 인터페이스: `is_enabled() -> bool`, `enable() -> None`, `disable() -> None`. 임시 HOME/HKCU를 가리키게 해 실 파일/레지스트리로 테스트.
- **`version_negotiator`** — handshake 시 양쪽이 `(app_version_semver, PROTOCOL_VERSION_int)`를 주고받고, 결과를 다음 enum으로 반환: `Compatible`, `PeerOlderMinor`, `PeerNewerMinor`, `PeerOlderMajor(refuse)`, `PeerNewerMajor(refuse)`. minor 차이는 link 유지 + tray 경고, major 차이는 link 거부 + tray 강한 경고. 순수 함수.

### 얇은 어댑터 (수동/통합 검증)

- **`daemon_supervisor`** — `anyclip.py`의 `run()`을 GUI 프로세스 안에서 asyncio task로 띄우고 종료/재시작 관리. CLI 모드(`--headless`)와는 같은 supervisor를 다른 진입점으로 호출. 별도 subprocess는 쓰지 않음 (Python 안에 Python 다시 띄우는 복잡도 회피).
- **`tray_menu`** — UI shell. macOS는 `rumps`, Windows는 `pystray`. 공통 `MenuModel`(상태 라벨, 토글 상태, 클릭 핸들러) 인터페이스를 정의하고 OS별 어댑터 두 개가 그것을 그린다. 메뉴 항목은 status 라벨, last sync 시각, "Token...", "Start at Login" 토글, "Open Logs", 업데이트 알림(있을 때만), "Quit".
- **`updater_bridge`** — macOS는 `Sparkle.framework`을 .app 번들에 포함하고 PyObjC로 `SUUpdater` 호출. Windows는 `WinSparkle.dll`을 `ctypes`로 로드해 `win_sparkle_init` → `win_sparkle_check_update_with_ui`. appcast URL은 `https://gum798.github.io/AnyClip/appcast.xml`로 고정.
- **`onboarding_window`** — 첫 실행 시 `~/.anyclip/config.json`이 없으면 작은 다이얼로그 띄움. 두 버튼: "Generate new token (first device)" / "Enter existing token (second device)". macOS는 PyObjC NSAlert + NSTextField, Windows는 `pystray` 외부의 tkinter 단일 다이얼로그.

### 빌드/배포 결정

- **빌드 도구**: macOS=py2app, Windows=PyInstaller. PyInstaller가 양쪽 다 가능하지만 macOS는 py2app이 Info.plist·LSUIElement·NSLocalNetworkUsageDescription 주입이 자연스러움.
- **빌드 타겟**: macOS arm64 (Apple Silicon 전용), Windows x64. Intel Mac과 Windows ARM64는 v1.0 범위 밖.
- **CI**: `.github/workflows/release.yml`이 `v*` 태그 push에 트리거. `macos-14`(arm64) + `windows-latest` 매트릭스로 빌드해 GitHub Releases에 asset(.dmg, .exe) 업로드 + `gh-pages` 브랜치의 `appcast.xml` 갱신.
- **코드 서명**: 없음. README의 첫 실행 섹션에 macOS는 "Finder에서 우클릭 → 열기"·Windows는 SmartScreen "추가 정보 → 실행" 절차 명시.
- **자동 업데이트 인프라**: appcast.xml은 GitHub Pages(`gum798.github.io/AnyClip/`)에서 호스팅. EdDSA private key는 GitHub Actions Secret `SPARKLE_PRIVATE_KEY`. public key는 빌드 시점에 .app Info.plist의 `SUPublicEDKey` / WinSparkle 빌드 설정에 주입.
- **자동 시작 기본값**: opt-in (꺼짐). 사용자가 명시적으로 menubar 토글을 켜야만 등록.
- **단일 인스턴스 처리**: 기존 `anyclip.py`의 PID 파일(`~/.anyclip/anyclip.pid`) 로직을 그대로 재사용. GUI 진입점도 같은 PID 파일을 본다.
- **로그 위치**: 기존 `~/.anyclip/anyclip.log` 그대로. "Open Logs" 메뉴는 macOS는 `open -R`, Windows는 `explorer /select`.
- **헤드리스 호환**: `--headless` 플래그가 있으면 GUI shell을 우회하고 기존 `anyclip.py` `run()` 직행. 기존 CLI 옵션(`--token`, `--peer`, `--port`, `--verbose`, `--poll`, `--name`)을 그대로 받음.

### Handshake 프로토콜 확장

기존 handshake JSON에 다음 필드 추가:

```
{
  "version": 1,                  // PROTOCOL_VERSION (기존)
  "app_version": "1.0.0",        // (신규) semver, 앱 빌드 시점에 주입
  "protocol_major": 1,           // (신규) major 호환성 키
  "protocol_minor": 0,           // (신규) minor 호환성 키
  ...
}
```

`version_negotiator`가 다음을 판단:

- `protocol_major` 동일 → `Compatible` 또는 minor 차이 따라 `PeerOlderMinor`/`PeerNewerMinor` (link 유지 + tray hint)
- `protocol_major` 다름 → `PeerOlderMajor` / `PeerNewerMajor` 둘 다 link 거부 (tray 강한 경고 + 어느 쪽이 업데이트 필요한지 표시)

기존 필드(`version`)는 하위호환을 위해 유지하되 의미는 `protocol_major`와 동치로 다룬다.

### 아이콘 자산

- 메인 아이콘: 클립보드 모티프 SVG 1종 → ImageMagick/sips로 macOS `.icns`(16~1024)·Windows `.ico`(16/32/48/256) 자동 파생.
- Tray 아이콘: 상태별 3종 (`linked`, `searching`, `error`). macOS는 모노크롬 PDF template image(light/dark 자동), Windows는 풀컬러 .ico.

### 디렉터리 변화

- 신규: `app/` 하위에 GUI shell 모듈, `build/` 하위에 py2app 스펙·PyInstaller spec·아이콘 자산·Info.plist 템플릿, `.github/workflows/release.yml`, `docs/PRD-app-packaging.md`.
- 수정: `anyclip.py`(handshake에 신규 필드 추가, `--headless` 분기, 동일 진입점), `README.md`(설치 섹션을 "Download from Releases" 우선으로 재작성, CLI 섹션은 "Advanced" 아래로).

## Testing Decisions

좋은 테스트는 외부 동작만 검증한다 — 내부 구조나 호출 순서가 아니라, "이 입력이 들어오면 이 결과·이 파일·이 상태"가 나온다는 것. 모듈을 갈아엎어도 깨지지 않을 만큼 단단해야 한다.

### 테스트 대상

`pytest`를 신규 도입(`requirements-dev.txt`에 분리). 다음 5개 깊은 모듈에 한해 유닛 테스트:

- **`config_store`** — 빈 디렉토리에서 `save → load` 라운드트립, 권한 0600 검증, 토큰 생성 시 충분한 엔트로피, 손상된 JSON 파일에서 graceful 처리.
- **`peer_state`** — 이벤트 시퀀스 → 최종 상태 골든 테스트. 시나리오: `[Discovered, LinkUp] → Linked`, `[LinkUp, LinkDown] → Searching`, `[HandshakeFailed × 5] → Error("auth")`, `[PermissionMissing] → Error("local_network")`.
- **`permission_probe`** — 가짜 시계 + 가짜 mDNS 이벤트 피드. 30초 안에 이벤트 1개 이상 → `ok`, 30초 무이벤트 → `blocked_local_network`, 네트워크 인터페이스 자체 없음 → `no_network`.
- **`autostart`** — 임시 `$HOME` / 임시 HKCU 키를 가리키게 해, `enable() → is_enabled() True → plist/레지스트리 파일 존재 → disable() → is_enabled() False → 파일 부재` 라운드트립.
- **`version_negotiator`** — 표 기반 테스트. (local_app, local_proto) × (peer_app, peer_proto) 매트릭스로 9가지 케이스의 `Compatibility` enum 정답 검증.

### 테스트 안 하는 것

- `daemon_supervisor` — 기존 `anyclip.py`의 supervisor 로직이 운영 중인 검증된 자산. GUI 안에서 task로 띄우는 wrapper만 추가되므로 수동 smoke 테스트로 충분.
- `tray_menu` — OS native 어댑터, GUI 자동 테스트 비용 대비 회수 적음.
- `updater_bridge` — Sparkle/WinSparkle 자체는 외부 라이브러리. v1.0에서는 양쪽 OS에서 실 release 후 사용자 측에서 자동 업데이트가 정상 트리거되는지 수동 검증.
- `onboarding_window` — 한 번만 뜨는 다이얼로그, 수동 검증.
- 빌드 산출물 자체 — GHA 워크플로가 빌드 통과/asset 생성 여부로 자체 검증. `.app`/`.exe`의 실제 실행 검증은 수동.

### 프리어트 (prior art)

현재 저장소에는 자동화된 테스트가 전혀 없다. 따라서 위 5개 모듈이 첫 테스트 슈트가 된다. pytest 디스커버리 기본 규칙(`tests/test_*.py`) 사용. CI는 GHA 매트릭스에 `pytest` step을 빌드 step 직전에 추가해 빌드와 함께 굴린다.

## Out of Scope

다음 항목은 v1.0에 포함하지 *않는다*. 별도 PRD에서 다룬다.

- **macOS 코드 서명 / 공증**: Apple Developer 계정·공증 인프라 필요. v1.x 또는 별도 결정 후 도입.
- **Windows 코드 서명**: EV/OV 인증서 비용. 위와 같음.
- **App Store / Microsoft Store 배포**: sandbox entitlement 제약이 mDNS·raw TCP와 충돌. v1.x 이후 별도 판단.
- **Homebrew Cask / winget / Chocolatey** 등록: 외부 저장소 PR 절차 필요.
- **macOS Intel(x86_64) 빌드**: arm64만 출시.
- **Windows ARM64 빌드**: x64만 출시.
- **OS keychain / Windows Credential Manager 통합**: phase 2. v1.0은 `config.json` 평문 0600.
- **QR/PIN 페어링**: 사용자가 토큰 문자열을 직접 옮긴다.
- **3대 이상 동시 동기화**: 기존 1:1 한계 유지.
- **NAT/외부 네트워크 동기화**: 기존 LAN-only 한계 유지.
- **Linux/Android/iOS 빌드**: README의 향후 계획대로 별도 phase.
- **클립보드 권한(macOS Sonoma+ Pasteboard 알림) 대응**: pyperclip이 현재 잘 동작하는 상태 유지.
- **자동 업데이트의 in-place 무결성 검증 외 추가 안전망**(롤백, 다중 채널): Sparkle/WinSparkle 기본 동작에 의존.
- **메인 윈도우 GUI / 클립보드 히스토리 / 명시적 푸시 / 레이아웃 커스터마이징**: 경량 status 메뉴로 한정.

## Further Notes

### v1.0 한 번에 묶는 위험

PRD 합의 시점에 사용자가 "전부 한 번에 v1.0"을 명시적으로 선택. 일반적 권장(walking skeleton → Sparkle은 마지막)을 따르지 않았다. 다음 위험을 인지하고 진행한다.

- 첫 PR이 GUI shell + onboarding + Sparkle 통합 + GHA 워크플로 + Info.plist/icns/ico + 자가진단 + handshake 확장으로 합산 ~2000줄 규모가 될 수 있다. 리뷰·롤백 단위가 크다.
- Sparkle/WinSparkle 첫 부트스트랩이 까다롭다. appcast.xml 형식·EdDSA 서명·public key 주입 중 하나라도 어긋나면 사용자 기기들이 "Update failed" 루프에 빠질 수 있다.
- 양쪽 기기에 자동 업데이트가 깔리기 *전*까지는 한 번의 수동 교체가 불가피하다. v1.0 릴리스 노트에 이 점을 명시한다.

### 코드 서명 없음의 함의

- macOS Gatekeeper는 quarantine xattr이 붙은 .dmg 안의 .app를 차단한다. 사용자는 첫 실행 시 Finder 우클릭 → 열기를 한 번 거쳐야 한다.
- Sparkle이 자동 다운로드한 새 .app에도 quarantine xattr이 새로 붙는다. EdDSA 검증은 통과해도 Gatekeeper가 재차 막을 수 있다. v1.0 릴리스에서 이 동작을 사용자 측에서 실측해 README에 정확한 절차를 적는다.
- Windows SmartScreen은 평판 누적 전까지 "Windows protected your PC" 화면을 띄운다. "추가 정보 → 실행" 절차 명시.

### gum798/AnyClip 권한 가정

PRD는 `gum798/AnyClip`이 메인테이너 본인 계정이며 GitHub Pages·Secrets·Actions·Releases에 admin 권한이 있다는 가정 위에 작성됨. 만약 fork 또는 권한 없는 저장소라면 GitHub Pages 호스팅·Secret 등록 단계에서 막힘 — 그 시점에 별도 결정 필요.

### 마이그레이션

기존 CLI 사용자(launchd plist 또는 작업 스케줄러로 `python anyclip.py` 돌리는 사용자)에게는 다음을 README에 안내:

- 옵션 A: 기존 plist/Task 그대로 두고, 새 .app/.exe를 추가 설치하지 않음. 변화 없음.
- 옵션 B: 새 .app/.exe로 옮기되, 기존 plist의 `python anyclip.py`를 새 바이너리의 `AnyClip.app/Contents/MacOS/AnyClip --headless`로 교체. `--headless` 모드가 기존 옵션을 그대로 받으므로 토큰·peer 설정은 유지됨.
