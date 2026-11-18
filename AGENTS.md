# AGENTS.md — Transub Player

给在本仓库工作的人与 AI Agent 的总览。

## 这是什么

**Transub Player**：打开本地影片，用 **内嵌 Whisper.net**（whisper.cpp）边看边出**字幕**。

| 平台 | UI | 工程 |
|---|---|---|
| Windows | **WPF** | `src/TransubPlayer` |
| macOS | **Avalonia** | `src/TransubPlayer.Desktop` |

共享业务在 **`src/TransubPlayer.Core`**（ASR / mpv IPC / 翻译 / 设置）。

- 画面：**mpv**（`--wid` 嵌入 + JSON IPC）
- 识别：**AsrPipeline**（进程内，GGML turbo 按需下载）
- 翻译：**llama-server** + Hy-MT GGUF（按需）
- Windows ASR 加速：Vulkan；Mac：CoreML → CPU

### 产品钉子

主路径：**打开本地片 → 尽快看到可读字幕 → 译文可降级 → 关掉就干净。**

默认包：**Player + mpv + Whisper 原生库**。**不含** ASR/翻译模型权重。

禁止 Electron。

## 目录地图

```
src/TransubPlayer.Core/     跨平台 Services + Platform 抽象 + Loc
src/TransubPlayer/          Windows WPF 壳 + Platform/Windows
src/TransubPlayer.Desktop/  Avalonia Mac 壳 + Platform/Mac
tools/fetch-mpv*.ps1|sh     拉取 mpv
tools/pack-release.ps1      Windows 打包
tools/pack-mac.sh           macOS .app / .dmg
tools/mac/Info.plist        Mac bundle 元数据 / URL scheme
```

## 模块边界

1. **UI → Core Services → mpv/Whisper/llama**：单向依赖。
2. **Services 不引用具体控件**；视频面通过 `IVideoSurface`。
3. **平台适配**经 `PlatformServices` 注册（IPC / 子进程 / ASR GPU / OS 集成）。
4. **退出即释放**：关窗释放 Whisper 与 llama；只杀本进程拉起的 mpv/llama。

## 本地跑起来

### Windows

```powershell
powershell -ExecutionPolicy Bypass -File tools\fetch-mpv.ps1
dotnet run --project src\TransubPlayer\TransubPlayer.csproj
```

### macOS

```bash
bash tools/fetch-mpv-mac.sh
dotnet run --project src/TransubPlayer.Desktop/TransubPlayer.Desktop.csproj
# 打包
bash tools/pack-mac.sh osx-arm64
```

首次识别：设置 → 下载 whisper turbo。

## 与 Transub 协作

成片交接协议见 `docs/transub-handoff-contract.md`（`transub://` + CLI 回退）。
