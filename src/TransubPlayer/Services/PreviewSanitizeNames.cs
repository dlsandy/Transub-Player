using System.Text.RegularExpressions;

namespace TransubPlayer.Services;

/// <summary>
/// Light-preview port of Transub3 name-loop stems, pollution strip, kinship / address fixes.
/// Full lexicon + honorific extract stays in Transub; Player only needs streaming-path parity.
/// Source: <c>ja-person-names-core.js</c>, <c>mt-sanitize-core.js</c>, <c>subtitle-glossary-core.js</c>.
/// </summary>
internal static class PreviewSanitizeNames
{
    /// <summary>Multi-char ZH stems Whisper / MT often invent or loop (EXTRA_POLLUTION + surnames).</summary>
    internal static readonly string[] MultiLoopStems =
    [
        "玲奈", "莉奈", "佳奈", "纱奈", "沙奈", "真理", "理子", "绘真", "绘里",
        "美咲", "结衣", "阳菜", "春奈", "桃子", "琴音", "黄奈", "奏翔", "斗碧",
        "乃爱", "爱奈", "露娜", "环奈", "花音", "雏子", "真寻", "圣音", "明日香",
        "七海", "仓木", "北野", "喜多野", "奈奈", "妮可", "惠梨香", "日奈子",
        "理绪", "莉子", "真希", "穗香", "绮罗", "铃音", "飞鸟", "麻衣",
        // Common surname loops (佐藤佐藤…)
        "佐藤", "铃木", "高桥", "田中", "伊藤", "渡边", "山本", "中村",
        "小林", "加藤", "吉田", "山田", "佐佐木", "松本", "井上", "木村",
        "清水", "山口", "阿部", "池田", "桥本", "石川", "前田", "藤田",
        "小川", "冈田", "后藤", "长谷川", "石井", "村上", "近藤", "坂本",
        "远藤", "青木", "藤原", "西村", "福田", "太田", "三浦", "冈本",
        "松田", "中岛", "原田", "小野", "田村", "竹内", "金子", "中川",
        "中野", "松井", "岩崎", "横山", "上田",
    ];

    internal static readonly string[] SingleLoopStems =
    [
        "葵", "嬉", "笑", "爱", "愛", "盐", "塩", "鸭", "鴨", "舞", "桃", "玲", "樱", "凛", "花", "月",
        "琉", "律", "春", "陆", "蓝", "兰", "翼", "光", "望", "萌", "杏", "铃", "雪", "梦", "林", "森", "原",
    ];

    internal static readonly string[] LoopTokens = MultiLoopStems
        .Concat(SingleLoopStems)
        .Distinct(StringComparer.Ordinal)
        .OrderByDescending(t => t.Length)
        .ThenBy(t => t, StringComparer.Ordinal)
        .ToArray();

    private static readonly HashSet<string> MultiStemSet = new(MultiLoopStems, StringComparer.Ordinal);
    private static readonly HashSet<string> SingleStemSet = new(SingleLoopStems, StringComparer.Ordinal);
    private static readonly HashSet<string> AllStemSet = new(
        MultiLoopStems.Concat(SingleLoopStems), StringComparer.Ordinal);

    private static readonly Regex HonorificRe = new(@"(さん|くん|ちゃん|君|様|氏)", RegexOptions.Compiled);
    private static readonly Regex PunctOnlyRe = new(@"^[…⋅・.\s。！？!?～〜ー\-'""\u3000]*$", RegexOptions.Compiled);
    private static readonly Regex NameBareRe = new(@"[…⋅・.。！？!?\s～〜\-ー'""\u3000]+", RegexOptions.Compiled);
    private static readonly Regex MultiWs = new(@"[^\S\n]{2,}", RegexOptions.Compiled);
    private static readonly Regex TrailingHonorific = new(
        @"(?:同学|小姐|先生|桑|君|酱|大人|老师)$", RegexOptions.Compiled);

    /// <summary>ASR / display: strip Whisper name loops using Transub3 EXTRA stems.</summary>
    public static string StripNameLoops(string text)
    {
        var raw = text ?? "";
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

        // Keep single name-call cues (MFYD-185); only wipe when raw had honorific-less debris
        // and the leftover is still a bare pollution stem — match prior Player behavior.
        if (IsNameOnlyDebris(cur) && !HonorificRe.IsMatch(raw))
            cur = "";

        return cur;
    }

