# Changelog

[English](CHANGELOG.md) | [简体中文](CHANGELOG.zh-CN.md) | [日本語](CHANGELOG.ja.md) | [한국어](CHANGELOG.ko.md)

## [1.5.3] — 2026-09-10

### Added

- When several local subtitle files sit next to the video (`video.srt`, `video.zh-cn.srt`, …), switch among them from the control bar or the Subtitles menu.

### Improved

- More stable GPU recognition when switching videos or running playlist prefetch: wait for Whisper to finish releasing before the next job, and free translation GPU when ASR needs Vulkan.
- Playlist subtitle prefetch only queues the next few items, not the entire remaining list.

### Fixed

- Volume changes from the player (slider, wheel, or arrow keys) are remembered after restart.
- Opening a livestream no longer fails with "No process is associated with this object".

---

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

