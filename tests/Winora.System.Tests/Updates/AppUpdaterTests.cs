using System.Net;
using System.Security.Cryptography;
using System.Text;
using Winora.System.Updates;
using Xunit;

namespace Winora.System.Tests.Updates;

/// <summary>
/// The whole run: download, judge, put in place.
/// </summary>
/// <remarks>
/// Nothing here restarts anything. Starting the new program is the last step and the one step that
/// cannot be observed from inside the test that asked for it; what is checked is everything up to
/// it, and above all what is left on disk when a step goes wrong. The rule every case below shares:
/// a failed update leaves a program that still runs.
/// </remarks>
public sealed class AppUpdaterTests : IDisposable
{
    private readonly string _folder;
    private readonly string _target;

    public AppUpdaterTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "winora-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        _target = Path.Combine(_folder, "Winora.exe");
        File.WriteAllText(_target, "the program that is running");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception)
        {
            // A leftover temp folder is not worth failing a passing test over.
        }
    }

    private static byte[] Program(int length)
    {
        var bytes = new byte[length];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        for (var index = 2; index < length; index++)
        {
            bytes[index] = (byte)(index % 251);
        }

        return bytes;
    }

    /// <summary>The place the updater believes it is installed: our temp folder, not the real one.</summary>
    private sealed class HereLocation(string current) : IAppInstallLocation
    {
        public string CurrentExecutablePath => current;

        public string InstalledDirectory => Path.GetDirectoryName(current)!;

        public string InstalledExecutablePath => current;

        public bool IsInstalled { get; init; } = true;
    }

    private AppUpdater Updater(byte[] program, string checksum, bool installed = true) =>
        new(
            new HereLocation(_target) { IsInstalled = installed },
            new HttpClient(new TwoFileHandler(program, checksum)));

    private static AppRelease Release(long size) => new(
        new Version(0, 4, 0), "v0.4.0", "notes",
        "https://example.invalid/Winora.exe",
        "https://example.invalid/Winora.exe.sha256",
        size,
        DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task A_good_update_lands()
    {
        var program = Program(2048);
        var updater = Updater(program, Convert.ToHexString(SHA256.HashData(program)));

        Assert.Equal(UpdateOutcome.Installed, await updater.UpdateAsync(Release(program.Length), null));
        Assert.Equal(program, File.ReadAllBytes(_target));
    }

    /// <summary>
    /// Progress is reported while the download runs, and the last report is the end of it.
    /// </summary>
    /// <remarks>
    /// Recorded through a synchronous IProgress rather than Progress&lt;T&gt;, which hands its
    /// callbacks to the synchronization context and would leave the assertions racing the reports.
    /// A test that needed a sleep to pass would be a test that fails on a loaded machine.
    /// </remarks>
    [Fact]
    public async Task Progress_is_reported_and_ends_at_the_end()
    {
        var program = Program(200_000);
        var updater = Updater(program, Convert.ToHexString(SHA256.HashData(program)));
        var seen = new SynchronousProgress();

        await updater.UpdateAsync(Release(program.Length), seen);

        Assert.NotEmpty(seen.Values);
        Assert.All(seen.Values, value => Assert.InRange(value, 0d, 1d));
        Assert.Equal(1d, seen.Values[^1], 3);
    }

    /// <summary>The three refusals of AppDownloadCheck, each leaving the program untouched.</summary>
    [Fact]
    public async Task A_download_that_does_not_verify_changes_nothing()
    {
        var program = Program(2048);
        var updater = Updater(program, new string('a', 64));

        Assert.Equal(
            UpdateOutcome.Verification,
            await updater.UpdateAsync(Release(program.Length), null));

        Assert.Equal("the program that is running", File.ReadAllText(_target));
    }

    [Fact]
    public async Task A_web_page_instead_of_a_program_changes_nothing()
    {
        var page = Encoding.UTF8.GetBytes("<!doctype html><title>Sign in</title>");
        var updater = Updater(page, Convert.ToHexString(SHA256.HashData(page)));

        Assert.Equal(UpdateOutcome.Verification, await updater.UpdateAsync(Release(page.Length), null));
        Assert.Equal("the program that is running", File.ReadAllText(_target));
    }

    [Fact]
    public async Task A_download_that_fails_changes_nothing()
    {
        var updater = new AppUpdater(
            new HereLocation(_target),
            new HttpClient(new FailingHandler()));

        Assert.Equal(UpdateOutcome.DownloadFailed, await updater.UpdateAsync(Release(2048), null));
        Assert.Equal("the program that is running", File.ReadAllText(_target));
    }

    /// <summary>
    /// A copy running from wherever it was downloaded is never replaced. Nobody agreed to that file
    /// being changed, and the folder it sits in is not ours to write into.
    /// </summary>
    [Fact]
    public async Task A_copy_that_is_not_installed_is_never_replaced()
    {
        var program = Program(2048);
        var updater = Updater(program, Convert.ToHexString(SHA256.HashData(program)), installed: false);

        Assert.Equal(
            UpdateOutcome.NotInstalled,
            await updater.UpdateAsync(Release(program.Length), null));

        Assert.Equal("the program that is running", File.ReadAllText(_target));
    }

    /// <summary>Nothing half-written is left behind when the update is refused.</summary>
    [Fact]
    public async Task A_refused_update_leaves_no_debris()
    {
        var program = Program(2048);
        var updater = Updater(program, new string('a', 64));

        await updater.UpdateAsync(Release(program.Length), null);

        Assert.False(File.Exists(_target + AppFileSwap.FreshSuffix));
    }

    /// <summary>Records every report on the thread that made it, so nothing has to be waited for.</summary>
    private sealed class SynchronousProgress : IProgress<double>
    {
        private readonly List<double> _values = [];

        public IReadOnlyList<double> Values => _values;

        public void Report(double value) => _values.Add(value);
    }

    /// <summary>Serves the program on one address and its checksum on the other.</summary>
    private sealed class TwoFileHandler(byte[] program, string checksum) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();

            HttpContent content = url.EndsWith(".sha256", StringComparison.Ordinal)
                ? new StringContent(checksum, Encoding.ASCII)
                : new ByteArrayContent(program);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("no network");
    }
}
