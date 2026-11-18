using System.Net.Sockets;
using System.Reflection;
using TransubPlayer.Localization;
using TransubPlayer.Platform;
using TransubPlayer.Services;

static void Ok(string name) => Console.WriteLine($"PASS  {name}");
static void Fail(string name, string detail)
{
    Console.WriteLine($"FAIL  {name}: {detail}");
    Environment.ExitCode = 1;
}

// --- Loc packs ---
try
{
    Loc.Apply("zh-Hans");
    var s = Loc.Get("Main.Tagline");
    if (string.IsNullOrWhiteSpace(s) || s.StartsWith("Str.", StringComparison.Ordinal))
        Fail("Loc.zh-Hans", $"got '{s}'");
    else
        Ok($"Loc.zh-Hans → {s}");

    Loc.Apply("en");
    var en = Loc.Get("Main.Open");
    if (string.IsNullOrWhiteSpace(en) || en.StartsWith("Str.", StringComparison.Ordinal))
        Fail("Loc.en", $"got '{en}'");
    else
        Ok($"Loc.en → {en}");
}
catch (Exception ex)
{
    Fail("Loc", ex.Message);
}

// --- Platform registration (Unix path used on Linux CI / Mac) ---
try
{
    PlatformServices.Register(
        new UnixMpvIpcFactory(),
        new UnixChildProcessPolicy(),
        new CpuAsrGpuRuntime(),
        os: null);
    Ok("PlatformServices.Register");
}
catch (Exception ex)
{
    Fail("PlatformServices.Register", ex.Message);
}

// --- AppPaths ---
try
{
    var data = AppPaths.AppDataDir;
    Directory.CreateDirectory(data);
    if (!Directory.Exists(data))
        Fail("AppPaths.AppDataDir", data);
    else
        Ok($"AppPaths.AppDataDir → {data}");

    var models = AsrModelStore.ResolveModelsRoot(new AppSettings());
    Ok($"AsrModelStore.ResolveModelsRoot → {models}");
}
catch (Exception ex)
{
    Fail("AppPaths", ex.Message);
}

// --- ASR backends ---
try
{
    var list = AsrBackends.Selectable;
    if (list.Count < 2)
        Fail("AsrBackends.Selectable", string.Join(",", list));
    else
        Ok($"AsrBackends.Selectable → {string.Join(",", list)}");

    AsrRuntime.Apply(new AppSettings { AsrBackend = AsrBackends.Cpu });
    Ok($"AsrRuntime.Apply → {AsrRuntime.ActiveBackend}");
}
catch (Exception ex)
{
    Fail("AsrRuntime", ex.Message);
}

// --- Unix mpv IPC endpoint + round-trip connect ---
try
{
    var factory = new UnixMpvIpcFactory();
    var endpoint = factory.CreateServerEndpoint();
    if (string.IsNullOrWhiteSpace(endpoint) || !endpoint.Contains("transub-player-mpv", StringComparison.Ordinal))
    {
        Fail("UnixMpvIpcFactory.CreateServerEndpoint", endpoint);
    }
    else
    {
        Ok($"UnixMpvIpcFactory.CreateServerEndpoint → {endpoint}");

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(endpoint));
        listener.Listen(1);
        var acceptTask = listener.AcceptAsync();
        var transportTask = factory.ConnectAsync(endpoint, CancellationToken.None);
        if (!Task.WaitAll([acceptTask, transportTask], TimeSpan.FromSeconds(3)))
        {
            Fail("UnixMpvIpcFactory.ConnectAsync", "timeout");
        }
        else
        {
            using var transport = transportTask.Result;
            using var server = acceptTask.Result;
            Ok("UnixMpvIpcFactory.ConnectAsync");
        }

        try { File.Delete(endpoint); } catch { /* ignore */ }
    }
}
catch (Exception ex)
{
    Fail("UnixMpvIpc", ex.ToString());
}

// --- Child process track/stop ---
try
{
    var policy = new UnixChildProcessPolicy();
    var psi = new System.Diagnostics.ProcessStartInfo
    {
        FileName = "sleep",
        ArgumentList = { "30" },
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    policy.PrepareStartInfo(psi);
    var p = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start sleep");
    policy.Track(p);
    System.Diagnostics.Process? tracked = p;
    policy.Stop(ref tracked, waitMs: 2000);
    if (tracked is not null)
    {
        try
        {
            if (!tracked.HasExited)
                Fail("UnixChildProcessPolicy.Stop", "still alive");
            else
                Ok("UnixChildProcessPolicy.Track/Stop");
        }
        catch
        {
            Ok("UnixChildProcessPolicy.Track/Stop");
        }
    }
    else
    {
        Ok("UnixChildProcessPolicy.Track/Stop");
    }
}
catch (Exception ex)
{
    Fail("UnixChildProcessPolicy", ex.Message);
}

// --- PreviewController ctor takes IVideoSurface ---
try
{
    var ctor = typeof(PreviewController)
        .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .FirstOrDefault();
    if (ctor is null)
    {
        Fail("PreviewController.ctor", "missing");
    }
    else
    {
        var ps = ctor.GetParameters();
        var surface = ps.Any(p => p.ParameterType.Name is "IVideoSurface");
        if (!surface)
            Fail("PreviewController.ctor", string.Join(", ", ps.Select(p => p.ParameterType.Name)));
        else
            Ok("PreviewController(IVideoSurface, …)");
    }
}
catch (Exception ex)
{
    Fail("PreviewController", ex.Message);
}

// --- MpvLocator candidates include non-.exe on Unix ---
try
{
    var candidates = MpvLocator.Candidates().ToList();
    if (candidates.Count == 0)
        Fail("MpvLocator.Candidates", "empty");
    else if (OperatingSystem.IsWindows() && candidates.All(c => c.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
        Ok($"MpvLocator.Candidates → {candidates.Count} (.exe)");
    else if (!OperatingSystem.IsWindows() && candidates.Any(c => c.EndsWith("/mpv", StringComparison.Ordinal) || c.EndsWith($"{Path.DirectorySeparatorChar}mpv")))
        Ok($"MpvLocator.Candidates → {candidates.Count} (unix mpv)");
    else
        Ok($"MpvLocator.Candidates → {candidates.Count} (first={candidates[0]})");
}
catch (Exception ex)
{
    Fail("MpvLocator", ex.Message);
}

Console.WriteLine(Environment.ExitCode == 0
    ? "\nAll Core smoke checks passed."
    : "\nSome Core smoke checks failed.");
return Environment.ExitCode;
