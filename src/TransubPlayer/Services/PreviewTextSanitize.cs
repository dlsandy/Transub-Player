using System.Text.RegularExpressions;

namespace TransubPlayer.Services;

/// <summary>
/// Lightweight preview-side port of Transub ASR cleanup + post-MT sanitize.
/// Full opaque NSFW / fluency stacks stay in Transub desktop; Player only needs
/// streaming-path parity (engine cleans only after ASR finishes).
/// </summary>
internal static class PreviewTextSanitize
{
    private static readonly string[] MultiLoopStems =
    [
        "玲奈", "莉奈", "佳奈", "纱奈", "沙奈", "真理", "理子", "绘真", "绘里",
        "美咲", "结衣", "阳菜", "春奈", "桃子", "琴音", "黄奈", "奏翔", "斗碧",
        "乃爱", "爱奈", "露娜", "环奈", "花音", "雏子", "真寻", "圣音", "明日香",
    ];

    private static readonly string[] SingleLoopStems =
    [
        "葵", "嬉", "笑", "爱", "愛", "盐", "塩", "鸭", "鴨", "舞", "桃", "玲", "樱", "凛", "花", "月",
    ];

    private static readonly string[] LoopTokens = MultiLoopStems
        .Concat(SingleLoopStems)
        .OrderByDescending(t => t.Length)
        .ThenBy(t => t, StringComparer.Ordinal)
        .ToArray();

    private static readonly Regex HonorificRe = new(@"(さん|くん|ちゃん|君|様|氏)", RegexOptions.Compiled);
    private static readonly Regex PunctOnlyRe = new(@"^[…⋅・.\s。！？!?～〜ー\-'""\u3000]*$", RegexOptions.Compiled);
    private static readonly Regex NameBareRe = new(@"[…⋅・.。！？!?\s～〜\-ー'""\u3000]+", RegexOptions.Compiled);
    private static readonly Regex MultiWs = new(@"[^\S\n]{2,}", RegexOptions.Compiled);

    private static readonly Regex PromptLeakMark = new(
        @"将下面|术语表|译名表|请勿删除|不要解释|只输出译文|日译中字幕翻译|汉化组|严禁净化|忠实语气模式|根据以下|无须复译|照译|" +
        @"将以下带序号|注意只需要输出翻译|风格要严格符合|【待翻译文本】|【翻译任务】|行数与顺序必须|" +
        @"专有名词(?:仅|只)使用原文|禁止臆造角色名|忠实完整传达成人向",
        RegexOptions.Compiled);

    private static readonly Regex HonorificEcho = new(
        @"([\u4e00-\u9fffぁ-んァ-ンー]{1,12})(同学|小姐|先生|桑|君|酱|大人|老师)(?:[\s…·・.。]{0,8}\2)+",
        RegexOptions.Compiled);

    private static readonly Regex BareHonorificEcho = new(
        @"(同学|小姐|先生|桑|君|酱|大人|老师)(?:[\s…·・.。]{0,8}\1)+",
        RegexOptions.Compiled);

    /// <summary>
    /// Soft-voice / whisper ASR often hallucinates long mono runs (プププ… / あああ… / HHH…)
    /// or space-separated token loops (今 今 今… / 琴音 琴音…). Collapse or drop before display/MT.
    /// </summary>
    private static readonly Regex MonoCharRun = new(@"(.)\1{5,}", RegexOptions.Compiled);
    private static readonly Regex SpacedTokenLoop = new(
        @"(\S{1,12})(?:[ \t\u3000]+?\1){4,}",
        RegexOptions.Compiled);
    private static readonly Regex TightNgramLoop = new(
        @"([\u3040-\u30ff\u4e00-\u9fffA-Za-zぁ-んァ-ンー]{1,4})\1{4,}",
        RegexOptions.Compiled);
    private static readonly Regex BareNorm = new(
        @"[\s…⋅・.。，,！!？?～〜\-ー'""\u3000]+",
        RegexOptions.Compiled);

