## Parent

#1

## What to build

README의 메인 시나리오를 "Download from Releases → 더블클릭"로 재작성한다. 현재 README의 git clone + venv + pip install 절차는 "Advanced / CLI users" 섹션으로 강등. Gatekeeper / SmartScreen 첫 실행 우회 절차, macOS Local Network 권한 안내, 기존 launchd/Task Scheduler 사용자를 위한 마이그레이션 가이드(`AnyClip.app/Contents/MacOS/AnyClip --headless` 교체)도 함께.

새 README 구조 (대략):

1. What is AnyClip — 한 줄 소개
2. Quick Start — Download .dmg / .exe → 첫 실행 우회 → onboarding → "Done"
3. Permissions — macOS Local Network, Windows 방화벽
4. Updates — Sparkle/WinSparkle이 알아서 함. "Check for Updates..." 수동 트리거
5. Troubleshooting — 기존 표 유지하되 "Open Logs" UI 경로 우선
6. Advanced: CLI mode — 기존 git clone 절차, `--headless`, launchd plist, 작업 스케줄러
7. Migration from CLI users — `python anyclip.py` → `AnyClip --headless`
8. How it works, License — 기존 유지

## Acceptance criteria

- [ ] README의 첫 화면(스크롤 없이)이 Download → 더블클릭으로 끝나는 경로
- [ ] macOS Gatekeeper 우회 절차(Finder 우클릭 → 열기) 정확히 명시
- [ ] Windows SmartScreen 우회 절차(추가 정보 → 실행) 명시
- [ ] Local Network 권한 시스템 설정 경로 안내 + 거부했을 때 menubar UI에서 복구 가능함을 안내
- [ ] 기존 CLI 섹션이 "Advanced" 아래로 보존 (구 사용자 회귀 없음)
- [ ] Migration 가이드: 기존 launchd plist의 `python anyclip.py` 라인을 `AnyClip.app/Contents/MacOS/AnyClip --headless`로 교체하는 정확한 예시
- [ ] 자동 업데이트 부트스트랩 1회 수동 교체 필요 안내

## Blocked by

#11, #12
