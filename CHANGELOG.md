# Changelog

[English](CHANGELOG.md) | [简体中文](CHANGELOG.zh-CN.md) | [日本語](CHANGELOG.ja.md) | [한국어](CHANGELOG.ko.md)

## [1.5.6] — 2026-10-15

### Added

- Control-bar **Subtitle files** button: click for a pull-up menu to generate subtitles with Transub, or export source / translated SRT.

### Fixed

- Setup wizard / model downloads harden HTTPS (TLS 1.2+, system proxy), retry Hugging Face official↔mirror and GitHub release proxies on SSL/connection failures, and show actionable localized errors instead of bare `see inner exception`.
- ASR follows the player’s live duration as mpv revises length after open (avoids stopping a few seconds before the end); after recognition finishes, the MT queue is drained and translated sidecars/cache are flushed to disk.

---

## [1.5.5] — 2026-10-10

### Added

- **Murasaki 4B (JA→ZH) translation model:** recommended when watching Japanese dramas or films (~2.5 GB) for better Japanese→Chinese subtitle quality.

### Improved

- Stream recording remembers the last Save As folder and reuses it next time.

### Fixed

- Whisper no longer conditions on previous text (`WithNoContext`), and consecutive identical source cues are collapsed — fixes stuck-phrase subtitles repeating every ~2 seconds.
- Live preview subtitles reload as soon as the playhead is in a gap (and re-add the SRT instead of relying only on `sub-reload`), so later cues appear after the first line instead of staying blank.
- Recognition prefers the player’s known duration, fixing a bug where subtitles only covered about one hour. (Bug: [https://github.com/dlsandy/Transub-Player/issues/1](https://github.com/dlsandy/Transub-Player/issues/1)). Thanks to **[lingoforlove](https://github.com/lingoforlove)** for the report.
- After switching the translation model, a ready llama-server is checked and restarted when needed.
- Translation output strips unclosed `<think>` reasoning blocks so `index|translation` parsing is not broken.
- Closing the window respects the ASR idle wait budget (no infinite wait); before switching media or starting recognition, any translation process still holding the GPU is stopped.
- Program Files installs with a non-writable leftover `data` folder are no longer treated as portable; logs/settings fall back to `%LocalAppData%\Transub Player\data` so “Open log folder” no longer hits Access Denied.

---



## [1.5.4] — 2026-09-22



### Added

- **Control-bar Transub button:** the language/translate icon next to the subtitle controls opens Transub to generate high-quality / production subtitles (same handoff as the menu item “Generate high-quality subtitles with Transub”).
- **Local clip capture:** press `C` to mark the start, press `C` again at the end to save; `Shift+C` cancels. Prefers saving beside the video when the folder is writable; otherwise writes to the clips folder. Uses ffmpeg stream-copy when installed; otherwise re-encodes (may be slower).



### Fixed

- After a video finishes and playback stops, Play or Space starts again from the beginning.
- Mute is remembered across file switches and restarts (raising the volume clears mute).

---



## [1.5.3] — 2026-09-10



### Added

- When several local subtitle files sit next to the video (e.g. `video.srt`, `video.zh-cn.srt`), switch among them from the control bar or the Subtitles menu.



### Improved

- More stable recognition when switching videos or running playlist prefetch: wait for Whisper to finish releasing before the next job, and free translation GPU when ASR needs Vulkan.
- Playlist subtitle prefetch only queues the next few items, not the entire remaining list.



### Fixed

- Volume changes from the player (slider / wheel / arrow keys) are remembered after restart.
- Opening a livestream no longer fails with “No process is associated with this object”.
- Opening a video attaches same-folder external subtitles in the same load, so a race no longer leaves the on-screen track blank.

---



## [1.5.2] — 2026-09-02



### Improved

- **Translation + recognition coordination:** translation logic starts on demand while recognition is still running, so translated subtitles appear sooner.
- **Automatic translation port switch:** if the translation port is in use, the player picks another port so translation does not fail.



### Fixed

- Fixed a bug where an engine loading overlay could cover the video during playback and could not be dismissed.

---



## [1.5.1] — 2026-09-01



### Added

- First public release: Windows local player — open a video and get live subtitles while watching (built-in Whisper turbo; models download on demand).
- Switch source / translation / dual subtitles (shortcuts `1` / `2` / `3`); Chinese / Japanese / Korean / English mutual translation.
- Stream playback and recording; optional GPU acceleration (Vulkan).
- Multilingual README (English / 简体中文 / 日本語 / 한국어) and product intro image.



### Distribution

- GitHub Release: `dlsandy/Transub-Player` (installer + portable package).
- GitCode repo: `AndyDai/Transub-Player` (source and release notes; large install binaries hosted on GitHub).

---
