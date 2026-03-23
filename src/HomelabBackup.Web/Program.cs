using HomelabBackup.Core;
using HomelabBackup.Core.Config;
using HomelabBackup.Web.Components;
using HomelabBackup.Web.Services;

var builder = WebApplication.CreateBuilder(args);

var configPath = Environment.GetEnvironmentVariable("CONFIG_PATH") ?? "config/backup.yml";
var config = ConfigLoader.Load(configPath);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddHomelabBackupCore(config);
builder.Services.AddSingleton<BackupStateService>();
builder.Services.AddSingleton<BackupJobQueue>();
builder.Services.AddHostedService<BackupWorkerService>();
builder.Services.AddSingleton<InMemoryLoggerProvider>();

// Register the in-memory logger provider once the app is built
var app = builder.Build();

// Wire up in-memory logger now that DI is available
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
