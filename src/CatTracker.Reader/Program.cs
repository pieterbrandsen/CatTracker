using System.Globalization;
using System.Text;

// ---------------------------------------------------------------------------------------------
// CatTracker.Reader — the only privileged component in the system.
//
// It copies the Find My cache to a spool directory and writes a heartbeat saying how that went.
// That is the entire job. It is separate from the main app for one concrete reason: macOS grants
// Full Disk Access per binary, and replacing a binary revokes it. Keeping the privileged part
// tiny and frozen means you grant FDA once, then update the rest of CatTracker as often as you
// like without ever opening System Settings again.
//
// Consequently: resist adding features here. Parsing, storage and logic all belong in the app,
// which needs no special permission at all.
// ---------------------------------------------------------------------------------------------

var options = ReaderOptions.Parse(args);
if (options.ShowHelp)
{
    Console.WriteLine(ReaderOptions.Usage);
    return 0;
}

Console.WriteLine($"cattracker-reader: source={options.SourcePath}");
Console.WriteLine($"cattracker-reader: spool ={options.SpoolDirectory}");

Directory.CreateDirectory(options.SpoolDirectory);

var itemsPath = Path.Combine(options.SpoolDirectory, "items.json");
var heartbeatPath = Path.Combine(options.SpoolDirectory, "heartbeat.json");

long lastMTime = -1;
long lastSize = -1;

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdown.Cancel();

do
{
    Cycle();

    if (options.WatchSeconds <= 0) break;

    try
    {
        await Task.Delay(TimeSpan.FromSeconds(options.WatchSeconds), shutdown.Token);
    }
    catch (OperationCanceledException)
    {
        break;
    }
} while (!shutdown.IsCancellationRequested);

return 0;

void Cycle()
{
    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    try
    {
        var info = new FileInfo(options.SourcePath);
        if (!info.Exists)
        {
            WriteHeartbeat(now, "not_found",
                "Find My cache file does not exist. Is the Find My app installed and has it run " +
                "at least once on this account?", null, null);
            return;
        }

        var mtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
        var size = info.Length;

        if (mtime == lastMTime && size == lastSize)
        {
            // Nothing new. Still heartbeat, so the app can tell "reader is fine, Find My is idle"
            // apart from "reader is dead" — which look identical if you only watch items.json.
            WriteHeartbeat(now, "ok", "unchanged", mtime, size);
            return;
        }

        var content = ReadWithRetry(options.SourcePath, out var failure);
        if (content is null)
        {
            WriteHeartbeat(now, failure!.Status, failure.Detail, mtime, size);
            return;
        }

        // Cheapest possible sanity check on a torn read. Deliberately not a JSON parse: this
        // binary must never need updating because Apple renamed a field.
        var trimmed = content.AsSpan().TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '[' && trimmed[0] != '{'))
        {
            WriteHeartbeat(now, "error",
                "Cache did not look like JSON; likely read mid-write. Will retry.", mtime, size);
            return;
        }

        AtomicWrite(itemsPath, content);
        lastMTime = mtime;
        lastSize = size;

        WriteHeartbeat(now, "ok", "updated", mtime, size);
        Console.WriteLine($"cattracker-reader: copied {size} bytes (mtime {mtime})");
    }
    catch (UnauthorizedAccessException ex)
    {
        WriteHeartbeat(now, "permission_denied", Fda(ex.Message), null, null);
    }
    catch (Exception ex)
    {
        WriteHeartbeat(now, "error", ex.Message, null, null);
        Console.Error.WriteLine($"cattracker-reader: {ex}");
    }
}

static string Fda(string message) =>
    "Full Disk Access is not granted to cattracker-reader. Open System Settings > Privacy & " +
    "Security > Full Disk Access, add the cattracker-reader binary, and restart the agent. " +
    $"({message})";

