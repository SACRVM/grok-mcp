using GrokMcp.Config;
using GrokMcp.Services;
using Microsoft.Extensions.Options;
using NReco.Logging.File;

var startedAt = DateTime.UtcNow;

try
{
    // The default content root is the CWD, and the default appsettings.json reload watches it
    // recursively. A systemd user unit starts in $HOME, and on Linux FileSystemWatcher takes one
    // inotify watch per subdirectory: it ate the whole fs.inotify.max_user_watches budget (524288)
    // and broke inotify for the rest of the desktop. grok-mcp reads config.env once at startup and
    // ships no appsettings.json, so pin the root to the install dir and switch the reload off.
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = [.. args, "--hostBuilder:reloadConfigOnChange=false"],
        ContentRootPath = AppContext.BaseDirectory,
    });

    builder.Services.Configure<GrokOptions>(GrokOptions.BindFromEnvironment);

    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

    var bootOpts = new GrokOptions();
    GrokOptions.BindFromEnvironment(bootOpts);
    if (Enum.TryParse<LogLevel>(bootOpts.LogLevel, ignoreCase: true, out var lvl))
        builder.Logging.SetMinimumLevel(lvl);

    if (!string.IsNullOrWhiteSpace(bootOpts.LogDir))
    {
        Directory.CreateDirectory(bootOpts.LogDir);
        PruneOldLogs(bootOpts.LogDir, bootOpts.LogRetentionDays);
        var logFilePath = Path.Combine(bootOpts.LogDir, "grok-mcp-{0:yyyy-MM-dd}.log");
        builder.Logging.AddFile(logFilePath, fileOpts =>
        {
            fileOpts.Append = true;
            fileOpts.FormatLogFileName = name => string.Format(name, DateTime.Now);
        });
    }

    if (string.IsNullOrWhiteSpace(bootOpts.ApiKey))
    {
        var fatal = $"FATAL: XAI_API_KEY is missing. Set it in {string.Join(" or ", GrokOptions.ConfigFilePaths())}.";
        await Console.Error.WriteLineAsync(fatal);
        // WinExe subsystem detaches stderr when not redirected — also persist to file
        // so a user staring at a flapping Scheduled Task can find the reason in the logs.
        if (!string.IsNullOrWhiteSpace(bootOpts.LogDir))
        {
            try
            {
                var fatalPath = Path.Combine(bootOpts.LogDir, $"grok-mcp-{DateTime.Now:yyyy-MM-dd}.log");
                await File.AppendAllTextAsync(fatalPath, $"{DateTime.Now:O} {fatal}{Environment.NewLine}");
            }
            catch { }
        }
        return 2;
    }

    builder.Services.AddSingleton<ChatSessionStore>();
    builder.Services.AddSingleton<ImageInputResolver>();
    builder.Services.AddSingleton<ImageWriter>();
    builder.Services.AddHttpClient<GrokClient>((sp, http) =>
    {
        var opts = sp.GetRequiredService<IOptions<GrokOptions>>().Value;
        http.Timeout = TimeSpan.FromSeconds(opts.HttpTimeoutSeconds);
    }).ConfigurePrimaryHttpMessageHandler(GrokClient.CreateHandler);

    builder.Services
        .AddMcpServer()
        .WithHttpTransport(o => o.Stateless = true)
        .WithToolsFromAssembly();

    var app = builder.Build();

    app.MapMcp("/mcp");
    app.MapGet("/health", (IOptions<GrokOptions> opts) => Results.Ok(new
    {
        status = "ok",
        pid = Environment.ProcessId,
        version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown",
        port = opts.Value.ListenPort,
        apiKeyConfigured = !string.IsNullOrWhiteSpace(opts.Value.ApiKey),
        uptimeSeconds = (long)(DateTime.UtcNow - startedAt).TotalSeconds,
    }));

    await app.RunAsync($"http://127.0.0.1:{bootOpts.ListenPort}");
    return 0;
}
catch (Exception ex)
{
    await Console.Error.WriteLineAsync($"FATAL: {ex.GetType().Name}: {ex.Message}");
    await Console.Error.WriteLineAsync(ex.StackTrace);
    return 1;
}

static void PruneOldLogs(string dir, int retentionDays)
{
    if (retentionDays <= 0) return;
    try
    {
        var cutoff = DateTime.Now.AddDays(-retentionDays);
        foreach (var f in Directory.EnumerateFiles(dir, "grok-mcp-*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(f) < cutoff)
                    File.Delete(f);
            }
            catch { /* locked or already gone — skip */ }
        }
    }
    catch { /* dir scan failures must never block startup */ }
}

// Marker type for assembly version lookup; the top-level statements file otherwise has no
// addressable class to reflect over.
public partial class Program { }
