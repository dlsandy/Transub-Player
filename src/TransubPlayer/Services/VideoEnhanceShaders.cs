using System.IO;
using System.Text;

namespace TransubPlayer.Services;

/// <summary>
/// Tiny built-in unsharp GLSL hooks written under <see cref="AppPaths.ShadersDir"/> on first use.
/// Scaler-only presets are nearly invisible at 1:1 display; these run on every frame.
/// </summary>
internal static class VideoEnhanceShaders
{
    public const string LightFileName = "enhance-unsharp-light.glsl";
    public const string EnhanceFileName = "enhance-unsharp.glsl";

    public static string Dir
    {
        get
        {
            EnsureInstalled();
            return AppPaths.ShadersDir;
        }
    }

    public static void EnsureInstalled()
    {
        var root = AppPaths.ShadersDir;
        WriteIfMissing(Path.Combine(root, LightFileName), BuildUnsharp(0.55));
        WriteIfMissing(Path.Combine(root, EnhanceFileName), BuildUnsharp(1.15));
    }

    public static IReadOnlyList<string> ShaderPathsFor(VideoEnhanceMode mode)
    {
        EnsureInstalled();
        return mode switch
        {
            VideoEnhanceMode.Light => [Path.Combine(AppPaths.ShadersDir, LightFileName)],
            VideoEnhanceMode.Enhance => [Path.Combine(AppPaths.ShadersDir, EnhanceFileName)],
            _ => [],
        };
    }

    private static void WriteIfMissing(string path, string contents)
    {
        try
        {
            if (File.Exists(path))
                return;
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // menu still works with scaler/deband-only fallback
        }
    }

    /// <summary>Simple 3×3 unsharp — visible at native resolution, cheap on GPU.</summary>
    private static string BuildUnsharp(double strength)
    {
        var s = strength.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        // Avoid raw-string `{}` clashes with C# interpolation — concatenate the #define only.
        return
            "//!DESC transub-enhance-unsharp\n" +
            "//!HOOK MAIN\n" +
            "//!BIND HOOKED\n" +
            "\n" +
            "#define STRENGTH " + s + "\n" +
            "\n" +
            "vec4 hook() {\n" +
            "    vec4 c = HOOKED_tex(HOOKED_pos);\n" +
            "    vec4 blur = (\n" +
            "        HOOKED_texOff(vec2(-1.0, -1.0)) +\n" +
            "        HOOKED_texOff(vec2( 0.0, -1.0)) +\n" +
            "        HOOKED_texOff(vec2( 1.0, -1.0)) +\n" +
            "        HOOKED_texOff(vec2(-1.0,  0.0)) +\n" +
            "        c +\n" +
            "        HOOKED_texOff(vec2( 1.0,  0.0)) +\n" +
            "        HOOKED_texOff(vec2(-1.0,  1.0)) +\n" +
            "        HOOKED_texOff(vec2( 0.0,  1.0)) +\n" +
            "        HOOKED_texOff(vec2( 1.0,  1.0))\n" +
            "    ) / 9.0;\n" +
            "    return c + (c - blur) * STRENGTH;\n" +
            "}\n";
    }
}
