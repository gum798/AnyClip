## Parent

#1

## What to build

**HITL** — 사람의 판단·실행이 필요한 출시 슬라이스.

v1.0.0 릴리스. Slice 1~12가 모두 통과한 후 메인테이너가 직접:

1. macOS·Windows 두 OS에서 빌드된 `.dmg`/`.exe`를 새 사용자 시나리오로 수동 smoke test
   - clean Mac에서 download → Gatekeeper 우회 → onboarding → token 생성 → menubar 동작 → 기존 기기와 link → 텍스트/이미지/파일 클립보드 동기화
   - clean Windows에서 동일 시나리오 (다른 토큰으로 enter 분기 검증)
2. 양쪽 OS에서 "Start at Login" 토글 → 재부팅 → 자동 시작 확인
3. Local Network 권한 거부 → menubar 경고 표시 → "Open Settings" → 권한 부여 → 복구
4. v1.0.0-rc1 임시 release → 자동 업데이트 부트스트랩 dry run (v1.0.0-rc1 → v1.0.0-rc2로 갱신 받기)
5. Release notes 작성: 새 기능, 알려진 한계(코드 서명 없음 → 첫 실행 우회), 기존 CLI 사용자 마이그레이션 안내, Sparkle 부트스트랩 1회 수동 교체 안내
6. `v1.0.0` 태그 push → GHA가 최종 release 생성
7. 기존 CLI 사용자에게 (issue/GitHub Discussions 등에서) 마이그레이션 공지

## Acceptance criteria

- [ ] 두 OS의 clean 환경에서 download → 첫 실행 → onboarding → link → 동기화 전체 흐름 성공
- [ ] 자동 시작 토글이 재부팅 후에도 유효
- [ ] Local Network 권한 복구 흐름 동작
- [ ] v1.0.0-rc1 → v1.0.0-rc2 자동 업데이트 dry run 성공
- [ ] Release notes 작성 완료, 한계·마이그레이션 모두 명시
- [ ] `v1.0.0` 태그 publish, Release 페이지에 .dmg + .exe asset 존재
- [ ] 기존 사용자 마이그레이션 공지 작성

## Blocked by

#13
