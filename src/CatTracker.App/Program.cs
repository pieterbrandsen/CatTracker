using System.Text.Json.Serialization;
using CatTracker.App;
using CatTracker.App.Alerting;
using CatTracker.App.Endpoints;
using CatTracker.App.Readers;
using CatTracker.App.Services;
using CatTracker.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

// The content root decides where wwwroot is found, and it defaults to the *current* directory —
// which is System32 for a Windows Service, / for a launchd agent, and wherever you happened to be
// standing when you ran the binary by hand. Left alone, the API answers happily while every page
// and stylesheet 404s.
//
// So: when the web root sits next to the binary — which is exactly what a published build looks
// like — anchor the content root there. Otherwise leave it alone, because whatever is hosting us
// (dotnet run, or the test server) already knows better than we do.
var publishedWebRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = Directory.Exists(publishedWebRoot) ? AppContext.BaseDirectory : null,
});

// Run as a Windows Service when Windows started us as one; a no-op everywhere else, including
// when the same binary is run from a terminal.
builder.Host.UseWindowsService(options => options.ServiceName = "CatTracker");

// Environment first, so a launchd plist or a service definition can point at a data directory
// before we go looking for the local config file that lives inside it.
builder.Configuration.AddEnvironmentVariables("CATTRACKER_");

var bootstrap = new AppOptions();
builder.Configuration.GetSection(AppOptions.SectionName).Bind(bootstrap);

var dataDirectory = bootstrap.ResolveDataDirectory();
Directory.CreateDirectory(dataDirectory);

// config.local.json lives with your data, not with the binaries, so an update can never
// overwrite your settings. Environment variables are re-applied last and win.
builder.Configuration.AddJsonFile(
    Path.Combine(dataDirectory, "config.local.json"), optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables("CATTRACKER_");

builder.Services.Configure<AppOptions>(builder.Configuration.GetSection(AppOptions.SectionName));

// ---- logging ---------------------------------------------------------------------------------

// Rolling files next to the data, not the binaries, so an update never deletes your history of
// what happened. Levels come from configuration, so raising verbosity on the Mac to chase a
// problem is a config edit and a restart — not a rebuild and redeploy from Windows.
var logDirectory = Path.Combine(dataDirectory, "logs");
Directory.CreateDirectory(logDirectory);

const string LogTemplate =
    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

builder.Services.AddSerilog((services, configuration) =>
{
    configuration
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.File(
            path: Path.Combine(logDirectory, "cattracker-.log"),
            rollingInterval: RollingInterval.Day,
            rollOnFileSizeLimit: true,
            fileSizeLimitBytes: Math.Max(1, bootstrap.Diagnostics.FileSizeLimitMb) * 1024L * 1024L,
            retainedFileCountLimit: Math.Max(1, bootstrap.Diagnostics.RetainedDays),
            // Small flush interval so the in-app log viewer shows what just happened.
            flushToDiskInterval: TimeSpan.FromSeconds(2),
            outputTemplate: LogTemplate);

    if (bootstrap.Diagnostics.Console)
        configuration.WriteTo.Console(outputTemplate: LogTemplate);
});

builder.Services.AddSingleton(new LogTail(logDirectory));

builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    json.SerializerOptions.WriteIndented = false;
});

// ---- storage ---------------------------------------------------------------------------------

builder.Services.AddPooledDbContextFactory<CatContext>(options => options.UseSqlite(
    SqliteContextFactory.BuildConnectionString(Path.Combine(dataDirectory, "cattracker.db"))));

builder.Services.AddSingleton<Repository>();

// ---- position source -------------------------------------------------------------------------

