using CatTracker.Core;

namespace CatTracker.App.Alerting;

public interface IAlertChannel
{
    string Name { get; }

    /// <summary>False when the channel cannot work here (wrong OS, not configured, disabled).</summary>
    bool IsAvailable { get; }

    Task SendAsync(Alert alert, CancellationToken cancellationToken);
}
