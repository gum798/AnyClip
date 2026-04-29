# AnyClip

LAN 안에서 두 컴퓨터의 **텍스트 클립보드를 양방향으로 자동 동기화**하는 매우 단순한 Python 도구입니다. 한쪽에서 복사하면 반대쪽에 즉시 반영됩니다.

> Phase 1: Windows ↔ macOS, 텍스트 전용, mDNS 자동 발견, 공유 토큰 인증.
> 향후: Linux, Android, iOS 등 모든 OS/디바이스로 확장 예정.

## 특징

- 단일 파일 Python 스크립트 (`anyclip.py`)
- mDNS(Bonjour/Zeroconf)로 자동 피어 발견 — IP 입력 불필요
- 공유 토큰 기반 핸드셰이크로 다른 사용자의 의도치 않은 연결 차단
- 양방향 자동 동기화, 에코 루프 방지 내장
- 의존성 단 2개: `pyperclip`, `zeroconf`

## 요구 사항

- Python 3.9+
- macOS 또는 Windows (LAN/VPN 으로 같은 네트워크에 있을 것)

## 설치

```bash
git clone https://github.com/gum798/AnyClip.git
cd AnyClip
python3 -m venv .venv
# macOS / Linux
source .venv/bin/activate
# Windows (PowerShell)
# .venv\Scripts\Activate.ps1

pip install -r requirements.txt
```

## 사용

양쪽 기기에서 동일한 토큰으로 실행하기만 하면 됩니다.

```bash
# Mac
python anyclip.py --token mysecret

# Windows
python anyclip.py --token mysecret
```

또는 환경 변수로 토큰을 전달 (`ps`에 노출되지 않아 권장):

```bash
export ANYCLIP_TOKEN=mysecret   # macOS
$env:ANYCLIP_TOKEN = "mysecret" # Windows PowerShell
python anyclip.py
```

연결되면 다음과 같은 로그가 보입니다:

```
INFO AnyClip starting (node 3f8a..., name='my-mac')
INFO listening on tcp/24816
INFO mDNS advertised as 'my-mac-3f8a1c2d._anyclip._tcp.local.'
INFO discovered peer 'desktop-7e9b...' at 192.168.0.42:24816
INFO linked with peer name='desktop' id=7e9b1234 (outbound)
```

이제 한쪽에서 텍스트를 복사하면 반대쪽 클립보드에 자동으로 반영됩니다.

### 옵션

| 옵션 | 기본값 | 설명 |
|------|--------|------|
| `--token` | `$ANYCLIP_TOKEN` | 공유 비밀키 (필수) |
| `--port` | `24816` | TCP 리슨 포트 |
| `--name` | hostname | 피어 식별용 표시 이름 |
| `--poll` | `0.5` | 클립보드 폴링 주기 (초) |
| `--verbose`, `-v` | 끔 | DEBUG 로그 활성화 |

## 방화벽 / 권한

### macOS

처음 실행 시 OS가 **로컬 네트워크 권한**을 요청합니다 (Bonjour 사용). 반드시 허용해주세요.
나중에 거부했다면 `시스템 설정 → 개인정보 보호 및 보안 → 로컬 네트워크`에서 Python (또는 Terminal)을 켜주세요.

### Windows

방화벽이 처음 실행 시 인바운드 연결을 차단할 수 있습니다. **개인 네트워크**에 대한 허용을 선택하세요. 사용 포트:

- `24816/TCP` (인바운드 + 아웃바운드, 변경 가능)
- `5353/UDP` 멀티캐스트 (mDNS)

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

## 알려진 제한

- 텍스트만 지원 (이미지/파일/리치 텍스트는 향후 기능)
- LAN 안에서만 동작 (NAT 너머 지원 없음)
- 1:1 피어만 지원 (3대 이상 동시 동기화 미지원)
- 평문 JSON 전송 (인증은 해시, 콘텐츠 자체는 암호화 안 됨 — 신뢰 LAN 가정)

## 라이선스

MIT
