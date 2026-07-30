using System.Text;
using System.Text.RegularExpressions;

namespace Winora.App.Diagnostics;

/// <summary>
/// The local diagnostic sink required by specification section 14. Raw exceptions go here and
/// nowhere else, with absolute paths and environment values redacted so a shared log cannot leak
/// where the user keeps their files.
/// </summary>
public static partial class DiagnosticSink
{
    private static readonly object Gate = new();

    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Winora",
        "diagnostics.log");

    public static void Write(string stage, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var correlationId = Guid.NewGuid().ToString("N")[..12];

        var entry = new StringBuilder()
            .Append(DateTimeOffset.UtcNow.ToString("O"))
            .Append("  [").Append(correlationId).Append("]  ")
            .Append(stage)
            .AppendLine()
            .AppendLine(Redact(exception.ToString()))
            .ToString();

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, entry, Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // A diagnostic sink that throws would replace the real failure with its own.
        }
    }

    private static string Redact(string text)
    {
        var redacted = DriveLetterPath().Replace(text, "[path]");
        redacted = UncPath().Replace(redacted, "[unc]");
        return EnvironmentToken().Replace(redacted, "[env]");
    }

    [GeneratedRegex(@"[A-Za-z]:\\[^\s""'<>|]*", RegexOptions.Compiled)]
    private static partial Regex DriveLetterPath();

    [GeneratedRegex(@"\\\\[^\s""'<>|]+", RegexOptions.Compiled)]
    private static partial Regex UncPath();

    [GeneratedRegex(@"%[A-Za-z_][A-Za-z0-9_]*%", RegexOptions.Compiled)]
    private static partial Regex EnvironmentToken();
}
