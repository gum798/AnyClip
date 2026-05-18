## Parent

#1

## What to build

`v*` 태그 push를 트리거로 macOS arm64와 Windows x64 빌드를 매트릭스로 돌려 GitHub Releases에 asset을 올리는 GitHub Actions 워크플로를 도입한다. 이번 슬라이스에서는 코드 서명·Sparkle은 제외. pytest를 빌드 직전에 굴려 5개 깊은 모듈에 회귀가 없음을 보장.

워크플로 구성:

- 트리거: `push: tags: ['v*']`
- 매트릭스 1: `runs-on: macos-14` — py2app 빌드 → `dist/AnyClip.app` → `hdiutil`로 `.dmg` 생성 → Release asset 업로드
- 매트릭스 2: `runs-on: windows-latest` — PyInstaller 빌드 → `dist/AnyClip.exe` → Release asset 업로드
- 두 job 공통 pre-step: `pip install -r requirements.txt -r requirements-dev.txt` → `pytest tests/`
- Release는 draft가 아닌 published 상태로 생성 (수동 release notes 편집은 사후)

## Acceptance criteria

- [ ] `.github/workflows/release.yml` 신설
- [ ] 두 매트릭스 잡이 pytest 통과 후 빌드 산출물을 Release asset으로 업로드
- [ ] 동작 검증: 임시 태그 `v0.9.0-rc1` push → workflow 통과 → Releases 페이지에 `.dmg` + `.exe` 자산이 보임
- [ ] 빌드된 `.dmg`를 Mac에 받아 마운트 → AnyClip.app 더블클릭 → 정상 동작 (이번 슬라이스 수동 검증의 게이트)
- [ ] 빌드된 `.exe`를 Windows에 받아 더블클릭 → 정상 동작
- [ ] 임시 태그·테스트 release는 검증 후 제거

## Blocked by

#8, #7
