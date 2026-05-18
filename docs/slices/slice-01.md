## Parent

#1

## What to build

Handshake JSON에 앱 버전과 프로토콜 버전 정보를 추가하고, 양쪽이 합의할 수 있는 순수 함수 `version_negotiator`를 도입한다. 두 인스턴스가 서로 다른 버전이어도 link 동작이 명확히 정의되어야 한다.

기존 handshake JSON에 다음 필드 추가 (decision encoded by PRD §Implementation Decisions):

```
{
  "version": 1,               // 기존 PROTOCOL_VERSION, 의미는 protocol_major와 동치로 다룸
  "app_version": "1.0.0",     // 신규, semver, 빌드 시점에 주입
  "protocol_major": 1,        // 신규, major 호환성 키
  "protocol_minor": 0         // 신규, minor 호환성 키
}
```

`version_negotiator` 모듈은 순수 함수로, 양측의 `(app_version, protocol_major, protocol_minor)`를 받아 다음 enum 중 하나를 반환:

- `Compatible`
- `PeerOlderMinor` / `PeerNewerMinor` (link 유지)
- `PeerOlderMajor(refuse)` / `PeerNewerMajor(refuse)` (link 거부)

기존 단일 버전 정수 사용처는 모두 `protocol_major`로 redirect. 현재 한 버전만 존재하므로 모든 케이스가 `Compatible`로 떨어져 실 사용자 영향 0.

## Acceptance criteria

- [ ] `version_negotiator` 모듈이 신규 추가되고 9가지 케이스 매트릭스 유닛 테스트 통과
- [ ] `anyclip.py` handshake JSON에 `app_version`, `protocol_major`, `protocol_minor` 필드 포함
- [ ] `app_version`은 단일 출처(모듈 상수)에서 읽어 빌드 시점에 주입 가능한 형태
- [ ] 두 인스턴스를 같은 머신/다른 머신에서 띄워 link 성립 후 로그에 `peer_app_version` 출력
- [ ] 기존 PROTOCOL_VERSION 정수 호환 — 한쪽이 신규 필드 없는 구버전이어도 link 성립 (default `protocol_major=version`, `protocol_minor=0`로 해석)
- [ ] `requirements-dev.txt` 신설 + pytest 도입 + CI 없이도 `pytest tests/test_version_negotiator.py` 통과

## Blocked by

None - can start immediately
