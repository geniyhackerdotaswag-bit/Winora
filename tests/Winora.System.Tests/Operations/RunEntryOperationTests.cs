using Microsoft.Win32;
using Winora.Core.Changes;
using Winora.Core.Contracts;
using Winora.System.Operations;
using Winora.System.Windows;
using Xunit;

namespace Winora.System.Tests.Operations;

public sealed class RunEntryOperationTests
{
    private const string LongName = "YandexBrowserAutoLaunch_EFB5B37C64649EA7404EBBBAEC96AF4B";

    [Fact]
    public void The_identifier_fits_the_catalog_limit_even_for_a_long_entry_name()
    {
        var id = RunEntryOperation.IdFor(LongName);

        Assert.True(id.Length <= 96, $"'{id}' is {id.Length} characters.");
        Assert.Matches("^[a-z][a-z0-9]*(\\.[a-z][a-z0-9]*)*$", id);
    }

    [Fact]
    public void Different_entry_names_get_different_identifiers()
    {
        Assert.NotEqual(RunEntryOperation.IdFor("Steam"), RunEntryOperation.IdFor("Discord"));
    }

    /// <summary>
    /// The whole reason disabling moves the value instead of deleting it: a fresh process must be
    /// able to rebuild the operation from the identifier alone, and it does that by hashing the
    /// names it can still see.
    /// </summary>
    [Fact]
    public void A_disabled_entry_is_still_reconstructible_from_its_identifier()
    {
        var store = new StubStore();
        store.Enabled["Steam"] = "steam.exe -silent";
        var id = RunEntryOperation.IdFor("Steam");
        var factory = new RunEntryOperationFactory(store);

        store.Disable("Steam");

        Assert.True(factory.TryCreate(id, out var rebuilt));
        Assert.Equal(id, rebuilt!.OperationId);
    }

    [Fact]
    public void An_entry_that_exists_nowhere_is_not_reconstructed()
    {
        var factory = new RunEntryOperationFactory(new StubStore());

        Assert.False(factory.TryCreate(RunEntryOperation.IdFor("Ghost"), out var operation));
        Assert.Null(operation);
    }

    [Fact]
    public void An_identifier_from_another_domain_is_ignored()
    {
        var factory = new RunEntryOperationFactory(new StubStore());

        Assert.False(factory.TryCreate("winora.visual-effects.ui-effects", out _));
    }

    [Fact]
    public async Task Disabling_moves_the_entry_and_preserves_its_command_exactly()
    {
        var store = new StubStore();
        store.Enabled["Steam"] = "\"C:\\Steam\\steam.exe\" -silent";
        var operation = new RunEntryOperation("Steam", store);
        var plan = await operation.PreviewAsync(Draft(operation, RunEntryValues.Disabled), default);

        var result = await operation.ApplyStepAsync(plan, plan.Steps[0], default);

        Assert.Equal(StepResultKind.Applied, result.Kind);
        Assert.False(store.Enabled.ContainsKey("Steam"));
        Assert.Equal("\"C:\\Steam\\steam.exe\" -silent", store.Disabled["Steam"]);
        Assert.True((await operation.VerifyStepAsync(plan, plan.Steps[0], default)).IsVerified);
    }

    [Fact]
    public async Task Re_enabling_restores_the_command_byte_for_byte()
    {
        var store = new StubStore();
        var command = "\"C:\\Program Files\\App\\app.exe\" --flag \"a b\"";
        store.Disabled["App"] = command;
        var operation = new RunEntryOperation("App", store);
        var plan = await operation.PreviewAsync(Draft(operation, RunEntryValues.Enabled), default);

        await operation.ApplyStepAsync(plan, plan.Steps[0], default);

        Assert.Equal(command, store.Enabled["App"]);
        Assert.False(store.Disabled.ContainsKey("App"));
    }

