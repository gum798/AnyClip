## Parent

#1

## What to build

Windows 빌드에 WinSparkle 자동 업데이트를 통합한다. Slice 10에서 생성된 동일 EdDSA 키쌍 사용. WinSparkle.dll을 PyInstaller 번들에 포함하고 `ctypes`로 `win_sparkle_init` → `win_sparkle_set_appcast_url` → `win_sparkle_check_update_with_ui` 호출.

같은 `appcast.xml`이 macOS와 Windows 양쪽을 다룬다 (Sparkle/WinSparkle 둘 다 같은 schema). asset URL은 각 OS별로 분기.

GUI shell의 tray 메뉴에 "Check for Updates..." 항목 추가.

## Acceptance criteria

- [ ] WinSparkle.dll이 PyInstaller 번들에 포함되어 `.exe`와 같은 디렉토리에 배치
- [ ] `updater_bridge` Windows 분기가 `ctypes`로 WinSparkle 초기화하고 appcast URL 설정
- [ ] EdDSA public key가 빌드 시점에 WinSparkle init에 주입 (Sparkle과 동일 키)
- [ ] Release 워크플로가 `.exe`에도 EdDSA 서명 부여 + appcast.xml에 Windows asset 항목 포함
- [ ] 수동 검증: v0.9.5 Windows 빌드를 깔고 → v0.9.6 release → 자동 업데이트 동작
- [ ] 잘못된 서명의 `.exe`는 WinSparkle이 거부

## Blocked by

#9