builder.Services.AddSingleton<IFindMyReader>(services =>
{
    var options = services.GetRequiredService<IOptions<AppOptions>>();
    var settings = options.Value.FindMy;
    var loggerFactory = services.GetRequiredService<ILoggerFactory>();
    var logger = loggerFactory.CreateLogger<FileFindMyReader>();

    switch (settings.Source)
    {
        case FindMySource.Replay:
            return new ReplayFindMyReader(options);

        case FindMySource.Direct:
            var direct = AppOptions.Expand(settings.DirectPath);
            return new FileFindMyReader(
                direct, null, $"Find My cache, read directly ({direct})", logger);

        default:
            var spool = string.IsNullOrWhiteSpace(settings.SpoolDirectory)
                ? Path.Combine(dataDirectory, "spool")
                : AppOptions.Expand(settings.SpoolDirectory);

            Directory.CreateDirectory(spool);
            return new FileFindMyReader(
                Path.Combine(spool, "items.json"),
                Path.Combine(spool, "heartbeat.json"),
                $"Spool from cattracker-reader ({spool})",
                logger);
    }
});

// ---- alerting --------------------------------------------------------------------------------

builder.Services.AddSingleton<IAlertChannel, LogAlertChannel>();
builder.Services.AddSingleton<IAlertChannel, MacNotificationChannel>();
builder.Services.AddSingleton<IAlertChannel, SoundAlertChannel>();
builder.Services.AddSingleton<IAlertChannel, IMessageAlertChannel>();
builder.Services.AddSingleton<AlertDispatcher>();

// ---- collection ------------------------------------------------------------------------------

builder.Services.AddSingleton<CollectorState>();
builder.Services.AddSingleton<FixProcessor>();
builder.Services.AddSingleton<Ingestor>();
builder.Services.AddSingleton<DemoDataSeeder>();

if (bootstrap.FindMy.Enabled)
    builder.Services.AddHostedService<CollectorService>();

// ---- map tiles -------------------------------------------------------------------------------

builder.Services.AddSingleton<TileSeedState>();
builder.Services.AddHttpClient("tiles", (services, client) =>
{
    var options = services.GetRequiredService<IOptions<AppOptions>>().Value;
    client.DefaultRequestHeaders.UserAgent.ParseAdd(options.Tiles.UserAgent);
    client.Timeout = TimeSpan.FromSeconds(20);
});

builder.Services.AddPooledDbContextFactory<TileContext>(options => options.UseSqlite(
    SqliteContextFactory.BuildConnectionString(Path.Combine(dataDirectory, "tiles.db"))));

builder.Services.AddSingleton(services => new TileCache(
    services.GetRequiredService<IDbContextFactory<TileContext>>(),
    services.GetRequiredService<IHttpClientFactory>().CreateClient("tiles"),
    services.GetRequiredService<IOptions<AppOptions>>(),
    services.GetRequiredService<ILogger<TileCache>>()));

var app = builder.Build();

// Applied on every start, which is what makes updating the app "replace the binaries" and
// nothing else. A schema change ships with the build that needs it.
var applied = DatabaseSetup.Migrate(app.Services.GetRequiredService<IDbContextFactory<CatContext>>());
if (applied.Count > 0)
    app.Logger.LogInformation("Applied {Count} migration(s): {Names}", applied.Count, string.Join(", ", applied));

TileSetup.Ensure(app.Services.GetRequiredService<IDbContextFactory<TileContext>>());

app.Logger.LogInformation("CatTracker {Version}", ApiEndpoints.Version());
app.Logger.LogInformation("Data directory: {Directory}", dataDirectory);
app.Logger.LogInformation(
    "Position source: {Source}", app.Services.GetRequiredService<IFindMyReader>().Description);

app.Logger.LogInformation("Logs: {Directory}", logDirectory);

// HTTP request logging sits at Debug on purpose: at Information it would bury the cat under tile
// requests. Set Serilog:MinimumLevel:Default to Debug in config.local.json when you need it.
app.UseSerilogRequestLogging(logging =>
{
    logging.GetLevel = (context, _, exception) =>
        exception is not null ? LogEventLevel.Error
        : context.Request.Path.StartsWithSegments("/tiles") ? LogEventLevel.Verbose
        : LogEventLevel.Debug;
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapApi();

app.Run();

/// <summary>Exposed so the integration tests can host the real application.</summary>
public partial class Program;
