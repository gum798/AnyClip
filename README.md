# AnyClip

LAN 안의 두 컴퓨터 사이에서 **클립보드를 양방향으로 자동 동기화**하는 도구.
한쪽에서 복사하면 반대쪽에 즉시 반영됩니다. 텍스트·이미지·파일·폴더 모두 지원.

## Quick Start

두 컴퓨터(예: Mac ↔ Windows) **각각에** 설치한 뒤, 같은 **토큰**으로 한 번만 연결하면 됩니다.

### macOS — Homebrew (권장)

```bash
# Homebrew가 없다면 먼저: https://brew.sh 의 한 줄 설치 명령 실행
brew tap gum798/tap
brew install --cask anyclip
open -a AnyClip          # 또는 Launchpad에서 AnyClip 실행
```

네이티브 Swift 빌드(`formacOS/`)가 `/Applications`에 설치됩니다. 코드 서명이 없는 빌드라 첫 실행은 Gatekeeper가 차단합니다 — 우클릭 → 열기 1회, 또는:

```bash
xattr -dr com.apple.quarantine /Applications/AnyClip.app
```

### Windows — Scoop (권장)

```powershell
# Scoop이 없다면 먼저 (관리자 권한 불필요, 한 번만):
Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser
irm get.scoop.sh | iex

scoop bucket add gum798 https://github.com/gum798/scoop-bucket
scoop install anyclip
anyclip                  # 또는 시작 메뉴에서 AnyClip 실행
```

네이티브 C# 빌드(`forwindows/`)가 설치됩니다. 첫 실행 시 SmartScreen이 뜨면 **추가 정보 → 실행** 1회. (winget은 [microsoft/winget-pkgs 심사](https://github.com/microsoft/winget-pkgs/pull/387026) 통과 후 `winget install gum798.AnyClip` 사용 가능.)

### 첫 실행 — 두 기기 연결 (설치 방식 공통)

1. **첫 번째 기기**에서 AnyClip 실행 → 온보딩 창에서 **"Generate new token"** → 생성된 토큰을 복사해 둡니다. (나중에 메뉴바/트레이 → **Token…** 에서 다시 볼 수 있습니다.)
2. **두 번째 기기**에서 AnyClip 실행 → 온보딩 창에서 **"Enter existing token"** → 1번에서 복사한 토큰을 붙여넣습니다.
3. 두 기기가 같은 LAN에 있으면 메뉴바(macOS)·트레이(Windows) 아이콘 상태가 `Searching for peer` → `Linked: <상대 이름>`으로 바뀝니다.
4. 이제 한쪽에서 복사(⌘C / Ctrl+C)하면 반대쪽 클립보드에 자동 반영됩니다. **동기화될 때마다 아이콘에 원호가 한 바퀴 도는 펄스**가 잠깐 재생됩니다.

> 복사할 때마다 알림 팝업이 뜨길 원하면 메뉴바/트레이 메뉴의 **Notifications** 를 켜세요. (기본은 꺼짐 — 대신 위 아이콘 펄스로 표시.)

### 업데이트

새 버전은 패키지 매니저로 받습니다. **실행 중인 AnyClip은 먼저 종료**해야 교체됩니다(특히 Windows).

```bash
# macOS
brew update && brew upgrade --cask anyclip
# 적용: 메뉴바 @ → Quit 후 다시 실행

# Windows (PowerShell) — 트레이 아이콘 → Quit 먼저
scoop update && scoop update anyclip
anyclip
```

릴리스 직후라면 매니저 인덱스가 아직 최신이 아닐 수 있어 `brew update` / `scoop update`를 먼저 실행해야 새 버전이 보입니다.

### 직접 다운로드

패키지 매니저를 쓰지 않을 때의 방법입니다.

