using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TransubPlayer.Services;

internal static class StripchatMouflonDecoder
{
    private static readonly Regex PkeyRe = new(
        @"#EXT-X-MOUFLON:PSCH:(?<psch>v\d+):(?<pkey>\w+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex V2UriSegmentRe = new(
        @"_(\d+)_([^_]+)_(\d+)",
        RegexOptions.Compiled);

    public static string DecodePlaylist(string m3u8)
    {
        if (!m3u8.Contains("#EXT-X-MOUFLON", StringComparison.OrdinalIgnoreCase))
            return m3u8;

        var pkeys = PkeyRe.Matches(m3u8).Select(m => m.Groups["pkey"].Value).ToList();
        string best = m3u8;
        var bestScore = -1;

        foreach (var pdkey in StripchatMouflonKeys.CandidatePdkeys(pkeys))
        {
            var decoded = DecodeWithPdkey(m3u8, pdkey);
            var score = ScorePlaylist(decoded);
            if (score > bestScore)
            {
                bestScore = score;
                best = decoded;
            }
        }

        return CleanMouflonTags(best);
    }

    private static string DecodeWithPdkey(string m3u8, string pdkey)
    {
        var lines = m3u8.Split('\n');
        string? pendingFile = null;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');

            if (line.StartsWith("#EXT-X-MOUFLON:FILE:", StringComparison.OrdinalIgnoreCase))
            {
                var enc = line["#EXT-X-MOUFLON:FILE:".Length..].Trim();
                pendingFile = DecryptB64(enc, pdkey);
                continue;
            }

            if (line.StartsWith("#EXT-X-MOUFLON:URI:", StringComparison.OrdinalIgnoreCase))
            {
                var uri = line["#EXT-X-MOUFLON:URI:".Length..].Trim();
                var match = V2UriSegmentRe.Match(uri);
                if (!match.Success) continue;

                var encrypted = match.Groups[2].Value;
                var reversed = new string(encrypted.Reverse().ToArray());
                var dec = DecryptB64(reversed, pdkey);
                if (string.IsNullOrWhiteSpace(dec)) continue;

                var newUri = uri.Replace($"_{encrypted}_", $"_{dec}_", StringComparison.Ordinal);
                lines[i] = "#EXT-X-MOUFLON:URI:" + newUri;
                for (var j = i + 1; j < lines.Length && j < i + 6; j++)
                {
                    if (string.IsNullOrWhiteSpace(lines[j])) continue;
                    if (lines[j].Contains("media.mp4", StringComparison.Ordinal))
                    {
                        lines[j] = newUri;
                        break;
                    }

                    if (!lines[j].StartsWith('#'))
                        break;
                }

                continue;
            }

            if (pendingFile is not null && line.Contains("media.mp4", StringComparison.Ordinal))
            {
                lines[i] = line.Replace("media.mp4", pendingFile, StringComparison.Ordinal);
                pendingFile = null;
            }
        }

        return string.Join('\n', lines);
    }

    private static int ScorePlaylist(string m3u8)
    {
        var score = 0;
        foreach (var line in m3u8.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith('#')) continue;
            if (string.IsNullOrWhiteSpace(t)) continue;
            if (t.Contains("media.mp4", StringComparison.Ordinal)) continue;
            if (t.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                || t.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
                || t.Contains(".m4s", StringComparison.OrdinalIgnoreCase))
                score++;
        }

        return score;
    }

    private static string CleanMouflonTags(string m3u8)
    {
        var sb = new StringBuilder();
        foreach (var line in m3u8.Split('\n'))
        {
            if (line.TrimStart().StartsWith("#EXT-X-MOUFLON", StringComparison.OrdinalIgnoreCase))
                continue;
            sb.AppendLine(line.TrimEnd('\r'));
        }

        return sb.ToString().TrimEnd() + "\n";
    }

    private static string DecryptB64(string encryptedB64, string key)
    {
        try
        {
            var data = Convert.FromBase64String(PadB64(encryptedB64));
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            var outBytes = new byte[data.Length];
            for (var i = 0; i < data.Length; i++)
                outBytes[i] = (byte)(data[i] ^ hash[i % hash.Length]);
            return Encoding.UTF8.GetString(outBytes);
        }
        catch
        {
            return "";
        }
    }

    private static string PadB64(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var mod = s.Length % 4;
        return mod == 0 ? s : s + new string('=', 4 - mod);
    }
}
