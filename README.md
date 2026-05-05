# AnyClip

LAN 안에서 두 컴퓨터의 **텍스트 클립보드를 양방향으로 자동 동기화**하는 매우 단순한 Python 도구입니다. 한쪽에서 복사하면 반대쪽에 즉시 반영됩니다.

> Phase 1: Windows ↔ macOS, 텍스트 전용, mDNS 자동 발견, 공유 토큰 인증.
> Phase 2.1: 자가 재시작 / 로그 회전 / `--peer` 폴백 / 토큰 brute-force 쿨다운 / 진단 가시화.
> 향후: Linux, Android, iOS 등 모든 OS/디바이스로 확장 예정.

## 특징

- 단일 파일 Python 스크립트 (`anyclip.py`)
- mDNS(Bonjour/Zeroconf)로 자동 피어 발견 — IP 입력 불필요
- mDNS 차단 환경에서도 `--peer <ip>`로 수동 폴백
- 공유 토큰 기반 핸드셰이크로 다른 사용자의 의도치 않은 연결 차단 + 5회/60초 brute-force 쿨다운
- 양방향 자동 동기화, 에코 루프 방지 내장
- 데몬이 unhandled 예외로 죽으면 supervisor가 1s→60s backoff로 자동 재시작
- 로그는 `~/.anyclip/anyclip.log`에 5MB×3로 회전
- 의존성 단 2개: `pyperclip`, `zeroconf`

## 요구 사항

- Python 3.9+
- macOS 또는 Windows (LAN/VPN 으로 같은 네트워크에 있을 것)

## 빠른 시작

양쪽 기기에서 **동일한 토큰**으로 실행하기만 하면 됩니다. 아래는 새 머신에서 처음부터 끝까지의 단일 시퀀스입니다.

### macOS

```bash
git clone https://github.com/gum798/AnyClip.git
cd AnyClip
python3 -m venv .venv
source .venv/bin/activate
pip install -r requirements.txt

export ANYCLIP_TOKEN=mysecret
python anyclip.py --verbose
```

### Windows (PowerShell)

```powershell
git clone https://github.com/gum798/AnyClip.git
cd AnyClip
python -m venv .venv
.venv\Scripts\Activate.ps1
pip install -r requirements.txt

$env:ANYCLIP_TOKEN = "mysecret"
python anyclip.py --verbose
```

### Windows (cmd)

```cmd
git clone https://github.com/gum798/AnyClip.git
cd AnyClip
python -m venv .venv
.venv\Scripts\activate.bat
pip install -r requirements.txt

set ANYCLIP_TOKEN=mysecret
python anyclip.py --verbose
```

> 토큰은 `--token mysecret`로도 줄 수 있지만, 환경 변수 쪽이 `ps`/`Get-Process` 인자에 노출되지 않아 권장됩니다.

**첫 실행 시**: macOS는 로컬 네트워크 권한 팝업을, Windows는 방화벽 허용 팝업을 띄웁니다 — 둘 다 허용해 주세요.

### 연결 확인

연결되면 다음과 같은 로그가 보입니다:

```
INFO AnyClip starting (node 3f8a..., name='my-mac')
INFO mDNS advertised as 'my-mac-3f8a1c2d._anyclip._tcp.local.' ip=192.168.0.10 server=anyclip-3f8a1c2d.local.
INFO listening on tcp/24816
INFO discovered peer 'desktop-7e9b...' at 192.168.0.42:24816
INFO linked with peer name='desktop' id=7e9b1234 (outbound)
```

이제 한쪽에서 텍스트를 복사하면 반대쪽 클립보드에 자동으로 반영됩니다.

종료하려면 실행한 터미널에서 `Ctrl+C`. 다음 실행부터는 `cd AnyClip` 후:

```bash
# macOS
source .venv/bin/activate && python anyclip.py --verbose

# Windows PowerShell
.venv\Scripts\Activate.ps1; python anyclip.py --verbose
```

### mDNS 차단 환경

회사망/게스트 Wi-Fi 등에서 mDNS 멀티캐스트가 막혀 자동 발견이 안 될 수 있습니다. 이때 `--peer`로 상대 IP를 직접 지정하세요:

```bash
# Mac에서 Windows IP를 직접 지정
python anyclip.py --token mysecret --peer 192.168.0.42

# 포트가 기본(24816)과 다르면 host:port
python anyclip.py --token mysecret --peer 192.168.0.42:24816

# 여러 번 지정 가능 (반복 사용)
python anyclip.py --token mysecret --peer 192.168.0.42 --peer 192.168.0.50
```

`--peer`는 mDNS와 공존합니다. 끊기면 1s→60s 지수 backoff로 자동 재연결.

### 옵션

| 옵션 | 기본값 | 설명 |
|------|--------|------|
| `--token` | `$ANYCLIP_TOKEN` | 공유 비밀키 (필수) |
| `--port` | `24816` | TCP 리슨 포트 |
| `--name` | hostname | 피어 식별용 표시 이름 |
| `--poll` | `0.5` | 클립보드 폴링 주기 (초) |
| `--peer HOST[:PORT]` | 없음 | 수동 폴백 피어. 반복 가능. mDNS와 공존. |
| `--verbose`, `-v` | 끔 | 콘솔 DEBUG 로그 활성화 (파일 로그는 항상 DEBUG) |

## 로그

