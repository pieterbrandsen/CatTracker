namespace CatTracker.App.Services;

/// <summary>Live collector health, surfaced by /api/status. Written by one thread, read by many.</summary>
public sealed class CollectorState
{
    private long _lastPollUtc;
    private long _lastSnapshotUtc;
    private long _lastNewFixUtc;
    private int _isStale;

    public string SourceDescription { get; set; } = "unknown";

    public long LastPollUtc
    {
        get => Interlocked.Read(ref _lastPollUtc);
        set => Interlocked.Exchange(ref _lastPollUtc, value);
    }

    public long LastSnapshotUtc
    {
        get => Interlocked.Read(ref _lastSnapshotUtc);
        set => Interlocked.Exchange(ref _lastSnapshotUtc, value);
    }

    public long LastNewFixUtc
    {
        get => Interlocked.Read(ref _lastNewFixUtc);
        set => Interlocked.Exchange(ref _lastNewFixUtc, value);
    }

    public bool IsStale
    {
        get => Interlocked.CompareExchange(ref _isStale, 0, 0) == 1;
        set => Interlocked.Exchange(ref _isStale, value ? 1 : 0);
    }

    public IReadOnlyList<string> LastWarnings { get; set; } = [];
    public string? LastError { get; set; }
}
