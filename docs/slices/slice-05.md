## Parent

#1

## What to build

로그인 시 자동 시작을 등록/해제하는 깊은 모듈 `autostart`를 도입한다. macOS는 `~/Library/LaunchAgents/com.anyclip.plist`를 직접 쓰고 `launchctl load/unload`, Windows는 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 레지스트리 키.

인터페이스:

- `is_enabled() -> bool`
- `enable(executable_path: str) -> None`
- `disable() -> None`

GUI 토글은 Slice 6, 7에서 연결. 본 슬라이스에서는 CLI 옵션 `--install-autostart` / `--uninstall-autostart` / `--autostart-status`로 호출 가능하게 해 단독 사용·테스트 검증.

테스트는 임시 `$HOME` (macOS) / 임시 레지스트리 hive 또는 mock-safe 레지스트리 키(Windows)에서 등록·확인·해제 라운드트립.

## Acceptance criteria

- [ ] `autostart` 모듈 + 유닛 테스트 (macOS: 임시 HOME에 plist 생성/검증/삭제; Windows: 임시 HKCU 경로 또는 동등 격리)
- [ ] CLI 옵션 3종 추가 (`--install-autostart`, `--uninstall-autostart`, `--autostart-status`) 동작
- [ ] enable 시 PYTHONPATH·env vars가 plist/registry value에 안전하게 인용되어 들어감 (공백·특수문자 안전)
- [ ] enable → disable 라운드트립 후 파일/키 잔여물 없음
- [ ] 같은 호출 두 번 (`enable` 두 번) 시 idempotent

## Blocked by

None - can start immediately