    /// <summary>Clean ASR source text before display / MT.</summary>
    public static string CleanAsr(string text, AppSettings settings, string? contentProfile = null)
    {
        if (!settings.TextSanitizeEnabled) return text ?? "";
        var cur = (text ?? "").Trim();
        if (cur.Length == 0) return "";

        cur = StripNameLoops(cur);
        if (cur.Length == 0) return "";

        cur = StripHallucinationLoops(cur);
        if (cur.Length == 0) return "";

        cur = JaAsrDomainLexicon.Apply(cur);
        cur = AvGlossaryLexicon.Apply(cur, contentProfile);
        cur = MultiWs.Replace(cur, " ").Trim();

        var glossary = SubtitleGlossaryStore.Load(settings);
        cur = glossary.Apply(cur);
        cur = cur.Trim();
        // Drop ellipsis / punct-only ASR debris so it never enters the MT queue.
        if (IsPlaceholderText(cur) || LooksLikeAsrHallucination(cur)) return "";
        return cur;
    }

    /// <summary>
    /// True for ellipsis / punct-only placeholders (ASR debris or MT refuse).
    /// These must not be shown as「译文」— display should fall back to source.
    /// </summary>
    public static bool IsPlaceholderText(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return true;
        return PunctOnlyRe.IsMatch(t);
    }

    /// <summary>Reject MT lines that clearly belong to a different target script (e.g. 汉字 when target is English).</summary>
    public static bool LooksLikeWrongTargetScript(string? text, string? translateTarget)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return false;
        var tgt = TranslateTargets.Normalize(translateTarget);
        var han = System.Text.RegularExpressions.Regex.Matches(t, @"[\u4e00-\u9fff]").Count;
        var kana = System.Text.RegularExpressions.Regex.Matches(t, @"[\u3040-\u30ff]").Count;
        var hangul = System.Text.RegularExpressions.Regex.Matches(t, @"[\uAC00-\uD7AF]").Count;
        var latin = System.Text.RegularExpressions.Regex.Matches(t, @"[A-Za-z]").Count;
        var len = Math.Max(1, t.Length);

