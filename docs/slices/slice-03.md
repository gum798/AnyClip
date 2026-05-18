## Parent

#1

## What to build

GUI 쉘이 데몬 상태를 구독해 menubar 라벨/아이콘을 갱신할 수 있도록, 코어 `anyclip.py`가 의미 있는 사건마다 구조화된 `DaemonEvent`를 emit하게 한다. 동시에 이벤트 시퀀스를 받아 UI 상태(`Idle` / `Searching` / `Linked(peer, since)` / `Error(reason)`)를 계산하는 순수 reducer `peer_state`를 도입한다.

emit 대상 이벤트 (이름은 prototype에서 굳어진 값):

- `PeerDiscovered(name, addr)`
- `LinkUp(peer_name, peer_id)`
- `LinkDown(reason)`
- `HandshakeFailed(addr, reason)`
- `PermissionMissing(kind)` — 다음 슬라이스에서 채움

전달 채널은 in-process `asyncio.Queue` (subprocess 미사용). 헤드리스 모드에서는 구독자 없이도 drop 가능. GUI shell이 구독해 `peer_state.reduce()`로 상태 머신 한 칸 굴림.

reducer는 stateless 함수: `reduce(prev_state, event) -> new_state`. 같은 사건 시퀀스는 항상 같은 결과 — 골든 테스트 가능.

## Acceptance criteria

- [ ] `DaemonEvent` 데이터클래스(또는 union) 정의 + 4종 이벤트 emit 지점 추가
- [ ] `peer_state.reduce` 순수 함수 + 골든 테스트 (각 시나리오: 발견→연결, 연결→끊김, 핸드셰이크 5회 실패, 권한 누락)
- [ ] 헤드리스 모드에서 구독자 없어도 이벤트 누적이 메모리 누수 안 됨 (queue maxsize 또는 lossy drop)
- [ ] 이벤트 emit이 기존 로그 출력을 줄이지 않음 (사람용 로그는 그대로, 이벤트는 추가 채널)
- [ ] 데모: 두 인스턴스를 띄워 한쪽의 이벤트 큐를 tail하면 4종 이벤트가 시간순으로 흐름

## Blocked by

#2