    public static bool IsNameOnlyDebris(string? text)
    {
        var bare = NameBareRe.Replace((text ?? "").Trim(), "");
        if (bare.Length == 0 || bare.Length > 4) return false;
        return AllStemSet.Contains(bare);
    }

    /// <summary>
    /// ZH MT: drop multi-char pollution stems not justified by JA source; collapse justified loops.
    /// </summary>
    public static string StripUnjustifiedPollutionNames(string text, string source)
    {
        var raw = text ?? "";
        var cur = raw;
        var touched = false;
        var loopStrippedSrc = StripNameLoops(source ?? "");

        foreach (var stem in MultiLoopStems)
        {
            if (!cur.Contains(stem, StringComparison.Ordinal)) continue;
            var esc = Regex.Escape(stem);
            if (!SourceJustifiesZhName(stem, source ?? "", loopStrippedSrc))
            {
                var re = new Regex($@"{esc}(?:同学|小姐|先生|桑|君|酱|大人|老师)?");
                var next = re.Replace(cur, "");
                if (next != cur)
                {
                    touched = true;
                    cur = next;
                }
            }
            else
            {
                var next = Regex.Replace(cur, $@"(?:{esc}){{2,}}", stem);
                next = Regex.Replace(next, $@"({esc})(?:\s*[…⋅・.。]{{1,6}}\s*(?:{esc}))+", "$1");
                if (next != cur)
                {
                    touched = true;
                    cur = next;
                }
            }
        }

        // Single-char: whole-cue or consecutive loops only
        foreach (var stem in SingleLoopStems)
        {
            if (!cur.Contains(stem, StringComparison.Ordinal)) continue;
            if (SourceJustifiesZhName(stem, source ?? "", loopStrippedSrc)) continue;
            var esc = Regex.Escape(stem);
            if (Regex.IsMatch(cur.Trim(), $@"^[\s…⋅・.。！？!?～〜\-]*{esc}[\s…⋅・.。！？!?～〜\-]*$"))
            {
                touched = true;
                cur = "";
                break;
            }

            var next = Regex.Replace(cur, $@"(?:{esc}){{2,}}", "");
            next = Regex.Replace(next, $@"(?:{esc})(?:\s*[…⋅・.。]{{1,6}}\s*(?:{esc}))+", "");
            if (next != cur)
            {
                touched = true;
                cur = next;
            }
        }

        cur = MultiWs.Replace(cur, " ").Trim();
        if (touched)
        {
            if (!Regex.IsMatch(cur, @"^(?:哈啊?|啊+|嗯+|唔+|呼+|呜+|哦+|噢+)[…·.。]*$"))
                cur = Regex.Replace(cur, @"^[…⋅・.。～〜\s\-]+|[…⋅・.。～〜\s\-]+$", "").Trim();
            if (PunctOnlyRe.IsMatch(cur)) cur = "";
        }

        return cur;
    }

