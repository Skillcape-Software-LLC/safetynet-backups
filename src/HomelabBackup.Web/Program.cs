using HomelabBackup.Core;
using HomelabBackup.Core.Config;
using HomelabBackup.Core.Data;
using HomelabBackup.Web.Components;
using HomelabBackup.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// --- Database setup ---
var dbPath = Environment.GetEnvironmentVariable("DB_PATH") ?? "data/safetynet.db";
var repository = new SqliteConfigRepository(dbPath);
repository.EnsureSchema();

// YAML migration: if a backup.yml exists and the DB is empty, migrate automatically
var legacyConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH") ?? "config/backup.yml";
if (File.Exists(legacyConfigPath) && repository.IsEmpty())
{
    try
    {
        var legacyConfig = ConfigLoader.Load(legacyConfigPath);
        repository.Save(legacyConfig);
        File.Move(legacyConfigPath, legacyConfigPath + ".migrated", overwrite: true);
        Console.WriteLine($"[SafetyNet] Migrated config from {legacyConfigPath} to {dbPath}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[SafetyNet] YAML migration failed (continuing with empty config): {ex.Message}");
    }
}

// Also apply BROWSE_ROOT env var as a convenience override
var config = repository.Load();
var browseRootEnv = Environment.GetEnvironmentVariable("BROWSE_ROOT");
if (!string.IsNullOrWhiteSpace(browseRootEnv) && string.IsNullOrWhiteSpace(config.BrowseRoot))
    config.BrowseRoot = browseRootEnv;

// --- Services ---
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSingleton<IConfigRepository>(repository);
builder.Services.AddHomelabBackupCore(config);
builder.Services.AddSingleton<ConfigService>();
builder.Services.AddSingleton<BackupStateService>();
builder.Services.AddSingleton<BackupJobQueue>();
builder.Services.AddHostedService<BackupWorkerService>();
builder.Services.AddSingleton<InMemoryLoggerProvider>();

var app = builder.Build();

// Wire up in-memory logger
var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
var logProvider = app.Services.GetRequiredService<InMemoryLoggerProvider>();
loggerFactory.AddProvider(logProvider);

// Wire scheduler progress to BackupStateService for real-time UI updates
var scheduler = app.Services.GetService<HomelabBackup.Core.Services.SchedulerService>();
if (scheduler is not null)
{
    var stateService = app.Services.GetRequiredService<BackupStateService>();
    scheduler.ProgressFactory = sourceName => stateService.CreateProgress(sourceName);
    scheduler.OnResultCompleted = result => stateService.ReportCompletion(result);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
