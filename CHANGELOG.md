# Changelog

[English](CHANGELOG.md) | [简体中文](CHANGELOG.zh-CN.md) | [日本語](CHANGELOG.ja.md) | [한국어](CHANGELOG.ko.md)

## [1.7.1] — 2026-11-09

### Improved

- **Prefer discrete GPU on hybrid laptops:** mpv D3D11 and Whisper Vulkan pin to a discrete adapter when DXGI finds one, so playback / recognition are less likely to share the Intel iGPU with other apps. ([#19](https://github.com/dlsandy/Transub-Player/issues/19))
- ASR backend hint warns when another app is encoding with Intel QSV (e.g. HandBrake): switch recognition to CPU or pause that encode first on affected Intel drivers.
- Live subtitle cleanup: stronger name-loop / pollution stripping, kinship and address fixes, wider prompt-leak filters, and an expanded Japanese ASR domain-fix lexicon.

### Fixed

- A broken localization pack (e.g. duplicate `x:Key`) no longer crashes startup; the player stays on the fallback language and logs the failure. ([#20](https://github.com/dlsandy/Transub-Player/issues/20))

---

## [1.7.0] — 2026-11-04

### Added

- **Color grade & LUT:** right-click the video → Color grade. Manual brightness / contrast / saturation / gamma / hue with live preview and named presets; built-in 3D LUT looks (warm cinema, cool night, vintage film, contrast punch, soft pastel); browse custom `.cube` / `.3dl` / `.hald`; optional auto-apply of a same-stem LUT next to the video.

### Improved

- Whisper recognition caps CPU worker threads (aligned with whisper.cpp CLI) so playback stays responsive; settings/status show the loaded Vulkan/GPU vs CPU library more clearly, with an explicit restart hint when the process already pinned the CPU native library. [[https://github.com/dlsandy/Transub-Player/issues/16](https://github.com/dlsandy/Transub-Player/issues/16)]
- Check for updates on startup is on by default.

### Fixed

- Subtitle source “Local” no longer silently starts live ASR/MT when no sidecar is present; stays local-only — switch to “Live” if you need recognition.

---

## [1.6.0] — 2026-10-25

### Added

- **LAN scan-to-play:** control-bar Wi‑Fi entry + shared folders; phone browser picks files. Live subtitles from desktop Whisper / MT with translation / source / bilingual / off. Includes password onboarding, connection/session status, format compatibility hints, same-file cache reuse, and a mobile source/translation frontier bar.
- **Minimize to tray:** minimizing hides the window in the system tray so LAN and recognition can keep running. Right-click the tray icon to show the window or quit. Opening a file brings the main window back.
- **Export subtitles from the context menu:** right-click the video → Export subtitles → source or translated; the control-bar subtitle-file menu uses the same hierarchy.

### Improved

- If no finished subtitle sits next to the video, the player no longer stays on a blank “local subtitles only” state and falls back to live recognition and translation.
- Wider external-subtitle matching: `Subs` / `字幕` folders, and language tags such as `video.chs.srt`, `video_chs.srt`, and `video-chs.srt`. GBK / GB18030 and ASS/SSA files can be read.

### Fixed

- Choosing TranslateGemma 4B no longer loads a larger 9B / 12B / 27B file in the same folder (out-of-memory startup used to leave translations missing).

---

## [1.5.7] — 2026-10-20

### Added

- **Seek bar for non-live streams:** progressive / VOD network media (once duration is known) shows the progress bar and supports drag seek, ←/→, Home/End, and Shift+scroll. Live HLS and screen capture stay without a seek bar.

---

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
