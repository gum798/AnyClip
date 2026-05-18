## Parent

#1

## What to build

macOS용 menubar 앱 GUI shell을 도입하고 py2app으로 로컬 `.app` 빌드가 가능하게 한다. 이번 슬라이스에서 끝나면 메인테이너 본인 머신에서 `python setup.py py2app` → `dist/AnyClip.app` 더블클릭 → menubar 아이콘 → onboarding 다이얼로그 → 토큰 저장 → 자동으로 데몬 시작 → menubar 상태가 "Searching" → 다른 기기와 만나면 "Linked: <peer>"로 전이까지 동작.

구성요소:

- `rumps` 기반 menubar 앱이 진입점
- 첫 실행 시 (config 없음) PyObjC `NSAlert` + `NSTextField` 기반 onboarding 다이얼로그: "Generate new token (first device)" / "Enter existing token (second device)"
- `daemon_supervisor` thin wrapper가 `anyclip.py`의 `run()`을 GUI 프로세스 안 asyncio task로 띄움
- DaemonEvent 큐 구독 → `peer_state.reduce` → menubar 라벨/상태 갱신
- 메뉴 항목: Status 라벨, Last Sync 시각, "Token..." (현재 토큰 표시/재설정 메뉴), "Start at Login" 토글 (autostart 모듈 호출), "Open Logs" (`open -R ~/.anyclip/anyclip.log`), "Quit"
- `PermissionMissing(local_network)` 이벤트 수신 시 menubar 상태가 경고 + "Open Local Network Settings" 추가 항목 — `x-apple.systempreferences:com.apple.preference.security?Privacy_LocalNetwork`로 점프
- `setup.py` (py2app) 작성: LSUIElement=true (Dock 미표시), NSLocalNetworkUsageDescription, NSBonjourServices=["_anyclip._tcp"], 번들 ID, 최소 macOS 14
- `--headless` 플래그가 있으면 GUI shell을 우회하고 기존 `anyclip.py` `run()` 직행 — 같은 진입점

## Acceptance criteria

- [ ] `python setup.py py2app` 로컬 빌드 성공, `dist/AnyClip.app` 생성
- [ ] 빌드된 `.app` 더블클릭 → menubar 아이콘 등장, Dock 미표시
- [ ] config 없는 상태 첫 실행 → onboarding 다이얼로그, Generate 버튼 시 새 토큰 저장 후 데몬 시작
- [ ] menubar 상태가 `peer_state` 변화에 따라 갱신 (Searching ↔ Linked ↔ Error)
- [ ] "Start at Login" 토글이 `autostart` 모듈을 호출해 LaunchAgent plist 생성/삭제
- [ ] "Open Logs"가 Finder에서 로그 파일 노출
- [ ] Local Network 권한 거부 상태에서 30초 후 menubar 경고 + "Open Settings" 항목 동작
- [ ] "Quit" 시 데몬·mDNS unregister 깔끔 종료, PID 파일 정리
- [ ] `AnyClip.app/Contents/MacOS/AnyClip --headless --token foo` 가 GUI 없이 기존 데몬 모드로 동작

## Blocked by

#4, #5, #6, #3