1. [최신 릴리스](https://github.com/gum798/AnyClip/releases/latest)에서 OS에 맞는 파일을 다운로드합니다.
   - macOS (native Swift, 권장): `AnyClip-vX.Y.Z-macos-arm64.zip` (Apple Silicon 전용)
   - macOS (legacy Python): `AnyClip-vX.Y.Z.dmg` (Apple Silicon 전용)
   - Windows (native C#, 권장): `AnyClip-vX.Y.Z-windows-x64-native.zip` (x64)
   - Windows (legacy Python): `AnyClip-vX.Y.Z-windows-x64.zip` (x64)
2. 압축을 풀고 `AnyClip.app`(macOS)을 `/Applications`로 옮기거나 `AnyClip.exe`(Windows)를 원하는 폴더에 둔 뒤 실행합니다.
3. 이후 연결 절차는 위 **[첫 실행 — 두 기기 연결](#첫-실행--두-기기-연결-설치-방식-공통)** 과 동일합니다.

### macOS 첫 실행 — Gatekeeper 우회

코드 서명이 없는 빌드라 macOS가 첫 실행을 차단할 수 있습니다.

1. `AnyClip.app`을 `/Applications/`로 옮긴 뒤,
2. Finder에서 `AnyClip.app`을 **우클릭 → 열기**를 선택합니다.
3. "확인되지 않은 개발자" 경고에서 **열기**를 누르면 한 번만 통과시키면 끝입니다. 이후 더블클릭이 그냥 동작합니다.

### Windows 첫 실행 — SmartScreen 우회

다운로드한 `.zip`을 풀고 `AnyClip.exe`를 더블클릭하면 SmartScreen이 "Windows에서 PC를 보호했습니다"라는 창을 띄울 수 있습니다.

1. **추가 정보** 클릭
2. **실행** 클릭

서명되지 않은 새 배포본은 평판이 쌓이기 전까지 매번 이 절차가 필요할 수 있습니다.

## Permissions

### macOS — Local Network 권한

처음 실행 시 macOS가 "AnyClip이 로컬 네트워크의 장치를 검색하려고 합니다"라고 묻습니다. **허용**을 선택하세요. mDNS로 같은 LAN의 상대 기기를 자동 발견하는 데 필요합니다.

실수로 거부했다면:

- 메뉴바 아이콘이 약 30초 후 ⚠ 상태로 바뀌고 메뉴에 **Open Local Network Settings** 항목이 나타납니다. 누르면 시스템 설정의 정확한 화면으로 바로 이동합니다.
- 또는 수동으로: `시스템 설정 → 개인정보 보호 및 보안 → 로컬 네트워크`에서 AnyClip을 켜기.
- 권한을 켠 뒤 메뉴바의 **Quit** → AnyClip 재실행.

### Windows — 방화벽

처음 실행 시 Windows 방화벽 팝업이 뜨면 **개인 네트워크**에 대한 허용을 선택하세요. 사용 포트:

- `24816/TCP` 인바운드·아웃바운드 (기본값; `--port`로 변경 가능)
- `5353/UDP` 멀티캐스트 (mDNS)

## Updates

내장 자동 업데이트(Sparkle / WinSparkle)가 하루에 한 번 새 버전을 확인합니다.

- 새 버전이 있으면 백그라운드로 다운로드한 뒤, 재시작 시점만 묻습니다.
- 즉시 확인하려면 메뉴바/트레이 아이콘 → **Check for Updates…**.
- 모든 업데이트는 EdDSA 서명으로 검증되므로 위·변조된 바이너리는 자동 거부됩니다.

> **부트스트랩 한 번**: 자동 업데이트가 실제로 동작하는 빌드를 처음 받기 *전*까지는 한 번의 수동 교체가 필요합니다. 첫 정식 릴리스를 받은 이후엔 모든 후속 업데이트가 자동입니다.

## Troubleshooting

문제 진단의 첫 단계는 **메뉴바/트레이 아이콘 → Open Logs**입니다. Finder/Explorer에서 `~/.anyclip/anyclip.log`가 열립니다.

| 증상 | 확인 |
|------|------|
| 메뉴바 ⚠ + "Local Network blocked" | macOS Local Network 권한 거부. 위 Permissions 섹션 참고 |
| `Searching for peer`에서 멈춤 (둘 다 켜진 상태) | mDNS 멀티캐스트 차단. 회사망/게스트 Wi-Fi인지 확인. Advanced의 `--peer` 폴백 사용 |
| `auth failed from peer` 반복 | 양쪽 토큰이 다름. 첫 번째 기기에서 **Token…** 메뉴로 경로 확인, 두 번째 기기 onboarding 다시 |
| `auth gate: <ip> blocked` | 5회 연속 토큰 실패 후 60초 IP 차단. 토큰 맞추고 60초 대기 |
| `connect to X:Y failed` | 24816/TCP 인바운드 차단. 방화벽 규칙 확인 |
| Ctrl+C / Quit 후에도 데몬이 남음 | 메뉴바/트레이 **Quit** 사용 권장. 그래도 남으면 `~/.anyclip/anyclip.pid` 확인 후 보고 |
| 시작 시 `tcp/24816 is held by a non-anyclip process` | 다른 프로그램이 24816을 쓰고 있음. Advanced의 `--port`로 변경 |

## How it works

```
┌────── Mac ──────┐                   ┌──── Windows ────┐
│  ClipboardWatch │                   │  ClipboardWatch │
│        │        │                   │        │        │
│   PeerLink ─────┼── TCP + JSON ─────┼──── PeerLink    │
│        │        │   (4-byte len)    │        │        │
│   MdnsBeacon ───┼── mDNS 5353/UDP ──┼─── MdnsBeacon   │
└─────────────────┘                   └─────────────────┘
```

- **자기 자신 필터링**: mDNS TXT 레코드의 `id`(UUID)로 자신의 광고를 무시.
- **에코 루프 방지**: 마지막 수신 콘텐츠의 SHA-256 해시를 기억해 동일 콘텐츠를 다시 송신하지 않음.
- **동시 connect 경합**: 두 노드가 서로에게 동시에 연결될 때 `node_id` 사전식 비교로 한 링크만 유지.
- **다중 피어 메시**: 1.3.0부터 데몬은 발견된 같은 토큰의 피어 전부와 동시에 링크를 유지하고(기본 최대 8대), 복사된 클립을 모든 활성 링크에 브로드캐스트한다. 수신한 클립을 다른 피어로 중계하지는 않는다.
- **인증**: 토큰을 SHA-256 해시로만 송신, 평문은 와이어에 흐르지 않음.
- **버전 협상**: handshake에서 양쪽이 protocol_major/minor를 교환해 메이저 불일치 시 link 거부 + 메뉴바 경고. 현재 프로토콜은 1.3이며 minor는 누적 기능 레벨이다(≥1이면 `kind:"files"`, ≥2면 64MiB 프레임 수신, ≥3이면 폴더 트리 복원). minor 3은 능력 표시(capability marker)일 뿐이라 송신 경로에서 아무것도 막지 않는다.
- **프레임 상한**: 프레임 하나당 64MiB(`MAX_PAYLOAD`). 16MB짜리 pptx 같은 큰 파일도 전송된다.
- **버전이 섞인 경우**: 16MiB를 넘는 프레임은 프로토콜 1.2 이상을 광고한 피어에게만 전송한다. 구버전 피어는 16MiB 초과 프레임을 받으면 세션을 끊기 때문에, 해당 링크는 유지한 채 전송만 건너뛰고 "clip not sent to …(too large for its AnyClip version)" 알림을 한 번 표시한다.
- **여러 파일 동기화**: 파일을 2개 이상 복사하면 하나의 `kind:"files"` 프레임(프로토콜 1.1)으로 묶어 전송. 합계 예산(~49MB)을 넘거나 500개를 초과하는 파일은 건너뛰고 알림으로 표시. 상대가 프로토콜 1.0(구버전)이면 첫 파일만 전송.
- **폴더 동기화**: 폴더를 복사하면 하위 파일을 재귀적으로 펼쳐 같은 `kind:"files"` 프레임에 담아 보낸다(프로토콜 1.3). 각 항목에는 폴더 이름부터 시작하는 상대 경로 `path`가 함께 실리고(POSIX `/`·NFC·상대 경로·`..` 불가·최대 32단계(파일 이름 포함)·정리 후 전체 경로 240자 이하), 규칙을 어기는 항목은 버려지지 않고 트리 구조 없이 평평하게 동기화된다. 받는 쪽은 `~/.anyclip/received/<폴더 이름>/…` 아래에 트리를 그대로 복원한 뒤 최상위 항목을 클립보드에 올린다. 같은 이름의 폴더가 이미 있으면 `<이름>-2`로 만들어 한 클립이 한 폴더에 떨어진다. `received/`는 데몬이 시작할 때와 정상 종료할 때 비우는 임시 폴더이므로, 받은 파일·폴더는 계속 쓰려면 다른 위치로 옮겨 보관해야 한다. 폴더 하나는 **전부 아니면 전부**라 남은 예산·개수에 통째로 들어가지 않으면 통째로 건너뛰고 `folder too large to sync: <이름>` 알림을 띄운다(부분 트리 없음). `.DS_Store`·`Thumbs.db`·`desktop.ini`와 심볼릭 링크는 제외하고(따라가지 않음), 남는 파일이 없는 폴더는 `folder is empty; nothing to sync`로 끝난다. 같은 선택에 섞인 낱개 파일은 지금처럼 `path` 없이 전송된다.
- **버전이 섞인 폴더 전송**: `path`는 선택 필드라 구버전 피어도 프레임을 그대로 받는다. 프로토콜 1.1~1.2 피어는 `path`를 무시하고 파일을 평평하게 저장하며, 이때 로그에 클립당·피어당 한 번 `peer <name> will flatten folders (protocol < 1.3)`가 남는다. 프로토콜 1.0 피어에게는 폴더에서 나온 항목을 첫 파일 폴백에서 제외하므로, 폴더만 복사한 클립은 그 링크로 아무것도 보내지 않는다(로그만). 트리를 그대로 주고받으려면 양쪽 모두 1.4.0 이상이어야 한다.

## Advanced — CLI mode

기존 CLI 사용 흐름(서버 / 헤드리스 환경 / 자동화)을 위한 모드입니다. 패키지된 앱과 동일한 데몬을 GUI 없이 띄웁니다.

### 옵션 A — 패키지된 바이너리에 `--headless`

```bash
# macOS
/Applications/AnyClip.app/Contents/MacOS/AnyClip --headless

# Windows
"C:\Program Files\AnyClip\AnyClip.exe" --headless
```

### 옵션 B — 소스에서 직접

```bash
# macOS
git clone https://github.com/gum798/AnyClip.git
cd AnyClip
python3 -m venv .venv
source .venv/bin/activate
pip install -r requirements.txt

export ANYCLIP_TOKEN=mysecret
python anyclip.py --headless --verbose
```

```powershell
# Windows PowerShell
git clone https://github.com/gum798/AnyClip.git
cd AnyClip
python -m venv .venv
.venv\Scripts\Activate.ps1
pip install -r requirements.txt

$env:ANYCLIP_TOKEN = "mysecret"
python anyclip.py --headless --verbose
```

### mDNS가 막힌 환경 — `--peer` 폴백

```bash
# 상대 IP를 직접 지정
python anyclip.py --headless --peer 192.168.0.42

# 여러 번 지정 가능
python anyclip.py --headless --peer 192.168.0.42 --peer 192.168.0.50
```

`--peer`는 mDNS와 공존합니다. 끊기면 1s→60s 지수 backoff로 자동 재연결.

### CLI 옵션

| 옵션 | 기본값 | 설명 |
|------|--------|------|
| `--headless` | 끔 | GUI 없이 데몬으로만 동작 |
| `--token` | `$ANYCLIP_TOKEN` 또는 `~/.anyclip/config.json` | 공유 비밀키 |
| `--save-token TOKEN` | — | 토큰을 `~/.anyclip/config.json`(0600)에 저장 후 즉시 종료 |
| `--install-autostart` | — | OS 로그인 시 자동 시작 등록 후 종료 |
| `--uninstall-autostart` | — | 자동 시작 해제 후 종료 |
| `--autostart-status` | — | 자동 시작 상태 출력 후 종료 |
| `--port` | `24816` | TCP 리슨 포트 |
| `--name` | hostname | 피어 식별용 표시 이름 |
| `--poll` | `0.5` | 클립보드 폴링 주기 (초) |
| `--peer HOST[:PORT]` | 없음 | 수동 폴백 피어 (반복 가능) |
| `--verbose`, `-v` | 끔 | 콘솔 DEBUG 로그 (파일 로그는 항상 DEBUG) |
| `--no-notify` | 끔 | 데스크톱 토스트 알림 억제 |

토큰을 한 번 저장해두면 이후 실행은 `--token` / `ANYCLIP_TOKEN` 없이도 동작합니다:

```bash
python anyclip.py --save-token mysecret      # 저장 후 종료
python anyclip.py --headless                  # 다음부터 자동 로드
```

## Migration — 기존 CLI 사용자

기존에 `python anyclip.py`를 launchd plist 또는 Windows 작업 스케줄러로 돌리고 있던 사용자에게는 두 가지 선택지가 있습니다.

### 옵션 A — 그대로 두기

기존 plist/Task가 멀쩡히 동작 중이라면 그대로 두고 새 .app/.exe를 추가로 설치하지 마세요. 변화 없이 계속 동작합니다.

### 옵션 B — 패키지 빌드로 이전

기존 자동 시작 항목의 `python anyclip.py` 라인을 새 바이너리의 `--headless` 호출로 교체합니다. `--headless` 모드는 기존 옵션(`--token`, `--peer`, `--port`, `--verbose`, `--poll`, `--name`)을 그대로 받으므로 운영 방식이 그대로 유지됩니다.

#### macOS — launchd plist 교체

기존:

```xml
<key>ProgramArguments</key>
<array>
    <string>/path/to/AnyClip/.venv/bin/python</string>
    <string>/path/to/AnyClip/anyclip.py</string>
</array>
```

새 plist (또는 메뉴바의 **Start at Login** 토글이 자동 생성):

```xml
<key>ProgramArguments</key>
<array>
    <string>/Applications/AnyClip.app/Contents/MacOS/AnyClip</string>
    <string>--headless</string>
</array>
```

```bash
launchctl unload ~/Library/LaunchAgents/com.anyclip.plist
# plist 편집
launchctl load ~/Library/LaunchAgents/com.anyclip.plist
```

> 메뉴바 → **Start at Login** 토글이 동일한 plist를 자동으로 작성/제거합니다. 수동 plist 편집을 피하고 싶으면 그쪽이 더 간단합니다.

#### Windows — Task Scheduler 교체

작업 스케줄러에서 기존 작업의 동작을 다음으로 변경:

- 프로그램: `C:\Program Files\AnyClip\AnyClip.exe`
- 인수: `--headless`

또는 트레이 메뉴의 **Start at Login** 토글로 동일한 HKCU Run 키를 자동 등록할 수 있습니다.

## 안정성 / 자가 복구

| 동작 | 설명 |
|------|------|
| Supervisor 재시작 | 데몬이 unhandled 예외로 죽으면 1s→2s→...→60s backoff로 자동 재시작 |
| `--peer` 재연결 | 끊김 후 1s→60s 지수 backoff. 5초 이상 살아있던 세션 후엔 backoff 리셋 |
| Brute-force 쿨다운 | 같은 IP에서 5회 연속 핸드셰이크 실패 → 60초 IP 차단 (inbound만) |
| 로그 회전 | 파일 5MB 초과 시 `.1`/`.2`/`.3`로 회전, 가장 오래된 것 삭제 |
| Idle watchdog | mDNS / TCP가 30s 잠잠하면 자가 핑·재광고로 복구 시도 |
| Quit 깔끔 종료 | mDNS unregister + 링크 close + listener close + PID 파일 정리 |

## 알려진 제한

- LAN 안에서만 동작 (NAT 너머 지원 없음)
- 풀 메시 다중 피어 지원 (동시 최대 8대, 릴레이 없음 — 모든 기기가 같은 LAN에서 서로를 직접 볼 수 있어야 함)
- 다중 피어는 모든 기기가 1.3.0 이상일 때 완전 동작 (구버전 피어는 기존처럼 링크를 독점하려 함)
- 폴더 동기화는 파일만 복원 — 빈 폴더와 심볼릭 링크는 제외, 트리 그대로 받으려면 모든 기기가 1.4.0 이상
- Python 빌드의 macOS 쪽은 클립보드에서 여러 항목을 읽지도 올리지도 못해, 여러 항목을 복사해도 첫 항목만 전송되고 받은 클립도 첫 최상위 항목만 올라감 (네이티브 Swift/C# 빌드는 전부 처리)
- 코드 서명 없음 — 첫 실행 시 Gatekeeper / SmartScreen 우회 절차 1회 필요
- macOS arm64 + Windows x64 한정 (Intel Mac, Windows ARM64, Linux/Android/iOS는 향후 phase)
- 평문 JSON 전송 (인증은 해시, 콘텐츠 자체는 암호화 안 됨 — 신뢰 LAN 가정)

## 네이티브 구현

| 플랫폼 | 위치 | 상태 |
|--------|------|------|
| macOS (Swift · AppKit) | `formacOS/` | v1.0.0 출시 — `brew install --cask anyclip` |
| Windows (C# · .NET 8) | `forwindows/` | 릴리스 자산 `AnyClip-vX.Y.Z-windows-x64-native.zip` |
| Python (레거시, macOS+Windows) | 저장소 루트 | 유지 — Sparkle/WinSparkle 자동 업데이트 채널 |

네이티브 빌드는 Python 구현과 와이어 호환(protocol 1.0)이며 `~/.anyclip/` 설정·토큰을 그대로 공유합니다. 빌드 방법은 각 디렉토리의 README 참고.

## 라이선스

MIT
