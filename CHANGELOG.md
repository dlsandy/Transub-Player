# Changelog

[English](CHANGELOG.md) | [简体中文](CHANGELOG.zh-CN.md) | [日本語](CHANGELOG.ja.md) | [한국어](CHANGELOG.ko.md)

## [1.5.2] — 2026-09-02

### Improved

- **Translation + recognition coordination:** Translation starts on demand while recognition is still running, so translated subtitles appear sooner.
- **Automatic translation port switch:** If the translation port is in use, the player picks another port so translation does not fail.

### Fixed

- Fixed a bug where an engine loading overlay could cover the video during playback and could not be dismissed.

---

## [1.5.1] — 2026-09-01

### Added

- First public release: Windows desktop player with live subtitles while watching (built-in Whisper turbo; models download on demand).
- Switch translation / source / dual subtitles (shortcuts `1` / `2` / `3`); Chinese / Japanese / Korean / English.
- Stream playback and recording; optional GPU acceleration (Vulkan).
- Multilingual README (English / 简体中文 / 日本語 / 한국어) and product screenshot.

### Distribution

- GitHub Release: `dlsandy/Transub-Player` (installer + portable zip).
- GitCode repo: `AndyDai/Transub-Player` (source and release notes; large binaries hosted on GitHub).

---

