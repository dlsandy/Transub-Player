# 更新日志

[English](CHANGELOG.md) | [简体中文](CHANGELOG.zh-CN.md) | [日本語](CHANGELOG.ja.md) | [한국어](CHANGELOG.ko.md)

## [1.5.2] — 2026-09-02

### 改进

- **翻译与识别协同**：优化翻译逻辑，使识别进行中按需启动翻译，使译文字幕更快出现。
- **翻译端口自动更换**：新增翻译端口自动更换，避免出现端口被占用导致无法翻译的情况。

### BUG修复

- 视频播放中出现引擎加载框遮挡住视频内容且无法消除的bug。

---

## [1.5.1] — 2026-09-01

### 新增

- 首次公开发布：Windows 本地播放器，打开影片边看边出字幕（内嵌 Whisper turbo，模型按需下载）。
- 原文 / 译文 / 双语切换（快捷键 `1` / `2` / `3`）；中 / 日 / 韩 / 英互译。
- 流媒体播放与录制；可选 GPU 加速（Vulkan）。
- 多语言 README（English / 简体中文 / 日本語 / 한국어）与产品介绍图。

### 分发

- GitHub Release：`dlsandy/Transub-Player`（安装程序 + 便携包）。
- GitCode 仓库：`AndyDai/Transub-Player`（源码与 Release 说明；大文件安装包托管 GitHub）。

---