- 콘솔(stderr): INFO 이상 (`--verbose`면 DEBUG)
- 파일: `~/.anyclip/anyclip.log` — 항상 DEBUG, 5MB × 3 backup으로 자동 회전
- 진단할 일이 있으면 `tail -f ~/.anyclip/anyclip.log` (Windows: `Get-Content -Wait`)

## 안정성 / 자가 복구

| 동작 | 설명 |
|------|------|
| Supervisor 재시작 | 데몬이 unhandled 예외로 죽으면 1s→2s→4s→...→60s backoff로 자동 재시작 |
| `--peer` 재연결 | 끊김 후 1s→60s 지수 backoff. 5초 이상 살아있던 세션 후엔 backoff 리셋 |
| Brute-force 쿨다운 | 같은 IP에서 5회 연속 핸드셰이크 실패 → 60초 IP 차단 (inbound만) |
| 로그 회전 | 파일 5MB 초과 시 `.1`/`.2`/`.3`로 회전, 가장 오래된 것 삭제 |
| 클립보드 read 실패 알림 | 5회 연속 실패 시 WARNING 1회 (스팸 방지) |
| Ctrl+C 깔끔 종료 | mDNS unregister + 링크 close + listener close |

## 방화벽 / 권한

### macOS

처음 실행 시 OS가 **로컬 네트워크 권한**을 요청합니다 (Bonjour 사용). 반드시 허용해주세요.
나중에 거부했다면 `시스템 설정 → 개인정보 보호 및 보안 → 로컬 네트워크`에서 Python (또는 Terminal)을 켜주세요.

### Windows

방화벽이 처음 실행 시 인바운드 연결을 차단할 수 있습니다. **개인 네트워크**에 대한 허용을 선택하세요. 사용 포트:

- `24816/TCP` (인바운드 + 아웃바운드, 변경 가능)
- `5353/UDP` 멀티캐스트 (mDNS)

## 트러블슈팅

| 증상 | 확인 |
|------|------|
| 양쪽 다 떴는데 `discovered peer` 안 뜸 | mDNS 멀티캐스트 차단 가능성. `--peer <상대 IP>` 시도 |
| `connect to X:Y failed` 가 보임 | 방화벽 / 포트 차단. 24816 인바운드 허용 확인 |
| `auth failed from peer` 반복 | 양쪽 토큰이 다름. `ANYCLIP_TOKEN` 또는 `--token` 일치 확인 |
| `auth gate: <ip> blocked` | 5회 연속 토큰 실패 후 60초 IP 차단. 토큰 맞추고 60초 대기 |
| `peer resolve failed` | 상대 광고가 IP 없이 떠있음. 양쪽 모두 최신 코드인지 확인 (Phase 1 fix) |
| `clipboard read failing: 5 consecutive errors` | OS 클립보드 권한 거부 / pyperclip 백엔드 누락. macOS 보호 권한 확인 |
| Ctrl+C 후에도 데몬이 남음 | 보고해주세요 — 종료 처리는 Phase 2.1 step 7에서 보강됨 |

## 동작 원리

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
- **에코 루프 방지**: 마지막 수신 텍스트의 SHA-256 해시를 기억해 동일 텍스트를 다시 송신하지 않음.
- **동시 connect 경합**: 두 노드가 서로에게 동시에 연결될 때 `node_id` 사전식 비교로 한 링크만 유지.
- **인증**: 토큰을 SHA-256 해시로만 송신, 평문은 와이어에 흐르지 않음.
- **Supervisor**: `main()`이 `asyncio.run(run())`을 try/except로 감싸 unhandled 예외 시 backoff 재시작.

## OS 레벨 자동 시작 (선택)

### macOS — launchd

`~/Library/LaunchAgents/com.anyclip.plist`:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key><string>com.anyclip</string>
    <key>ProgramArguments</key>
    <array>
        <string>/path/to/AnyClip/.venv/bin/python</string>
        <string>/path/to/AnyClip/anyclip.py</string>
    </array>
    <key>EnvironmentVariables</key>
    <dict>
        <key>ANYCLIP_TOKEN</key><string>YOUR-SECRET-HERE</string>
    </dict>
    <key>RunAtLoad</key><true/>
    <key>KeepAlive</key><true/>
    <key>StandardErrorPath</key><string>/tmp/anyclip.err</string>
</dict>
</plist>
```

```bash
launchctl load ~/Library/LaunchAgents/com.anyclip.plist
launchctl start com.anyclip
```

### Windows — 작업 스케줄러

작업 스케줄러 → 기본 작업 만들기 → 트리거 "로그온할 때" → 동작 "프로그램 시작":

- 프로그램: `C:\path\to\AnyClip\.venv\Scripts\python.exe`
- 인수 추가: `C:\path\to\AnyClip\anyclip.py`
- 시작 위치: `C:\path\to\AnyClip`

환경 변수 `ANYCLIP_TOKEN`은 사용자 환경에 등록 (시스템 → 환경 변수).

## 알려진 제한

- 텍스트만 지원 (이미지/파일/리치 텍스트는 향후 기능)
- LAN 안에서만 동작 (NAT 너머 지원 없음)
- 1:1 피어만 지원 (3대 이상 동시 동기화 미지원)
- 평문 JSON 전송 (인증은 해시, 콘텐츠 자체는 암호화 안 됨 — 신뢰 LAN 가정)

## 라이선스

MIT