string? ReadWithRetry(string path, out Failure? failure)
{
    failure = null;

    for (var attempt = 0; attempt < 3; attempt++)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (UnauthorizedAccessException ex)
        {
            // Not transient: no amount of retrying grants a TCC permission.
            failure = new Failure("permission_denied", Fda(ex.Message));
            return null;
        }
        catch (IOException ex)
        {
            failure = new Failure("error", ex.Message);
            Thread.Sleep(150 * (attempt + 1));
        }
    }

    return null;
}

static void AtomicWrite(string destination, string content)
{
    var temp = destination + ".tmp";
    File.WriteAllText(temp, content, new UTF8Encoding(false));
    File.Move(temp, destination, overwrite: true);
}

void WriteHeartbeat(long nowMs, string status, string? detail, long? mtime, long? size)
{
    // Hand-rolled JSON: no serializer, nothing for the trimmer to get wrong, no dependency that
    // could force this binary to change.
    var sb = new StringBuilder();
    sb.Append("{\"writtenUtcMs\":").Append(nowMs.ToString(CultureInfo.InvariantCulture));
    sb.Append(",\"status\":\"").Append(Escape(status)).Append('"');
    sb.Append(",\"detail\":");
    sb.Append(detail is null ? "null" : $"\"{Escape(detail)}\"");
    sb.Append(",\"sourceMTimeUtcMs\":")
      .Append(mtime?.ToString(CultureInfo.InvariantCulture) ?? "null");
    sb.Append(",\"sourceSizeBytes\":")
      .Append(size?.ToString(CultureInfo.InvariantCulture) ?? "null");
    sb.Append('}');

    try
    {
        AtomicWrite(heartbeatPath, sb.ToString());
    }
    catch (IOException ex)
    {
        Console.Error.WriteLine($"cattracker-reader: cannot write heartbeat: {ex.Message}");
    }
}

static string Escape(string value)
{
    var sb = new StringBuilder(value.Length + 8);
    foreach (var c in value)
    {
        switch (c)
        {
            case '"': sb.Append("\\\""); break;
            case '\\': sb.Append("\\\\"); break;
            case '\n': sb.Append("\\n"); break;
            case '\r': sb.Append("\\r"); break;
            case '\t': sb.Append("\\t"); break;
            default:
                if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(c);
                break;
        }
    }

    return sb.ToString();
}

internal sealed record Failure(string Status, string Detail);

internal sealed class ReaderOptions
{
    public const string Usage = """
        cattracker-reader — copies the Find My cache into CatTracker's spool directory.

          --source <path>    Cache file to read.
                             Default: ~/Library/Caches/com.apple.findmy.fmipcore/Items.data
          --spool  <dir>     Where to write items.json and heartbeat.json.
                             Default: ~/Library/Application Support/CatTracker/spool
          --watch  <secs>    Poll every N seconds. 0 = run once and exit. Default: 15
          --once             Same as --watch 0.
          -h, --help         This text.
        """;

    public string SourcePath { get; private set; } = DefaultSource();
    public string SpoolDirectory { get; private set; } = DefaultSpool();
    public int WatchSeconds { get; private set; } = 15;
    public bool ShowHelp { get; private set; }

    public static ReaderOptions Parse(string[] args)
    {
        var options = new ReaderOptions();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--source" when i + 1 < args.Length:
                    options.SourcePath = args[++i];
                    break;
                case "--spool" when i + 1 < args.Length:
                    options.SpoolDirectory = args[++i];
                    break;
                case "--watch" when i + 1 < args.Length:
                    options.WatchSeconds = int.TryParse(args[++i], out var s) ? s : 15;
                    break;
                case "--once":
                    options.WatchSeconds = 0;
                    break;
                case "-h":
                case "--help":
                    options.ShowHelp = true;
                    break;
            }
        }

        return options;
    }

    private static string Home() =>
        Environment.GetEnvironmentVariable("HOME")
        ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string DefaultSource() =>
        Path.Combine(Home(), "Library", "Caches", "com.apple.findmy.fmipcore", "Items.data");

    private static string DefaultSpool() =>
        Path.Combine(Home(), "Library", "Application Support", "CatTracker", "spool");
}
