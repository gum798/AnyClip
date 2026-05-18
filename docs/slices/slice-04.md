## Parent

#1

## What to build

macOS Local Network 권한을 거부했거나 mDNS가 잠잠한 상황을 데몬이 스스로 판정해 `PermissionMissing` 이벤트를 emit하도록 `permission_probe` 모듈을 도입하고 코어에 통합한다.

판정 규칙:

- 데몬 시작 후 30초 동안 mDNS 광고/발견 이벤트가 단 한 번도 없음 → `blocked_local_network`
- 시작 후 활성 네트워크 인터페이스 0개 → `no_network`
- 그 외 → `ok`

Windows에서는 no-op (Local Network 권한 개념 없음, 방화벽은 OS가 팝업으로 처리). probe는 모든 OS에서 import 가능하되 macOS에서만 의미 있는 결과 반환.

판정 결과가 `blocked_local_network`이면 `PermissionMissing(kind="local_network")` 이벤트를 emit해 `peer_state`가 `Error("local_network")`로 전이한다. GUI 쉘에서의 "Open Settings" 버튼·UI 처리는 본 슬라이스 범위 밖 — Slice 6에서 처리.

## Acceptance criteria

- [ ] `permission_probe` 순수 모듈 + 유닛 테스트 (fake clock + fake mDNS event feed, 3가지 결과 케이스)
- [ ] `anyclip.py` 시작 시 probe 태스크 띄움, 30초 후 결과를 단 1회 emit
- [ ] 정상 mDNS 트래픽이 흐르면 probe가 `ok`로 종료하고 이벤트 emit 안 함
- [ ] macOS에서 시스템 설정 → Local Network에서 Python.app 권한 끄고 실제 실행 시 `PermissionMissing` 이벤트 발생 (수동 검증)
- [ ] Windows에서 probe가 no-op이고 시작 시 에러 없음

## Blocked by

#5
