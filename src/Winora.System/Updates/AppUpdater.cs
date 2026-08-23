using System.Diagnostics;

namespace Winora.System.Updates;

/// <summary>How an update attempt ended.</summary>
public enum UpdateOutcome
{
    /// <summary>The new program is in place and the process may now restart into it.</summary>
    Installed,

    /// <summary>Nothing arrived. The program is unchanged.</summary>
    DownloadFailed,

    /// <summary>What arrived was not what was promised. The program is unchanged.</summary>
    Verification,

    /// <summary>The file could not be put in place. The program is unchanged.</summary>
    SwapFailed,

    /// <summary>This copy is not the installed one, so it is not ours to replace.</summary>
    NotInstalled,

    /// <summary>
    /// The program was moved aside and could not be put back. It is beside its own path under
    /// <see cref="AppFileSwap.OldSuffix" />.
    /// </summary>
    /// <remarks>
    /// The rarest outcome and the only one somebody must act on. Reported apart from
    /// <see cref="SwapFailed" /> because telling a person nothing changed, while their program is
    /// missing from where they keep it, is the one answer worse than saying nothing.
    /// </remarks>
    Displaced,
}

/// <summary>Downloads a release and puts it in the place of the running program.</summary>
public interface IAppUpdater
{
    Task<UpdateOutcome> UpdateAsync(
        AppRelease release,
        IProgress<double>? progress,
        CancellationToken cancellationToken = default);

    /// <summary>Clears away what a previous update left behind. Called once at startup.</summary>
    void RemoveLeftovers();

    /// <summary>Starts the program that is now in place and asks this process to end.</summary>
    bool Restart();
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// Nothing happens here without somebody having pressed a button. Winora changes the machine it runs
/// on and asks for administrator rights to do it; a program of that kind replacing its own
/// executable in the background is not something a person can agree to after the fact. The same
/// reasoning is already written down for the bypass download, and it applies with more force to
/// Winora's own file.
/// </para>
/// <para>
/// A copy that is not the installed one is refused outright. It is sitting wherever its owner put
/// it — usually the Downloads folder — and rewriting a file there would be changing something nobody
/// offered up.
/// </para>
/// </remarks>
public sealed class AppUpdater : IAppUpdater
{
    private readonly IAppInstallLocation _location;
    private readonly HttpClient _http;

    public AppUpdater(IAppInstallLocation location)
        : this(location, CreateClient())
    {
    }

    public AppUpdater(IAppInstallLocation location, HttpClient http)
    {
        _location = location ?? throw new ArgumentNullException(nameof(location));
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Winora");
        return http;
    }

    public async Task<UpdateOutcome> UpdateAsync(
        AppRelease release,
        IProgress<double>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);

        if (!_location.IsInstalled)
        {
            return UpdateOutcome.NotInstalled;
        }

        var target = _location.InstalledExecutablePath;
        var fresh = target + AppFileSwap.FreshSuffix;

        try
        {
            string checksum;
            try
            {
                await DownloadAsync(release.DownloadUrl, fresh, release.SizeBytes, progress, cancellationToken)
                    .ConfigureAwait(false);

                checksum = await _http.GetStringAsync(release.ChecksumUrl, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return UpdateOutcome.DownloadFailed;
            }

            if (AppDownloadCheck.Verify(fresh, release.SizeBytes, checksum) != DownloadVerdict.Ok)
            {
                return UpdateOutcome.Verification;
            }

            return AppFileSwap.Replace(target, fresh) switch
            {
                SwapResult.Replaced => UpdateOutcome.Installed,
                SwapResult.Displaced => UpdateOutcome.Displaced,
                _ => UpdateOutcome.SwapFailed,
            };
        }
        finally
        {
            // Whatever happened, a half-written download is not left lying beside the program. On
            // the successful path the file has already been moved and there is nothing to remove.
            TryDelete(fresh);
        }
    }

    public void RemoveLeftovers() => AppFileSwap.RemoveLeftovers(_location.InstalledDirectory);

    /// <remarks>
    /// The caller ends this process afterwards. Started without a shell so the new program inherits
    /// nothing from the old one's console, and with the install folder as its working directory so
    /// it behaves exactly as it would from its shortcut.
    /// </remarks>
    public bool Restart()
    {
        try
        {
            var started = Process.Start(new ProcessStartInfo(_location.InstalledExecutablePath)
            {
                WorkingDirectory = _location.InstalledDirectory,
                UseShellExecute = false,
            });

            return started is not null;
        }
        catch (Exception)
        {
            // The file is in place and will run the next time it is opened by hand. Reported so the
            // screen can say to reopen it rather than sitting on a button that did nothing.
            return false;
        }
    }

    private async Task DownloadAsync(
        string url,
        string destination,
        long expectedSize,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? expectedSize;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var target = File.Create(destination);

        var buffer = new byte[81920];
        long written = 0;
        int read;

        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            written += read;

            if (total > 0)
            {
                progress?.Report(Math.Clamp((double)written / total, 0, 1));
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Cleared by RemoveLeftovers at the next startup.
        }
    }
}
