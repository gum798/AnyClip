## Parent

#1

## What to build

macOS 빌드에 Sparkle 자동 업데이트를 통합한다. EdDSA 키쌍을 생성해 public key는 `Info.plist`의 `SUPublicEDKey`로, private key는 `SPARKLE_PRIVATE_KEY` GitHub Actions Secret으로. 릴리스 워크플로가 `sign_update`로 .dmg에 EdDSA 서명을 부여하고 `appcast.xml`을 갱신해 `gh-pages` 브랜치(또는 `docs/` 폴더)에 publish한다. `https://gum798.github.io/AnyClip/appcast.xml`이 안정 URL.

GUI shell의 `updater_bridge`가 PyObjC로 `SUUpdater`를 생성하고 `feedURL`을 위 URL로 설정. 앱 시작 시 백그라운드 체크, menubar 메뉴에 "Check for Updates..." 추가.

이번 슬라이스가 통과한 직후의 첫 릴리스가 *모든 미래 업데이트의 부트스트랩 지점*이라는 점이 위험. 키 주입·서명·appcast 형식 중 하나라도 어긋나면 모든 기존 사용자가 update-loop에 빠질 수 있어 수동 smoke test 필수.

## Acceptance criteria

- [ ] EdDSA 키쌍 생성, public key가 빌드 시점에 Info.plist에 주입
- [ ] `SPARKLE_PRIVATE_KEY` Secret이 GitHub Actions에 등록 (수동, 사람 단계)
- [ ] Sparkle.framework이 `.app` 번들의 `Contents/Frameworks/`에 포함
- [ ] `updater_bridge`가 `SUUpdater`를 띄우고 feedURL이 GitHub Pages URL로 설정
- [ ] Release 워크플로가 `.dmg`에 EdDSA 서명 + appcast.xml 갱신 + GitHub Pages 발행
- [ ] 수동 검증: v0.9.5를 먼저 release → 그 .app을 깔고 → v0.9.6 release → 깔린 앱이 자동 업데이트 알림 → 동의 시 v0.9.6로 교체 후 재시작
- [ ] 잘못된 서명 (테스트로 일부러 truncate한 .dmg) 은 거부되어 업데이트 안 됨

## Blocked by

#9
