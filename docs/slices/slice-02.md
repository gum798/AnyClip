## Parent

#1

## What to build

토큰과 설정의 영속화를 담당하는 깊은 모듈 `config_store`를 도입하고, `anyclip.py`가 환경변수/CLI 인자에 토큰이 없으면 `~/.anyclip/config.json`에서 자동 로드되도록 한다. 헤드리스 사용자가 한 번 저장하면 다음 실행부터 토큰을 다시 줄 필요가 없는 상태.

`config_store` 인터페이스:

- `load() -> Config | None` — 파일 없거나 손상이면 `None`
- `save(Config) -> None` — 0600 권한으로 atomic write
- `generate_token() -> str` — `secrets.token_urlsafe(32)` 수준의 엔트로피

`anyclip.py`의 토큰 결정 우선순위는 다음 순서:

1. `--token` CLI 인자
2. `ANYCLIP_TOKEN` 환경변수
3. `config_store.load().token`
4. 위 셋 다 없으면 FatalStartupError로 종료, stderr에 "토큰이 없습니다. `anyclip --save-token <TOKEN>` 또는 GUI 앱을 사용하세요" 안내

신규 CLI 옵션 `--save-token <TOKEN>`을 추가해 헤드리스 사용자가 한 번 저장하고 빠져나갈 수 있게 한다.

GUI shell의 onboarding 다이얼로그는 본 슬라이스 범위 밖 — Slice 6, 7에서 같은 `config_store`를 호출.

## Acceptance criteria

- [ ] `config_store` 모듈 + 유닛 테스트 (라운드트립, 0600 권한, 손상 JSON graceful, 토큰 엔트로피)
- [ ] `anyclip.py`가 토큰 우선순위 1→4 순서로 해결
- [ ] `--save-token <TOKEN>` 옵션 추가 — 저장만 하고 즉시 종료, exit code 0
- [ ] 토큰 자동 로드된 경우 로그에 "token loaded from config" INFO 한 줄 (값은 절대 미출력)
- [ ] 환경변수만 쓰던 기존 사용자에게 회귀 없음 (우선순위 2가 우선순위 3보다 위)

## Blocked by

#2
