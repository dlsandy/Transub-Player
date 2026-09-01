# Transub Player

[English](README.md) | [简体中文](README.zh-CN.md) | [日本語](README.ja.md) | [한국어](README.ko.md)

**打开影片，字幕就来。** 外语片、日剧、韩剧、动漫剧 —— 没有字幕也能先看。打开视频，画面立刻出来；可读字幕随后跟上。不再为看不懂和找字幕而烦恼，追剧就是如此轻松。

<p align="center">
  <img src="docs/images/cover.jpg" alt="Transub Player — 边看边出双语字幕" width="900">
</p>

> Windows 本地播放器 · 当前版本 1.5.1  
> 从 [GitHub](https://github.com/dlsandy/Transub-Player/releases) 或 [GitCode](https://gitcode.com/AndyDai/Transub-Player/releases) 下载安装程序或便携包即可使用。软件内也可「检查更新」检查并升级至新版本。

> **GitCode 用户**：本页为中文说明。若仓库首页默认显示英文，请点上方「简体中文」，或在 GitCode 项目设置中将默认页面设为 `README.zh-CN.md`（若平台支持）。

---

## 为什么选它

**边看边出字幕**  
字幕由内嵌语音识别实时生成，边播边补。生成状态一目了然——你知道它在工作，而不是干等。

**原文 · 译文 · 双语，一键切换**  
轻松看，切译文；对照学，切双语。翻译暂不可用，原文字幕照常播放，观影不中断。

**中 / 日 / 韩 / 英互译**  
片源语、译文语在播放中随时切换；文件名还能辅助猜测片源语。默认先求「尽快看懂」，需要时再增强。  
后续还将更新支持更多语言。

**直播也能播，还能录**  
常见网络流媒体地址直接播放；想稍后回看，可一键录制到本地。

**深度匹配 Transub 字幕生成器**  
支持用 Transub 字幕生成器生成高质量字幕，字幕生成后播放器自动回应提示播放。

---

## 适合这样的你

- 想马上看懂外语内容，不想到处找字幕
- 没有现成字幕，也不想为「看一集」折腾复杂工具
- 更在意「能不能继续看下去」，而不是「字幕能不能直接发片」

需要可直接发布的中文字幕、完整清洗与质检时，请使用配套产品 [Transub](https://www.transub.cc)。

---

## 下载与开始

1. 在 [GitHub Releases](https://github.com/dlsandy/Transub-Player/releases) 或 [GitCode Releases](https://gitcode.com/AndyDai/Transub-Player/releases) 下载（也可用软件内「检查更新」）：
   - **安装程序**（推荐）：`TransubPlayer-*-win-x64-setup.exe`，一路下一步即可
   - **便携包**：`TransubPlayer-*-win-x64.zip`，解压后运行 `TransubPlayer.exe`
2. 若 SmartScreen 提示「已保护你的电脑」，点「更多信息」→「仍要运行」
3. 按首次配置向导完成推荐设置（可顺带关联常见视频格式）
4. 打开或拖入影片；若尚未安装识别 / 翻译模型，按提示下载后即可边看边出字幕

> 同文件夹里若已有字幕文件，会优先直接加载；需要时也可改回「边看边出」。  
> 安装版的设置与模型在 `%LocalAppData%\Transub Player\data\`；便携版在解压目录下的 `data\`。

### 三步上手

1. 打开或拖入本地影片（或打开流媒体地址）
2. 等字幕出现，跟着画面看下去
3. 按需切换：译文 / 原文 / 双语（快捷键 `1` / `2` / `3`）

---

## 常用快捷键


| 操作 | 按键 |
| --- | --- |
| 打开 | Ctrl+O |
| 播放 / 暂停 | 空格 · 单击画面 |
| 快退 / 快进 | ← / → |
| 译文 / 原文 / 双语 | 1 / 2 / 3 |
| 字幕延后 / 提前 | Z / X |
| 显示 / 隐藏字幕 | V |
| 播放列表 | L |
| 下一个 / 上一个 | N / P |
| 全屏 | F · F11 · Enter · 双击画面 |
| 截图 | S |
| 静音 | M |
| 退出全屏 | Esc |


更多选项见右键菜单与设置窗口（界面语言、文件关联、字幕外观、模型安装等）。

---

## 工作原理


| 层 | 技术 |
| --- | --- |
| 外壳 / 界面 | WPF |
| 画面渲染 | mpv |
| 语音识别 | 内嵌 Whisper（whisper.cpp / GGML turbo，按需下载） |
| 翻译 | llama-server + Hy-MT GGUF（按需） |


主路径：**打开本地片 → 尽快看到可读字幕 → 译文可降级 → 关掉就干净。**

---

## 从源码构建

环境要求：Windows + .NET SDK，首次需联网拉取 mpv 与模型。

```powershell
powershell -ExecutionPolicy Bypass -File tools\fetch-mpv.ps1
dotnet run --project src\TransubPlayer\TransubPlayer.csproj
```

打包发行版（Player + mpv + 内嵌 Whisper，不含模型权重），产物输出到 `artifacts\pack\`（安装程序 + 便携 zip；需本机安装 [Inno Setup 6](https://jrsoftware.org/isinfo.php) 才会生成 setup）：

```powershell
powershell -ExecutionPolicy Bypass -File tools\pack-release.ps1
# 仅便携包 / 仅安装程序：
# powershell -ExecutionPolicy Bypass -File tools\pack-release.ps1 -Target Portable
# powershell -ExecutionPolicy Bypass -File tools\pack-release.ps1 -Target Setup
```

首次识别：设置 → 模型 → 下载 whisper turbo。

---

## 系统要求

- Windows 10 / 11（64 位）
- 首次使用需联网下载识别 / 翻译模型（之后可离线播放已准备好的内容）
- 可选 GPU 加速（按本机能力自动或手动选择）；无独显也可用

---

## 小贴士

- 字幕会**稍晚于画面**出现，属边看边生成的正常现象
- 目标是「能看懂、跟得上」，不是成片发布级字幕
- 没有翻译模型时，仍可先看原文字幕继续播放
- 关掉程序后，播放器拉起的相关进程会一起退出，不在后台偷偷占资源

---

## 一句话

> 打开就能看，字幕随后到。