    [Fact]
    public async Task Rollback_returns_the_entry_and_is_idempotent()
    {
        var store = new StubStore();
        store.Enabled["Steam"] = "steam.exe";
        var operation = new RunEntryOperation("Steam", store);
        var plan = await operation.PreviewAsync(Draft(operation, RunEntryValues.Disabled), default);
        await operation.ApplyStepAsync(plan, plan.Steps[0], default);

        var rollback = RollbackFor(plan);
        var first = await operation.RollbackStepAsync(rollback, plan.Steps[0], default);
        var moves = store.MoveCount;
        var second = await operation.RollbackStepAsync(rollback, plan.Steps[0], default);

        Assert.Equal(StepResultKind.Applied, first.Kind);
        Assert.True(store.Enabled.ContainsKey("Steam"));
        Assert.Equal(StepResultKind.AlreadyRestored, second.Kind);
        Assert.Equal(moves, store.MoveCount);
    }

    [Fact]
    public async Task Planning_a_state_the_entry_already_holds_is_refused()
    {
        var store = new StubStore();
        store.Enabled["Steam"] = "steam.exe";
        var operation = new RunEntryOperation("Steam", store);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await operation.PreviewAsync(Draft(operation, RunEntryValues.Enabled), default));
    }

    [Fact]
    public async Task External_drift_after_the_dry_run_is_refused_rather_than_overwritten()
    {
        var store = new StubStore();
        store.Enabled["Steam"] = "steam.exe";
        var operation = new RunEntryOperation("Steam", store);
        var plan = await operation.PreviewAsync(Draft(operation, RunEntryValues.Disabled), default);

        store.Disable("Steam");
        var moves = store.MoveCount;
        var result = await operation.ApplyStepAsync(plan, plan.Steps[0], default);

        Assert.Equal(StepResultKind.NotApplied, result.Kind);
        Assert.Equal(moves, store.MoveCount);
    }

    [Fact]
    public async Task The_plan_states_that_a_sign_out_is_needed_and_needs_no_elevation()
    {
        var store = new StubStore();
        store.Enabled["Steam"] = "steam.exe";
        var operation = new RunEntryOperation("Steam", store);

        var plan = await operation.PreviewAsync(Draft(operation, RunEntryValues.Disabled), default);

        Assert.Equal(RestartRequirement.SignOut, plan.Restart);
        Assert.Equal(PrivilegeRequirement.StandardUser, plan.Privilege);
        Assert.Equal(RollbackCapability.Full, plan.Rollback);
        Assert.False(plan.RequiresRestorePoint);
    }

    private static OperationDraft Draft(RunEntryOperation operation, string proposed) => new(
        operation.OperationId,
        "winora.category.system",
        operation.EntryName,
        "Moves a documented startup entry between the Run key and Winora's holding key.",
        new OperationTarget(operation.OperationId),
        new DisplayValue(RunEntryValues.Kind, proposed));

    private static RollbackPlan RollbackFor(ChangePlan plan) => RollbackPlan.Create(
        Guid.NewGuid(),
        plan,
        BackupReceipt.Verified("backup", "BACKUP-DIGEST", plan.Digest, plan.SourceFingerprint, plan.SourceFingerprint),
        plan.Steps[^1].ResultFingerprint);

    private sealed class StubStore : IRunEntryStore
    {
        public Dictionary<string, string> Enabled { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> Disabled { get; } = new(StringComparer.Ordinal);

        public int MoveCount { get; private set; }

        public IReadOnlyList<string> EnabledNames() => Enabled.Keys.ToArray();

        public IReadOnlyList<string> DisabledNames() => Disabled.Keys.ToArray();

        public RunEntryReading Read(string name)
        {
            if (Enabled.TryGetValue(name, out var enabled))
            {
                return new RunEntryReading(RunEntryState.Enabled, enabled, RegistryValueKind.String, true);
            }

            return Disabled.TryGetValue(name, out var disabled)
                ? new RunEntryReading(RunEntryState.Disabled, disabled, RegistryValueKind.String, true)
                : new RunEntryReading(RunEntryState.Absent, null, RegistryValueKind.String, true);
        }

        public RunEntryWriteOutcome Enable(string name) => Move(name, Disabled, Enabled);

        public RunEntryWriteOutcome Disable(string name) => Move(name, Enabled, Disabled);

        private RunEntryWriteOutcome Move(
            string name,
            Dictionary<string, string> from,
            Dictionary<string, string> to)
        {
            if (!from.TryGetValue(name, out var command))
            {
                return RunEntryWriteOutcome.NotWritten;
            }

            MoveCount++;
            to[name] = command;
            from.Remove(name);
            return RunEntryWriteOutcome.Written;
        }
    }
}
