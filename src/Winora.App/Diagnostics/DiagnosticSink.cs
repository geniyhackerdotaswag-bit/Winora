using System.Text;
using System.Text.RegularExpressions;
using Winora.Infrastructure.Paths;

namespace Winora.App.Diagnostics;

/// <summary>
/// The local diagnostic sink required by specification section 14. Raw exceptions go here and
/// nowhere else, with absolute paths and environment values redacted so a shared log cannot leak
/// where the user keeps their files.
/// </summary>
public static partial class DiagnosticSink
{
    private static readonly object Gate = new();

    /// <remarks>
    /// Beside the store rather than under <c>LocalApplicationData</c>. A packaged app has that
    /// folder redirected into its container, so the log both disappeared on uninstall and sat where
    /// the user could not reasonably find it — which matters most in exactly the situation it is
    /// written for, when the app will not start and there is nothing else to go on.
    /// </remarks>
    public static string LogPath { get; } = Path.Combine(
        WinoraDataPaths.RootForCurrentUser(),
        "diagnostics.log");

    public static void Write(string stage, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Append(stage, Redact(exception.ToString()));
    }

    /// <summary>
    /// Records a plain observation, not a failure.
    /// </summary>
    /// <remarks>
    /// Added because an operation that silently did nothing could not be told apart from one that
    /// never ran. Applying a sound scheme reported success on screen, the registry was untouched,
    /// and nothing anywhere said which of the two had happened — three rounds of guessing followed.
    /// A note here is cheaper than another round. Redacted like any other entry, so it stays safe
    /// to send.
    /// </remarks>
    public static void Note(string stage, string message) =>
        Append(stage, Redact(message ?? string.Empty));

    private static void Append(string stage, string body)
    {
        var correlationId = Guid.NewGuid().ToString("N")[..12];

        var entry = new StringBuilder()
            .Append(DateTimeOffset.UtcNow.ToString("O"))
            .Append("  [").Append(correlationId).Append("]  ")
            .Append(stage)
            .AppendLine()
            .AppendLine(body)
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