        return tgt switch
        {
            TranslateTargets.En => han >= 2 && han * 2 >= len / 3 && latin < han,
            TranslateTargets.Ko => hangul == 0 && han >= 2 && han * 2 >= len / 3,
            TranslateTargets.Ja => kana == 0 && han == 0 && latin * 2 >= len / 3,
            TranslateTargets.Zh or TranslateTargets.ZhHant
                => latin * 3 >= len && han == 0 && kana == 0,
            _ => false,
        };
    }

    /// <summary>Sanitize one MT line against its source.</summary>
    public static string SanitizeMt(string zh, string source, AppSettings settings, string? contentProfile = null)
    {
        var toZh = TranslateTargets.IsChinese(settings);
        if (!settings.TextSanitizeEnabled)
        {
            var raw = (zh ?? "").Trim();
            return toZh ? CommasPeriodsToSpaces(raw) : raw;
        }

        var cur = (zh ?? "").Trim();
        if (cur.Length == 0 || IsPlaceholderText(cur)) return "";

        if (!toZh)
        {
            cur = StripPromptLeak(cur);
            cur = StripHallucinationLoops(cur);
            cur = MultiWs.Replace(cur, " ").Trim();
            return IsPlaceholderText(cur) || LooksLikeAsrHallucination(cur) ? "" : cur;
        }

        var src = source ?? "";
        if (cur.Length == 0) return "";

        cur = StripPromptLeak(cur);
        if (cur.Length == 0 || IsPlaceholderText(cur)) return "";

        if (LooksLikeSourceEcho(cur, src))
            return "";

        cur = StripHallucinationLoops(cur);
        if (cur.Length == 0 || IsPlaceholderText(cur)) return "";

        cur = CollapseHonorificEchoes(cur);
        cur = StripSpuriousNamePrefixes(cur, src);
        cur = StripResidualJaInZh(cur);
        cur = MtTrainedRemapLexicon.Apply(cur, src, contentProfile);
        cur = AvGlossaryLexicon.Apply(cur, contentProfile);
        cur = MultiWs.Replace(cur, " ").Trim();

        var glossary = SubtitleGlossaryStore.Load(settings);
        cur = glossary.Apply(cur);
        cur = CommasPeriodsToSpaces(cur);
        if (IsPlaceholderText(cur) || LooksLikeAsrHallucination(cur)) return "";
        return cur;
    }

    /// <summary>Chinese subtitle style: commas / periods → spaces (keep decimal points).</summary>
    public static string CommasPeriodsToSpaces(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var cur = text
            .Replace('，', ' ')
            .Replace('。', ' ')
            .Replace(',', ' ');
        // Western period, but not inside numbers like 3.5
        cur = Regex.Replace(cur, @"(?<!\d)\.(?!\d)", " ");
        return MultiWs.Replace(cur, " ").Trim();
    }

    public static void CleanAsrCues(IList<Cue> cues, AppSettings settings, string? contentProfile = null, int startIndex = 0)
    {
        if (!settings.TextSanitizeEnabled) return;
        startIndex = Math.Clamp(startIndex, 0, cues.Count);
        for (var i = cues.Count - 1; i >= startIndex; i--)
        {
            var cleaned = CleanAsr(cues[i].Text, settings, contentProfile);
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                cues.RemoveAt(i);
                continue;
            }

            cues[i].Text = cleaned;
        }

        for (var i = 0; i < cues.Count; i++)
            cues[i].Index = i + 1;
    }

    /// <summary>Drop stuck identical ZH across adjacent cues with different JA.</summary>
    public static void UnstickCrossCue(IList<Cue> cues, AppSettings settings)
    {
        if (!settings.TextSanitizeEnabled || cues.Count < 2) return;
        var i = 0;
        while (i < cues.Count)
        {
            var zk = ZhNormKey(cues[i].Zh);
            if (zk.Length < 2)
            {
                i++;
                continue;
            }

            var j = i + 1;
            while (j < cues.Count && ZhNormKey(cues[j].Zh) == zk) j++;
            var streak = j - i;
            var minStreak = zk.Length >= 6 ? 2 : 3;
            if (streak >= minStreak)
            {
                var jaKeys = new string[streak];
                for (var k = 0; k < streak; k++)
                    jaKeys[k] = JaNormKey(cues[i + k].Text);
                var uniq = jaKeys.Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).Count();
                if (uniq >= Math.Min(2, streak))
                {
                    var firstJa = jaKeys[0];
                    for (var k = 0; k < streak; k++)
                    {
                        if (k == 0) continue;
                        if (jaKeys[k].Length > 0 && jaKeys[k] == firstJa) continue;
                        // Clear stuck repeats — empty Zh falls back to source on screen
                        // (never paint「…」as a fake translation). Keep _translated marked
                        // so we do not spin forever if MT keeps sticking.
                        if (zk.Length >= 4)
                            cues[i + k].Zh = "";
                    }
                }
            }

            i = j;
        }
    }

    public static string DescribeReady(AppSettings settings)
    {
        if (!settings.TextSanitizeEnabled) return "字幕清洗：关";
        var n = JaAsrDomainLexicon.PairCount;
        var t = MtTrainedRemapLexicon.RuleCount;
        var av = AvGlossaryLexicon.PairCount;
        var g = SubtitleGlossaryStore.Load(settings);
        var gPart = g.Entries.Count > 0
            ? $"术语 {g.Entries.Count}"
            : "术语 无";
        return $"字幕清洗：开 · 日语域纠错 {n} · 训练 remap {t} · AV术语 {av} · {gPart}";
    }

    private static string StripNameLoops(string text)
    {
        var raw = text;
        var cur = raw;
        var stripped = false;
        foreach (var tok in LoopTokens)
        {
            var esc = Regex.Escape(tok);
            var consec = new Regex($@"(?:{esc}){{2,}}");
            if (consec.IsMatch(cur))
            {
                stripped = true;
                cur = consec.Replace(cur, "");
            }

            var ellipsis = new Regex($@"(?:{esc})(?:\s*[…⋅・.。]{{1,6}}\s*(?:{esc}))+");
            if (ellipsis.IsMatch(cur))
            {
                stripped = true;
                cur = ellipsis.Replace(cur, "");
            }

            // Soft-voice ASR often inserts spaces:「琴音 琴音 琴音…」
            var spaced = new Regex($@"(?:{esc})(?:[ \t\u3000]+(?:{esc})){{2,}}");
            if (spaced.IsMatch(cur))
            {
                stripped = true;
                cur = spaced.Replace(cur, "");
            }
        }

        cur = MultiWs.Replace(cur, " ").Trim();
        if (stripped)
        {
            cur = Regex.Replace(cur, @"^[…⋅・.。～〜ー\s\-]+|[…⋅・.。～〜ー\s\-]+$", "").Trim();
            if (PunctOnlyRe.IsMatch(cur))
                cur = "";
        }

        if (IsNameOnlyDebris(cur) && !HonorificRe.IsMatch(raw))
            cur = "";

        return cur;
    }

    /// <summary>
    /// Drop / collapse whisper soft-voice hallucinations that fill the screen
    /// (mono char runs, spaced token loops, low-entropy long cues).
    /// </summary>
    private static string StripHallucinationLoops(string text)
    {
        var cur = text ?? "";
        if (cur.Length == 0) return "";
        var beforeLen = BareNorm.Replace(cur, "").Length;

        // プ×N / 哈×N / あ×N — keep a short moan (≤3) when the run is modest; wipe long spam.
        cur = MonoCharRun.Replace(cur, m =>
        {
            var ch = m.Groups[1].Value;
            if (string.IsNullOrEmpty(ch)) return "";
            return m.Length >= 12 ? "" : new string(ch[0], 3);
        });

        // 「今 今 今…」「音 琴音 琴音…」spaced loops — wipe when long, else keep one token.
        cur = SpacedTokenLoop.Replace(cur, m =>
        {
            var tok = m.Groups[1].Value;
            if (m.Length >= 16) return "";
            return tok;
        });

        // Tight n-gram without spaces: ははははは / ababab…
        cur = TightNgramLoop.Replace(cur, m =>
        {
            var unit = m.Groups[1].Value;
            if (m.Length >= 16) return "";
            return unit;
        });

        cur = MultiWs.Replace(cur, " ").Trim();
        cur = Regex.Replace(cur, @"^[…⋅・.。～〜ー\s\-]+|[…⋅・.。～〜ー\s\-]+$", "").Trim();

        var afterLen = BareNorm.Replace(cur, "").Length;
        // Long spam with a stray leftover char (プ…ス / 音 琴音…) — drop the debris.
        if (beforeLen >= 20 && afterLen <= 2 && beforeLen - afterLen >= 12)
            return "";
        if (beforeLen >= 48 && afterLen <= 4 && afterLen * 8 <= beforeLen)
            return "";

        if (IsPlaceholderText(cur) || LooksLikeAsrHallucination(cur))
            return "";
        return cur;
    }

    /// <summary>True when a cue is mostly repeated junk rather than real speech.</summary>
    internal static bool LooksLikeAsrHallucination(string? text)
    {
        var raw = (text ?? "").Trim();
        if (raw.Length < 8) return false;

        var bare = BareNorm.Replace(raw, "");
        if (bare.Length < 8) return false;

        var uniq = bare.Distinct().Count();
        if (uniq <= 2 && bare.Length >= 10) return true;
        if (uniq <= 4 && bare.Length >= 36)
        {
            var top = 0;
            foreach (var g in bare.GroupBy(c => c))
                if (g.Count() > top) top = g.Count();
            if (top * 100 >= bare.Length * 70) return true;
        }

        var tokens = Regex.Split(raw, @"[ \t\u3000]+")
            .Where(t => t.Length > 0)
            .ToArray();
        if (tokens.Length >= 8)
        {
            var ut = tokens.Distinct(StringComparer.Ordinal).Count();
            if (ut <= 2) return true;
        }

        // Soft VAD segments are short; a 100+ char low-entropy line is almost never real dialogue.
        if (bare.Length >= 100 && uniq <= 10) return true;
        return false;
    }

    private static bool IsNameOnlyDebris(string text)
    {
        var bare = NameBareRe.Replace((text ?? "").Trim(), "");
        if (bare.Length == 0 || bare.Length > 4) return false;
        return MultiLoopStems.Contains(bare) || SingleLoopStems.Contains(bare);
    }

    private static string StripPromptLeak(string text)
    {
        var raw = text;
        if (!PromptLeakMark.IsMatch(raw) && !LooksLikeGlossaryDump(raw))
            return raw;

        var next = raw;
        next = Regex.Replace(next, @"将下面的日文[\s\S]{0,160}?翻译成中文[。.]?", "");
        next = Regex.Replace(next, @"将下面术语表[\s\S]{0,80}?翻译成中文[。.]?", "");
        next = Regex.Replace(next, @"将下面的?[【\[][】\]]?号?句子[\s\S]{0,80}?翻译成中文[。.]?", "");
        next = Regex.Replace(next, @"你?将下面这句话翻译成中文了?[。.]?", "");
        next = Regex.Replace(next, @"请勿删除[。.]?", "");
        next = Regex.Replace(next, @"[，,]?翻译的行数是\d+行[^。\n]{0,40}", "");
        next = Regex.Replace(next, @"根据以下术语表[\s\S]{0,120}?(?:：|:)?", "");
        next = Regex.Replace(next, @"[（(]?\s*译名表[\s\S]{0,200}?[）)]?(?:：|:)?", "");
        next = Regex.Replace(next, @"术语表[（(]?请统一使用[）)]?[\s\S]{0,120}?(?:：|:)?", "");
        next = Regex.Replace(next, @"只输出译文[^。\n]{0,120}[。.]?", "");
        next = Regex.Replace(next, @"不要编号[、，,]?\s*不要解释[^。\n]{0,80}", "");
        next = Regex.Replace(next, @"[你我]是(?:一个)?日译中字幕翻译[^。\n]{0,80}[。.]?", "");
        next = Regex.Replace(next, @"按汉化组习惯[^。\n]{0,120}[。.]?", "");
        next = Regex.Replace(next, @"严禁净化[^。\n]{0,80}[。.]?", "");
        next = Regex.Replace(next, @"忠实语气模式[^。\n]{0,100}[。.]?", "");
        next = MultiWs.Replace(next, " ").Trim();

        if (LooksLikeGlossaryDump(next) || PromptLeakMark.IsMatch(next))
            next = "";
        return next;
    }

    private static bool LooksLikeGlossaryDump(string text)
    {
        var t = text.Trim();
        if (t.Length < 8) return false;
        var arrows = Regex.Matches(t, @"->|→|=>").Count;
        var hashes = Regex.Matches(t, @"#勿译|#保留").Count;
        return arrows >= 2 || (arrows >= 1 && hashes >= 1) || (t.Contains("术语表", StringComparison.Ordinal) && arrows >= 1);
    }

    private static bool LooksLikeSourceEcho(string text, string source)
    {
        var t = text.Trim();
        var src = source.Trim();
        if (t.Length == 0) return false;
        var srcIsJa = Regex.IsMatch(src, @"[\u3040-\u30ff]{2,}");
        if (src.Length > 0)
        {
            var norm = (string s) => Regex.Replace(s, @"\s+", "");
            if (t == src || norm(t) == norm(src))
                return srcIsJa;
        }

        var kana = Regex.Matches(t, @"[\u3040-\u30ff]").Count;
        var han = Regex.Matches(t, @"[\u4e00-\u9fff]").Count;
        var len = t.Length;
        if (len <= 24) return kana >= 2 && kana > han;
        return kana >= 6 && kana >= Math.Max(4, han * 35 / 100);
    }

    private static string CollapseHonorificEchoes(string text)
    {
        var cur = HonorificEcho.Replace(text, "$1$2");
        cur = BareHonorificEcho.Replace(cur, "$1");
        return cur;
    }

    private static string StripSpuriousNamePrefixes(string text, string source)
    {
        var cur = Regex.Replace(text, @"到的(?=[\u4e00-\u9fffぁ-んァ-ンー]{1,6}(?:小姐|先生|同学|桑|君|酱)?)", "");
        if (Regex.IsMatch(source, @"お兄|姉さん|お姉|兄さん"))
        {
            cur = cur.Replace("亲爱的奥小姐", "哥哥", StringComparison.Ordinal);
            cur = cur.Replace("奥小姐", Regex.IsMatch(source, @"お姉|姉さん") ? "姐姐" : "哥哥", StringComparison.Ordinal);
            cur = cur.Replace("兄小姐", "哥哥", StringComparison.Ordinal);
            cur = cur.Replace("姉小姐", "姐姐", StringComparison.Ordinal);
        }

        return cur;
    }

    private static string StripResidualJaInZh(string text)
    {
        var cur = text;
        var zhChars = Regex.Matches(cur, @"[\u4e00-\u9fff]").Count;
        var jaChars = Regex.Matches(cur, @"[\u3040-\u30ff]").Count;
        if (jaChars == 0 || zhChars < 1) return cur;
        if (zhChars <= 1 && jaChars <= 4) return cur;

        const string honor = "小姐|先生|同学|桑|君|酱";
        cur = Regex.Replace(cur, $@"[ぁ-んァ-ンー]{{2,12}}(?:{honor})", "");
        cur = Regex.Replace(cur, $@"[\u4e00-\u9fff]{{1,2}}[ぁ-んァ-ンー]{{2,10}}(?:{honor})", "");
        if (zhChars >= 2)
        {
            cur = Regex.Replace(cur, @"(?<=[\u4e00-\u9fff])[ぁ-んァ-ンー]{2,16}(?=[\u4e00-\u9fff])", "");
            cur = Regex.Replace(cur, @"[ぁ-んァ-ンー]{2,16}(?=[\u4e00-\u9fff]{2,})", "");
            cur = Regex.Replace(cur, @"(?<=[\u4e00-\u9fff，,、])[ぁ-んァ-ンー]{2,16}$", "");
            cur = Regex.Replace(cur, @"^[ぁ-んァ-ンー]{2,16}(?=[\s\u4e00-\u9fff])", "");
            if (Regex.Matches(cur, @"[\u4e00-\u9fff]").Count >= 2)
                cur = Regex.Replace(cur, @"[ぁ-んァ-ンー]{2,}", "");
        }

        cur = Regex.Replace(cur, @"的(?:小姐|先生|同学|桑|君|酱)(?=[\s，,、。！？!…]|$)", "");
        return MultiWs.Replace(cur, " ").Trim();
    }

    private static string ZhNormKey(string? zh)
        => Regex.Replace((zh ?? "").Trim(), @"[\s…·・.。，,！!？?]+", "");

    private static string JaNormKey(string? ja)
        => Regex.Replace((ja ?? "").Trim(), @"[\s…·・.。，,！!？?]+", "");
}