    /// <summary>Kinship / address mistranslations (轻量 port of fixKinship + BUILTIN_ADDRESS).</summary>
    public static string FixKinshipAndAddress(string text, string source)
    {
        var cur = text ?? "";
        var src = source ?? "";

        // BUILTIN_ADDRESS: dup honorific
        cur = cur.Replace("小姐小姐", "小姐", StringComparison.Ordinal);
        cur = cur.Replace("先生先生", "先生", StringComparison.Ordinal);

        if (Regex.IsMatch(src, @"お母|かあさん|母さん") || src.TrimStart().StartsWith("お母さん", StringComparison.Ordinal))
        {
            cur = cur.Replace("母小姐", "妈妈", StringComparison.Ordinal);
            cur = cur.Replace("妈妈小姐", "妈妈", StringComparison.Ordinal);
        }

        if (Regex.IsMatch(src, @"お父|とうさん|父さん") || src.TrimStart().StartsWith("お父さん", StringComparison.Ordinal))
        {
            cur = cur.Replace("父小姐", "爸爸", StringComparison.Ordinal);
            cur = cur.Replace("爸爸小姐", "爸爸", StringComparison.Ordinal);
        }

        if (Regex.IsMatch(src, @"奥さん|おくさん"))
        {
            cur = cur.Replace("旦那小姐", "太太", StringComparison.Ordinal);
            cur = cur.Replace("亲爱的奥小姐", "太太", StringComparison.Ordinal);
            cur = cur.Replace("奥小姐", "太太", StringComparison.Ordinal);
            cur = Regex.Replace(cur, @"(太太)(?:\s*\1)+", "$1");
        }
        else if (cur.Contains("旦那小姐", StringComparison.Ordinal)
                 && (Regex.IsMatch(src, @"旦那|だんな") || !Regex.IsMatch(src, @"奥さん|おくさん")))
        {
            cur = cur.Replace("旦那小姐", "老公", StringComparison.Ordinal);
            cur = Regex.Replace(cur, @"(老公)(?:\s*\1)+", "$1");
        }

        // BUILTIN oka_sama: 奥小姐 → 太太 when source has no 奥小姐 and no brother context
        if (cur.Contains("奥小姐", StringComparison.Ordinal)
            && !src.Contains("奥小姐", StringComparison.Ordinal)
            && !Regex.IsMatch(src, @"お兄|お姉|姉さん|兄さん"))
        {
            cur = cur.Replace("亲爱的奥小姐", "太太", StringComparison.Ordinal);
            cur = cur.Replace("奥小姐", "太太", StringComparison.Ordinal);
            cur = Regex.Replace(cur, @"(太太)(?:\s*\1)+", "$1");
        }

        if (Regex.IsMatch(src, @"おばあちゃん|祖母"))
            cur = Regex.Replace(cur, @"祖母小姐|外婆小姐|奶奶小姐", m => m.Value.Replace("小姐", "", StringComparison.Ordinal));
        if (Regex.IsMatch(src, @"おじいちゃん|祖父"))
            cur = Regex.Replace(cur, @"祖父小姐|外公小姐|爷爷小姐", m => m.Value.Replace("小姐", "", StringComparison.Ordinal));

        if (Regex.IsMatch(src, @"お兄|兄さん") && !Regex.IsMatch(src, @"奥さん|おくさん"))
        {
            cur = cur.Replace("亲爱的奥小姐", "哥哥", StringComparison.Ordinal);
            cur = cur.Replace("奥小姐", "哥哥", StringComparison.Ordinal);
            cur = cur.Replace("兄小姐", "哥哥", StringComparison.Ordinal);
        }

        if (Regex.IsMatch(src, @"お姉|姉さん"))
        {
            cur = cur.Replace("姉小姐", "姐姐", StringComparison.Ordinal);
            cur = cur.Replace("姐小姐", "姐姐", StringComparison.Ordinal);
        }

        // Legacy Player: 到的XX小姐
        cur = Regex.Replace(cur, @"到的(?=[\u4e00-\u9fffぁ-んァ-ンー]{1,6}(?:小姐|先生|同学|桑|君|酱)?)", "");

        // BUILTIN danna leftover
        if (cur.Contains("旦那小姐", StringComparison.Ordinal))
            cur = cur.Replace("旦那小姐", "老公", StringComparison.Ordinal);

        return cur;
    }

    private static bool SourceJustifiesZhName(string stem, string source, string loopStrippedSrc)
    {
        var s = TrailingHonorific.Replace((stem ?? "").Trim(), "");
        if (s.Length == 0) return false;

        var srcN = NormalizePersonStem(loopStrippedSrc);
        var stemN = NormalizePersonStem(s);
        var nameOnly = IsNameOnlyDebris(loopStrippedSrc);

        if (!nameOnly && (srcN.Contains(stemN, StringComparison.Ordinal)
                          || srcN.Contains(NormalizePersonStem(stem ?? ""), StringComparison.Ordinal)))
            return true;

        // Honorific on raw JA (美咲さん) even when loops elsewhere
        if (Regex.IsMatch(source, Regex.Escape(s) + @"(?:さん|ちゃん|くん|君|様)"))
            return true;

        return false;
    }

    private static string NormalizePersonStem(string text)
        => (text ?? "")
            .Replace("倉", "仓", StringComparison.Ordinal)
            .Replace("黒", "黑", StringComparison.Ordinal)
            .Replace("桜", "樱", StringComparison.Ordinal)
            .Replace("実", "实", StringComparison.Ordinal)
            .Replace("絵", "绘", StringComparison.Ordinal)
            .Replace("恵", "惠", StringComparison.Ordinal);
}
