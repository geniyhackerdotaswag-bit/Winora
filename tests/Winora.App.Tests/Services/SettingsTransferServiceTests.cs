using Winora.App.Services;
using Winora.Core.Changes;
using Winora.Core.Contracts;
using Winora.Core.Settings;
using Xunit;

namespace Winora.App.Tests.Services;

/// <summary>
/// Carrying settings to another machine as a file.
/// </summary>
/// <remarks>
/// The whole answer to "we need a server for this". Reading is the probe every screen already
/// makes; writing is the same executor a click uses, with the same plan, backup and undo. What is
/// tested here is the part that is new: what travels, what does not, and what happens to a file
/// this build does not entirely understand.
/// </remarks>
public sealed class SettingsTransferServiceTests : IDisposable
{
    private readonly string _folder;

    public SettingsTransferServiceTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "winora-transfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception)
        {
            // A leftover temporary folder is not worth failing a test over.
        }
    }

    private string File(string name) => Path.Combine(_folder, name);

    private sealed class Text : ILocalizationService
    {
        public bool IsAvailable => true;

        public string Get(string key) => key;
    }

    /// <summary>An operation that remembers what it holds and lets it be set.</summary>
    private sealed class FakeOperation(string id, string value) : IOperation
    {
        public string OperationId { get; } = id;

        public string Value { get; set; } = value;

        public int Writes { get; private set; }

        public void Wrote(string next)
        {
            Value = next;
            Writes++;
        }

        public ValueTask<OperationCapability> ProbeAsync(OperationTarget target, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new OperationCapability(
                SupportStatus.Supported,
                PrivilegeRequirement.StandardUser,
                new StateFingerprint("test", Value),
                true, true, true, true, true, true,
                null,
                new DisplayValue("winora.value.shell-preference", Value)));

        public ValueTask<ChangePlan> PreviewAsync(OperationDraft draft, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<StepResult> ApplyStepAsync(ChangePlan plan, ChangeStep step, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<VerificationResult> VerifyStepAsync(ChangePlan plan, ChangeStep step, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<StepResult> RollbackStepAsync(RollbackPlan plan, ChangeStep step, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    /// <summary>Stands in for the change pipeline and records what it was asked to write.</summary>
    private sealed class FakeExecutor : IChangeExecutor
    {
        public bool Succeeds { get; set; } = true;

        public List<string> Applied { get; } = [];

        public Task<ChangeOutcome> ApplyAsync(IOperation operation, OperationDraft draft, CancellationToken cancellationToken)
        {
            Applied.Add(operation.OperationId);

            if (Succeeds && operation is FakeOperation fake)
            {
                fake.Wrote(draft.ProposedValue.Text);
            }

            return Task.FromResult(new ChangeOutcome(Succeeds, "message", CoordinatorDisposition.Completed));
        }
    }

    private static (SettingsTransferService Service, FakeExecutor Executor) Build(params IOperation[] operations)
    {
        var executor = new FakeExecutor();
        return (new SettingsTransferService(operations, executor, new Text()), executor);
    }

    [Fact]
    public async Task Only_the_settings_that_travel_are_captured()
    {
        var (service, _) = Build(
            new FakeOperation("winora.explorer.file-extensions", "0"),
            new FakeOperation("winora.shell.taskbar-alignment", "1"),
            new FakeOperation("windows.appearance.theme", "dark"),

            // Startup entries name programs installed on this machine. Enabling one where it is not
            // installed would write a run entry pointing at nothing.
            new FakeOperation("winora.startup.run.discord", "on"));

        var captured = await service.CaptureAsync();

        Assert.Equal(3, captured.Count);
        Assert.DoesNotContain(captured, c => c.OperationId.StartsWith("winora.startup.", StringComparison.Ordinal));
        Assert.Equal(3, service.PortableCount);
    }

    /// <summary>
    /// A setting Windows is deciding for itself is left out rather than carried as a word.
    /// </summary>
    /// <remarks>
    /// The probe says "unset" when the value is absent. Carrying that to another machine would ask
    /// it to store the literal text; leaving the setting out asks for nothing, which is what "I
    /// have not chosen this" means.
    /// </remarks>
    [Fact]
    public async Task A_setting_nobody_has_chosen_is_not_carried()
    {
        var (service, _) = Build(
            new FakeOperation("winora.shell.taskbar-alignment", "unset"),
            new FakeOperation("winora.explorer.file-extensions", "0"));

        var captured = await service.CaptureAsync();

        Assert.Single(captured);
        Assert.Equal("winora.explorer.file-extensions", captured[0].OperationId);
    }

    [Fact]
    public async Task What_was_saved_is_what_arrives_on_the_other_machine()
    {
        var path = File("carried" + SettingsFilePickerExtension);

        var (source, _) = Build(
            new FakeOperation("winora.explorer.file-extensions", "0"),
            new FakeOperation("windows.appearance.theme", "dark"));

        Assert.True(await source.SaveAsync(path));

        var extensions = new FakeOperation("winora.explorer.file-extensions", "1");
        var theme = new FakeOperation("windows.appearance.theme", "light");
        var (target, executor) = Build(extensions, theme);

        var report = await target.ApplyAsync(path);

        Assert.NotNull(report);
        Assert.Equal(2, report!.Applied);
        Assert.Empty(report.Refused);
        Assert.Empty(report.Failed);
        Assert.Equal("0", extensions.Value);
        Assert.Equal("dark", theme.Value);
        Assert.Equal(2, executor.Applied.Count);
    }

    /// <summary>
    /// A setting already holding the wanted value is left alone.
    /// </summary>
    /// <remarks>
    /// Writing it again would put a backup and a journal entry behind a change that changed
    /// nothing, and the journal is what somebody reads to find out what actually happened.
    /// </remarks>
    [Fact]
    public async Task A_setting_that_already_agrees_is_not_written_again()
    {
        var path = File("same" + SettingsFilePickerExtension);

        var (source, _) = Build(new FakeOperation("winora.explorer.file-extensions", "0"));
        Assert.True(await source.SaveAsync(path));

        var same = new FakeOperation("winora.explorer.file-extensions", "0");
        var (target, executor) = Build(same);

        var report = await target.ApplyAsync(path);

        Assert.Equal(0, report!.Applied);
        Assert.Equal(1, report.Unchanged);
        Assert.Empty(executor.Applied);
        Assert.Equal(0, same.Writes);
    }

    /// <summary>
    /// A setting the file names but this build does not know is reported, not applied.
    /// </summary>
    /// <remarks>
    /// It arrives from a newer Winora or from a hand edit. Acting on an identifier nothing here
    /// defines would mean writing somewhere no catalogue vouched for.
    /// </remarks>
    [Fact]
    public async Task A_setting_this_build_does_not_know_is_reported_and_the_rest_still_apply()
    {
        var path = File("mixed" + SettingsFilePickerExtension);

        global::System.IO.File.WriteAllText(path, """
            {"schemaVersion":1,"capturedUtc":"2026-08-30T00:00:00+00:00","entries":[
              {"operationId":"winora.explorer.file-extensions","value":"0"},
              {"operationId":"winora.explorer.from-a-newer-build","value":"7"}]}
            """);

        var extensions = new FakeOperation("winora.explorer.file-extensions", "1");
        var (service, _) = Build(extensions);

        var report = await service.ApplyAsync(path);

        Assert.Equal(1, report!.Applied);
        Assert.Single(report.Refused);
        Assert.Equal(SettingsRejection.Unknown, report.Refused[0].Rejection);
        Assert.Equal("0", extensions.Value);
    }

    [Fact]
    public async Task A_setting_that_would_not_take_is_counted_as_failed()
    {
        var path = File("fails" + SettingsFilePickerExtension);

        var (source, _) = Build(new FakeOperation("winora.explorer.file-extensions", "0"));
        Assert.True(await source.SaveAsync(path));

        var (target, executor) = Build(new FakeOperation("winora.explorer.file-extensions", "1"));
        executor.Succeeds = false;

        var report = await target.ApplyAsync(path);

        Assert.Equal(0, report!.Applied);
        Assert.Single(report.Failed);
    }

    /// <summary>Something that is not a settings file is refused whole, not read in part.</summary>
    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("[1,2,3]")]
    public async Task A_file_that_is_not_ours_is_refused(string content)
    {
        var path = File("foreign.txt");
        global::System.IO.File.WriteAllText(path, content);

        var (service, executor) = Build(new FakeOperation("winora.explorer.file-extensions", "1"));

        Assert.Null(await service.ApplyAsync(path));
        Assert.Empty(executor.Applied);
    }

    [Fact]
    public async Task A_file_that_is_not_there_is_refused()
    {
        var (service, _) = Build(new FakeOperation("winora.explorer.file-extensions", "1"));

        Assert.Null(await service.ApplyAsync(File("never-written")));
    }

    private const string SettingsFilePickerExtension = ".winora-settings";
}
