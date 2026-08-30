using System.Text;
using System.Text.RegularExpressions;

namespace CatTracker.App.Services;

public sealed record LogPage(string File, IReadOnlyList<string> Files, IReadOnlyList<string> Lines);

/// <summary>
/// Reads back the rolling log files over HTTP.
///
/// The Mac running this lives in a cupboard, and the whole point of the project is that you check
/// on things from your phone. Being able to see why an alert did or did not fire, without walking
/// over and opening Console.app, is the difference between diagnosing a problem and guessing.
/// </summary>
public sealed partial class LogTail(string directory)
{
    /// <summary>How much of the tail we are willing to read. Files roll at 32 MB.</summary>
    private const int TailBytes = 512 * 1024;

    public string Directory { get; } = directory;

    [GeneratedRegex(@"^cattracker-[0-9A-Za-z_.\-]+\.log$")]
    private static partial Regex LogFileName();

    public IReadOnlyList<string> Files()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];

        return System.IO.Directory
            .EnumerateFiles(Directory, "cattracker-*.log")
            .Select(Path.GetFileName)
            .Where(name => name is not null && LogFileName().IsMatch(name))
            .Select(name => name!)
            .OrderByDescending(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    public LogPage Read(int lines, string? contains, string? file)
    {
        var available = Files();
        if (available.Count == 0) return new LogPage("", available, []);

        // Only ever a bare, pattern-matched file name from our own directory: a log viewer is not
        // a reason to hand out arbitrary file reads.
        var name = file is not null && LogFileName().IsMatch(file) && available.Contains(file)
            ? file
            : available[0];

        var path = Path.Combine(Directory, name);
        var text = ReadTail(path);

        var all = text.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0);

        if (!string.IsNullOrWhiteSpace(contains))
            all = all.Where(line => line.Contains(contains, StringComparison.OrdinalIgnoreCase));

        var selected = all.TakeLast(Math.Clamp(lines, 1, 5000)).ToArray();
        return new LogPage(name, available, selected);
    }

    private static string ReadTail(string path)
    {
        try
        {
            // Serilog holds the current file open, so share everything.
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            var skipPartialFirstLine = stream.Length > TailBytes;
            if (skipPartialFirstLine) stream.Seek(-TailBytes, SeekOrigin.End);

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();

            // The first line of a mid-file seek is almost certainly cut in half.
            if (skipPartialFirstLine)
            {
                var newline = text.IndexOf('\n');
                if (newline >= 0) text = text[(newline + 1)..];
            }

            return text;
        }
        catch (IOException ex)
        {
            return $"Could not read log file: {ex.Message}";
        }
    }
}
