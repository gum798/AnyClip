# AnyClip

LAN 안의 두 컴퓨터 사이에서 **클립보드를 양방향으로 자동 동기화**하는 도구.
한쪽에서 복사하면 반대쪽에 즉시 반영됩니다. 텍스트·이미지·파일 모두 지원.

## Quick Start

### macOS — Homebrew (권장)

```bash
brew tap gum798/tap
brew install --cask anyclip
```

네이티브 Swift 빌드(`formacOS/`)가 `/Applications`에 설치됩니다. 코드 서명이 없는 빌드라 첫 실행은 Gatekeeper가 차단합니다 — 우클릭 → 열기 1회, 또는:

```bash
xattr -dr com.apple.quarantine /Applications/AnyClip.app
```

### 직접 다운로드

1. [최신 릴리스](https://github.com/gum798/AnyClip/releases/latest)에서 OS에 맞는 파일을 다운로드합니다.
   - macOS (native Swift): `AnyClip-vX.Y.Z-macos-arm64.zip` (Apple Silicon 전용)
   - macOS (legacy Python): `AnyClip-vX.Y.Z.dmg` (Apple Silicon 전용)
   - Windows: `AnyClip-vX.Y.Z-windows-x64.zip` (x64)
2. 더블클릭으로 설치/압축 해제 후 실행합니다.
3. 첫 실행 창에서:
   - **첫 번째 기기** — "Generate new token"을 눌러 새 토큰을 만듭니다.
   - **두 번째 기기** — "Enter existing token"에 첫 번째 기기에서 만든 토큰을 붙여넣습니다.
4. 메뉴바/트레이 아이콘에 `Searching for peer` → `Linked: <상대 이름>`이 떠야 정상입니다.

이후 한쪽에서 복사하면 반대쪽 클립보드에 자동으로 반영됩니다.

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
- **인증**: 토큰을 SHA-256 해시로만 송신, 평문은 와이어에 흐르지 않음.
- **버전 협상**: handshake에서 양쪽이 protocol_major/minor를 교환해 메이저 불일치 시 link 거부 + 메뉴바 경고.

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
- 1:1 피어만 지원 (3대 이상 동시 동기화 미지원)
- 코드 서명 없음 — 첫 실행 시 Gatekeeper / SmartScreen 우회 절차 1회 필요
- macOS arm64 + Windows x64 한정 (Intel Mac, Windows ARM64, Linux/Android/iOS는 향후 phase)
- 평문 JSON 전송 (인증은 해시, 콘텐츠 자체는 암호화 안 됨 — 신뢰 LAN 가정)

## 라이선스

MIT
