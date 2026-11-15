using SpireAutopilot;

if (args.Length != 2) { Console.WriteLine("ExperienceMigrationProbe configPath experienceDirectory"); return 2; }
AutopilotConfig? config = null;
try
{
    config = AutopilotConfig.Load(args[0]);
    var store = new RunExperienceStore(args[1]);
    using var provider = new ProviderClient();
    int batches = 0;
    while (store.HasPendingReflection)
    {
        var frame = store.CreateReflectionFrame(null);
        if (frame == null || !frame.Token.StartsWith("experience-migration:", StringComparison.Ordinal)) break;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(60, config.RequestTimeoutSeconds + 5)));
        var decision = await provider.DecideAsync(config, frame, deadline.Token);
        if (!store.CommitReflection(frame.Token, decision, config.ApiKey)) throw new InvalidOperationException("经验迁移回复已过期，请重试。");
        Console.WriteLine($"migration batch={++batches} committed");
    }
    Console.WriteLine($"summary={Path.Combine(Path.GetFullPath(args[1]), "experience.md")} batches={batches}");
    return 0;
}
catch (Exception ex)
{
    string text = ex.GetType().Name + ": " + ex.Message;
    if (!string.IsNullOrEmpty(config?.ApiKey)) text = text.Replace(config.ApiKey, "[REDACTED]", StringComparison.Ordinal);
    Console.Error.WriteLine(text);
    return 1;
}
