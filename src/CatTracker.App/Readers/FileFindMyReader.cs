using System.Text.Json;
using CatTracker.Core;

namespace CatTracker.App.Readers;

/// <summary>
/// Reads a JSON cache file from disk — either the spool written by CatTracker.Reader (normal) or
/// the Find My cache directly (needs Full Disk Access for this app; handy for the Phase 0 spike).
/// </summary>
public sealed class FileFindMyReader(
    string itemsPath,
    string? heartbeatPath,
    string description,
    ILogger<FileFindMyReader> logger) : IFindMyReader
{
    private long _lastMTime = -1;
    private long _lastSize = -1;

    public string Description { get; } = description;

    public async Task<FindMySnapshot?> TryReadAsync(CancellationToken cancellationToken)
    {
        var info = new FileInfo(itemsPath);
        if (!info.Exists) return null;

        var mtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
        var size = info.Length;
        if (mtime == _lastMTime && size == _lastSize) return null;

        string json;
        try
        {
            json = await File.ReadAllTextAsync(itemsPath, cancellationToken);
        }
        catch (IOException ex)
        {
            // Almost certainly a read that raced the writer; the next poll picks it up.
            logger.LogDebug(ex, "Transient read failure on {Path}", itemsPath);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogError(ex,
                "Permission denied reading {Path}. If Source=Direct, this app needs Full Disk " +
                "Access; the supported setup is Source=Spool with CatTracker.Reader.", itemsPath);
            return null;
        }

        _lastMTime = mtime;
        _lastSize = size;
        return new FindMySnapshot(json, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public async Task<ReaderHeartbeat?> ReadHeartbeatAsync(CancellationToken cancellationToken)
    {
        if (heartbeatPath is null || !File.Exists(heartbeatPath)) return null;

        try
        {
            var json = await File.ReadAllTextAsync(heartbeatPath, cancellationToken);
            return JsonSerializer.Deserialize<ReaderHeartbeat>(
                json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            logger.LogDebug(ex, "Could not read heartbeat at {Path}", heartbeatPath);
            return null;
        }
    }
}
