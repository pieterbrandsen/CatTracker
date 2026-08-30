using System.Collections.Concurrent;
using CatTracker.Core;
using CatTracker.Data;
using Microsoft.Extensions.Options;

namespace CatTracker.App.Alerting;

/// <summary>
/// Records every alert and decides which ones actually get delivered.
///
/// The split matters: the history should be complete, but a "we have lost contact" condition that
/// re-notifies every ten seconds trains you to ignore it, and an alert you ignore is not an alert.
/// </summary>
public sealed class AlertDispatcher(
    Repository repository,
    IEnumerable<IAlertChannel> channels,
    IOptions<AppOptions> options,
    ILogger<AlertDispatcher> logger)
{
    private readonly ConcurrentDictionary<string, long> _lastDelivered = new();

    public async Task<Alert> RaiseAsync(
        AlertKind kind,
        string message,
        string cooldownKey,
        TimeSpan? cooldown = null,
        bool deliver = true,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var alert = new Alert { Kind = kind, Message = message, RaisedUtc = now };

        var window = cooldown ?? TimeSpan.FromMinutes(options.Value.Alerts.CooldownMinutes);
        var suppressed = deliver && IsSuppressed(cooldownKey, now, window);

        if (deliver && !suppressed)
        {
            alert.DeliveredUtc = now;
            _lastDelivered[cooldownKey] = now;
        }

        alert.Id = repository.InsertAlert(alert);

        if (!deliver)
        {
            logger.LogDebug("Alert {Kind} recorded without delivery: {Message}", kind, message);
            return alert;
        }

        if (suppressed)
        {
            logger.LogDebug("Alert {Kind} suppressed by cooldown ({Key})", kind, cooldownKey);
            return alert;
        }

        foreach (var channel in channels)
        {
            if (!channel.IsAvailable) continue;

            try
            {
                await channel.SendAsync(alert, cancellationToken);
            }
            catch (Exception ex)
            {
                // One broken channel must not stop the others, nor the collector.
                logger.LogError(ex, "Alert channel {Channel} failed", channel.Name);
            }
        }

        return alert;
    }

    private bool IsSuppressed(string key, long now, TimeSpan window)
    {
        if (window <= TimeSpan.Zero) return false;

        return _lastDelivered.TryGetValue(key, out var last)
               && now - last < (long)window.TotalMilliseconds;
    }

    /// <summary>Available channels, for the status page and the "test alert" button.</summary>
    public IReadOnlyList<string> AvailableChannels() =>
        channels.Where(c => c.IsAvailable).Select(c => c.Name).ToArray();
}
