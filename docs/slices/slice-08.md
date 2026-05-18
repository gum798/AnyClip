## Parent

#1

## What to build

클립보드 모티프의 메인 SVG 아이콘 1종과 tray 상태별 3종(linked / searching / error)을 제작하고, sips/ImageMagick으로 macOS `.icns` 와 Windows `.ico`를 자동 파생하는 빌드 스텝을 추가한다. macOS tray는 template image(모노크롬 PDF) 규칙을 따라 라이트/다크 모드 자동 대응.

산출물:

- `build/icon/anyclip.svg` — 클립보드 + 동기화 모티프 메인 아이콘
- `build/icon/tray/linked.svg`, `tray/searching.svg`, `tray/error.svg` — 모노크롬, 22x22 기준
- `build/icon/anyclip.icns` (16/32/64/128/256/512/1024 + @2x), `build/icon/anyclip.ico` (16/32/48/256)
- `build/icon/tray/linked.pdf` 등 (macOS template image용)
- 파생 자동화 스크립트 (`build/icon/build.sh` 또는 동등) — SVG만 수정하면 모든 파생물 재생성

py2app `setup.py`와 PyInstaller spec이 위 산출물을 번들에 포함하도록 갱신. rumps와 pystray가 상태 변화 시 아이콘을 교체.

## Acceptance criteria

- [ ] `build/icon/anyclip.svg` 1종 + tray 3종 커밋
- [ ] `build/icon/build.sh` (또는 동등 자동화)가 SVG → .icns/.ico/.pdf 파생 산출
- [ ] macOS `.app` 번들이 새 아이콘으로 보임 (Finder, Dock에 표시될 때, About 다이얼로그)
- [ ] macOS menubar tray 아이콘이 라이트/다크 모드 자동 반전
- [ ] Windows `.exe`와 tray 아이콘이 새 .ico로 보임
- [ ] `peer_state`가 Linked/Searching/Error로 바뀔 때 tray 아이콘이 실제로 교체됨

## Blocked by

#8, #7
