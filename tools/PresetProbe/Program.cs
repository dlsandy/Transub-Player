using TransubPlayer.Services;

var settings = AppSettings.Load();
Console.WriteLine("TranslateEnabled=" + settings.TranslateEnabled);
Console.WriteLine("SourceLanguage=" + settings.SourceLanguage);
Console.WriteLine("AsrModel=" + settings.AsrModel);
Console.WriteLine("TranslateTarget=" + settings.TranslateTarget);
var eng = EngineLocator.Find(settings);
Console.WriteLine("Engine=" + (eng is null ? "(none)" : eng.Label + " | " + eng.Path));
Console.WriteLine("Models=" + EngineLocator.ResolveModelsRoot(settings, eng));
Console.WriteLine("Llama=" + ManagedLlmInstaller.HasLlamaRuntime());
Console.WriteLine("Gguf=" + ManagedLlmInstaller.HasPreferredGguf(settings));
Console.WriteLine("--- runtime gaps ---");
var wantsMt = settings.TranslateEnabled;
var r = PresetReadiness.AnalyzeDisk(settings, wantsMt);
Console.WriteLine($"{(r.HasGaps ? "[?]" : "[OK]")} {r.PresetName} :: {(r.HasGaps ? r.SummaryLine() : "??")}");
