using System.Diagnostics;
using System.Runtime.InteropServices;
using CatTracker.Core;
using Microsoft.Extensions.Options;

namespace CatTracker.App.Alerting;

/// <summary>Writes every alert to the log. Always available, and the audit trail of last resort.</summary>
public sealed class LogAlertChannel(ILogger<LogAlertChannel> logger) : IAlertChannel
{
    public string Name => "log";
    public bool IsAvailable => true;

    public Task SendAsync(Alert alert, CancellationToken cancellationToken)
    {
        // ToString() on the enum: Serilog renders a bare enum as a quoted JSON scalar, and this
        // line is meant to be read by a person.
        logger.LogWarning("ALERT [{Kind}] {Message}", alert.Kind.ToString(), alert.Message);
        return Task.CompletedTask;
    }
}

/// <summary>Native macOS notification banner via osascript.</summary>
public sealed class MacNotificationChannel(
    IOptions<AppOptions> options, ILogger<MacNotificationChannel> logger) : IAlertChannel
{
    public string Name => "macos-notification";

    public bool IsAvailable =>
        options.Value.Alerts.MacNotifications && RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    public async Task SendAsync(Alert alert, CancellationToken cancellationToken)
    {
        var script =
            $"display notification \"{AppleScript.Escape(alert.Message)}\" " +
            $"with title \"CatTracker\" subtitle \"{AppleScript.Escape(alert.Kind.ToString())}\"";

        await AppleScript.RunAsync(script, logger, cancellationToken);
    }
}

/// <summary>
/// Plays a system sound. Deliberately separate from the banner: a notification you have to be
/// looking at is no use for "she is out at 3am".
/// </summary>
public sealed class SoundAlertChannel(
    IOptions<AppOptions> options, ILogger<SoundAlertChannel> logger) : IAlertChannel
{
    public string Name => "sound";

    public bool IsAvailable =>
        options.Value.Alerts.Sound && RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    public async Task SendAsync(Alert alert, CancellationToken cancellationToken)
    {
        var name = options.Value.Alerts.SoundName;
        if (string.IsNullOrWhiteSpace(name)) return;

        var path = $"/System/Library/Sounds/{name}.aiff";
        if (!File.Exists(path))
        {
            logger.LogWarning("Sound {Path} not found; check Alerts:SoundName.", path);
            return;
        }

        await Shell.RunAsync("/usr/bin/afplay", [path], logger, cancellationToken);
    }
}

/// <summary>
/// Sends an iMessage to yourself. The only channel that leaves the LAN — and it travels through
/// your own Apple account rather than a third-party push service, which is the closest thing to
/// "local" that reaches a phone. Requires granting the app Automation access to Messages.
/// </summary>
public sealed class IMessageAlertChannel(
    IOptions<AppOptions> options, ILogger<IMessageAlertChannel> logger) : IAlertChannel
{
    public string Name => "imessage";

    public bool IsAvailable =>
        !string.IsNullOrWhiteSpace(options.Value.Alerts.IMessageTo)
        && RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    public async Task SendAsync(Alert alert, CancellationToken cancellationToken)
    {
        var to = AppleScript.Escape(options.Value.Alerts.IMessageTo);
        var body = AppleScript.Escape($"CatTracker: {alert.Message}");

        var script =
            $$"""
              tell application "Messages"
                  set targetService to 1st account whose service type = iMessage
                  set targetBuddy to participant "{{to}}" of targetService
                  send "{{body}}" to targetBuddy
              end tell
              """;

        await AppleScript.RunAsync(script, logger, cancellationToken);
    }
}

internal static class AppleScript
{
    public static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ");

    public static Task RunAsync(string script, ILogger logger, CancellationToken cancellationToken) =>
        Shell.RunAsync("/usr/bin/osascript", ["-e", script], logger, cancellationToken);
}

internal static class Shell
{
    public static async Task RunAsync(
        string fileName, string[] arguments, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            var info = new ProcessStartInfo(fileName)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };

            foreach (var argument in arguments) info.ArgumentList.Add(argument);

            using var process = Process.Start(info);
            if (process is null)
            {
                logger.LogWarning("Could not start {FileName}", fileName);
                return;
            }

            // An alert channel must never be able to wedge the collector.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));

            await process.WaitForExitAsync(timeout.Token);

            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync(CancellationToken.None);
                logger.LogWarning(
                    "{FileName} exited {Code}: {Error}", fileName, process.ExitCode, error.Trim());
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to run {FileName}", fileName);
        }
    }
}
