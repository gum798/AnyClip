## Parent

#1

## What to build

Windows용 tray 앱 GUI shell을 도입하고 PyInstaller로 로컬 `.exe` 빌드 가능하게 한다. macOS 슬라이스와 동일한 사용자 경험: 더블클릭 → tray 아이콘 → 첫 실행 onboarding → 토큰 저장 → 데몬 시작 → 상태 전이 → 종료.

구성요소:

- `pystray` 기반 system tray 앱
- 첫 실행 시 `tkinter` 다이얼로그 onboarding: "Generate new token" / "Enter existing token"
- 동일 `daemon_supervisor` thin wrapper로 `anyclip.py.run()` 호스팅
- 동일 DaemonEvent 구독 → `peer_state.reduce` → tray 아이콘/툴팁 갱신
- 메뉴 항목 macOS와 동일 (Status, Last Sync, Token..., Start at Login 토글, Open Logs (`explorer /select,`), Quit)
- PyInstaller spec: `--noconsole` (콘솔 창 없음), 단일 디렉토리 또는 `--onefile`(빌드 시간/실행 속도 trade-off는 빌드 담당이 결정), 아이콘은 Slice 8에서 교체
- `--headless` 플래그 동작은 macOS와 동일

permission_probe는 Windows에서 no-op이므로 별도 UI 처리 없음. 방화벽 팝업은 OS가 자동.

## Acceptance criteria

- [ ] PyInstaller spec/명령으로 로컬 빌드 성공, `dist/AnyClip.exe` 생성
- [ ] `.exe` 더블클릭 → tray 아이콘 등장, 콘솔 창 안 뜸
- [ ] config 없는 첫 실행 → tkinter onboarding, Generate/Enter 두 분기 동작
- [ ] tray 상태가 `peer_state` 변화에 따라 갱신
- [ ] "Start at Login" 토글이 `autostart` 모듈을 호출해 HKCU\...\Run 키 생성/삭제
- [ ] "Open Logs"가 Explorer에서 로그 파일 노출
- [ ] "Quit" 시 데몬 깔끔 종료, PID 파일 정리
- [ ] `AnyClip.exe --headless --token foo`가 GUI 없이 기존 데몬 모드 동작
- [ ] 첫 실행 시 Windows 방화벽 팝업이 자연스럽게 뜨고 허용 시 mDNS·24816/TCP 통과

## Blocked by

#4, #5, #3
