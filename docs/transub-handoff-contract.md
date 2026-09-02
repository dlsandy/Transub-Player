# Transub ↔ Transub Player 协作契约

本地协作文档（不必进公开 README）。两端改协议或交接行为时，**同一周内两边对齐**。

## 产品边界

| | Transub Player | Transub |
|---|---|---|
| 主路径 | 打开本地片 → 尽快可读字幕 → 译文可降级 → 关掉释放 | 成片级清洗 / 质检 / 编辑 / 批量 |
| ASR | 进程内 Whisper.net turbo | Engine / 完整管线 |
| 清洗 | 轻量预览 sanitize + 可选复用词库 | opaque / TDP / QC 全管线 |
| 不做 | 成片工厂、刮削库、账号 | 万能播放器 |

## 协议：`transub://`

Scheme 由 **Transub** 注册（`package.json` → Electron `protocols`）。

### URL 形状

```
transub://handoff?mode=edit|queue|open
  &media=<abs-path>
  &sub=<abs-path>          # edit 必需；缺则降为 queue
  &src=<lang>
  &tgt=<lang>
  &profile=<contentProfile>
  &manifest=<player-handoff.json>
```

别名：`video`→`media`；`editSub`/`edit-sub`→`sub`；host `edit`/`queue`/`open`/`handoff`。

### mode 语义

| mode | Player 何时发 | Transub 行为 |
|------|---------------|--------------|
| `edit` | 有媒体 + 可读草稿字幕 | 打开字幕编辑器（`sub` + 可选 `media`） |
| `queue` | 有媒体、无草稿 | 主窗口任务队列加入该媒体 |
| `open` | 无媒体 | 仅拉起 / 前置 Transub |

### 回退

1. 优先 `transub://`（注册表存在时）
2. 否则 `Transub.exe` + CLI：`--edit-sub=` / `--edit-video=` 或 `--files=`
3. 找不到 exe → 提示设置「Transub 安装目录」或打开官网

实现：Player `Services/TransubHandoff.cs`；Transub `player-protocol-core.js` + `electron/player-handoff.js` + `main.js` `applyPlayerProtocolHandoff`。

Queue 交接时 Transub 会合并 URL 与 `player-handoff.json`，预填语种 / 内容画像 / 简繁译文目标。  
Edit 交接时编辑器会收到 `playerHandoff` session（状态提示 + 智能翻译覆盖）。

## 草稿与回写

**出站（Player → Transub）**

- 草稿候选顺序：译文 `.*.preview.srt` → dual → 原文 `.srt` → `.display.srt`（均在 `data/preview/{hash}/`）
- 有草稿时写 `player-handoff.json`（version=1，含语种 / profile / 各 draft 路径）并通过 `manifest=` 传递
- 交接成功后 `ArmFinishedSubtitleWatch()` 监听媒体同目录 sidecar

**回写（Transub → Player）**

- Transub 将成片写到媒体旁（`.srt` / `.ass` 等，见 `SubtitleFile.EnumerateSidecarCandidates`）
- Player `FinishedSubtitleMonitor` 检出新/改写文件 → 弹出「成片字幕已就绪」
- **不自动加载**：用户点「加载成片」才切轨；不强制改播放/暂停状态

## 共享资产（可选）

| 资产 | 约定 |
|------|------|
| 安装路径 | Player `TransubInstallPath` / 自动探测 `Transub.exe` |
| 模型目录 | 可共用；缺件各自提示下载；不把 Transub 引擎当 Player ASR |
| glossary / lexicon | 格式兼容则复用；损坏不挡播放 |
| Pro / opaque / TDP | **仅 Transub**；不复制进 Player |

## 版本与发版

- 协议字段增删：两边单测同步；破坏性变更 bump `player-handoff.json` `version` 并在 README/关于注明最低对方版本
- Player 可更勤发版；Transub 至少保持对本契约的解析兼容
- 验收最小集：
  1. 启动一次 Transub（完成协议注册）
  2. Player「用 Transub 生成高质量字幕」→ 有草稿进编辑器，否则进队列
  3. Transub 写回旁路字幕 → Player 提示并可手动加载
  4. 关掉 Player 后本进程拉起的 mpv/llama/Whisper 已释放

## 明确非目标

- Player 不调用 Transub 完整 ASR/GPU 引擎
- 不共享闭源算法源码
- 不要求两端模型版本锁死同一文件（路径约定即可）
