# 변경 기록

[English](CHANGELOG.md) | [简体中文](CHANGELOG.zh-CN.md) | [日本語](CHANGELOG.ja.md) | [한국어](CHANGELOG.ko.md)

## [1.5.2] — 2026-09-02

### 개선

- **번역과 인식 연동:** 인식이 진행 중일 때도 필요 시 번역을 시작해, 번역 자막이 더 빨리 나타나도록 최적화.
- **번역 포트 자동 전환:** 포트가 사용 중이면 다른 포트로 바꿔 번역이 실패하지 않도록 개선.

### 수정

- 재생 중 엔진 로딩 오버레이가 영상을 가리고 닫히지 않던 문제를 수정.

---

## [1.5.1] — 2026-09-01

### 추가

- 첫 공개 릴리스: Windows 로컬 플레이어. 영상을 열면 보면서 자막 표시(내장 Whisper turbo, 모델은 필요 시 다운로드).
- 번역 / 원문 / 이중 언어 전환(단축키 `1` / `2` / `3`); 중 / 일 / 한 / 영.
- 스트림 재생 및 녹화; 선택 GPU 가속(Vulkan).
- 다국어 README(English / 简体中文 / 日本語 / 한국어) 및 소개 이미지.



### 배포

- GitHub Release: `dlsandy/Transub-Player`(설치 프로그램 + 포터블 zip).
- GitCode 저장소: `AndyDai/Transub-Player`(소스 및 Release 안내; 대용량 설치 파일은 GitHub에서 제공).

---

