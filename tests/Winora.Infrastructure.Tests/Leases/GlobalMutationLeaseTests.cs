using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Winora.Core.Changes;
using Winora.Core.Contracts;
using Winora.Lease.ProcessHost;
using Winora.Infrastructure.Leases;
using Winora.Infrastructure.Journal;
using Winora.Infrastructure.Operations;
using Winora.Infrastructure.Paths;
using Winora.Infrastructure.Persistence;
using Winora.Infrastructure.Tests.Operations;
using Winora.Infrastructure.Tests.Persistence;
using Xunit;

namespace Winora.Infrastructure.Tests.Leases;

public sealed class GlobalMutationLeaseTests
{
    private static readonly DateTimeOffset AcquiredUtc =
        new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Normal_acquire_is_blocked_by_a_verified_incomplete_operation_without_a_lease_record()
    {
        using var directory = new TemporaryDirectory();
        var fixture = new LeaseFixture(directory.Path, AcquiredUtc);
        var incompleteOperationId = Guid.Parse("72dc9392-cb76-49f7-a830-9fd6ea7462a1");
        await AppendPlannedAsync(fixture.Paths, incompleteOperationId);

        var acquired = await fixture.Create(
                fixture.Owner(91, 9, MutationLeasePackageRole.App))
            .TryAcquireAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(acquired);
    }

    [Fact]
    public async Task Dead_owner_without_a_durable_operation_is_reaped_before_normal_acquire()
    {
        using var directory = new TemporaryDirectory();
        var fixture = new LeaseFixture(directory.Path, AcquiredUtc);
        var crashedOwner = fixture.Owner(92, 10, MutationLeasePackageRole.App);
        var replacementOwner = fixture.Owner(93, 11, MutationLeasePackageRole.App);
        fixture.Validator.Set(crashedOwner, MutationLeaseOwnerStatus.Alive);
        var abandoned = Assert.IsType<GlobalMutationLeaseHandle>(
            await fixture.Create(crashedOwner).TryAcquireAsync(
                Guid.NewGuid(),
                CancellationToken.None));
        fixture.Validator.Set(crashedOwner, MutationLeaseOwnerStatus.ProvenDead);

        await using var replacement = Assert.IsType<GlobalMutationLeaseHandle>(
            await fixture.Create(replacementOwner).TryAcquireAsync(
                Guid.NewGuid(),
                CancellationToken.None));

        Assert.Equal(2, replacement.Epoch);
        await abandoned.DisposeAsync();
    }

    [Fact]
    public async Task Dead_owner_with_a_verified_terminal_operation_is_reaped_before_normal_acquire()
    {
        using var directory = new TemporaryDirectory();
        var fixture = new LeaseFixture(directory.Path, AcquiredUtc);
        var operationId = Guid.Parse("713007f0-4d44-4cea-b5c4-e224630b1f73");
        var crashedOwner = fixture.Owner(94, 12, MutationLeasePackageRole.App);
        fixture.Validator.Set(crashedOwner, MutationLeaseOwnerStatus.Alive);
        var abandoned = Assert.IsType<GlobalMutationLeaseHandle>(
            await fixture.Create(crashedOwner).TryAcquireAsync(
                operationId,
                CancellationToken.None));
        await AppendTerminalAsync(fixture.Paths, operationId);
        fixture.Validator.Set(crashedOwner, MutationLeaseOwnerStatus.ProvenDead);

        await using var replacement = Assert.IsType<GlobalMutationLeaseHandle>(
            await fixture.Create(fixture.Owner(95, 13, MutationLeasePackageRole.App))
                .TryAcquireAsync(Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(2, replacement.Epoch);
        await abandoned.DisposeAsync();
    }

    [Fact]
    public async Task Recovery_takeover_rejects_a_dead_lease_without_a_durable_operation()
    {
        using var directory = new TemporaryDirectory();
        var fixture = new LeaseFixture(directory.Path, AcquiredUtc);
        var operationId = Guid.Parse("b759c83d-a1a9-4c8a-b00b-adb4a3d8e585");
        var crashedOwner = fixture.Owner(96, 14, MutationLeasePackageRole.App);
        fixture.Validator.Set(crashedOwner, MutationLeaseOwnerStatus.Alive);
        var abandoned = Assert.IsType<GlobalMutationLeaseHandle>(
            await fixture.Create(crashedOwner).TryAcquireAsync(
                operationId,
                CancellationToken.None));
        fixture.Validator.Set(crashedOwner, MutationLeaseOwnerStatus.ProvenDead);

        var recovered = await fixture.Create(
                fixture.Owner(97, 15, MutationLeasePackageRole.App))
            .TryAcquireRecoveryAsync(operationId, CancellationToken.None);

        Assert.Null(recovered);
        await abandoned.DisposeAsync();
    }

    [Fact]
    public async Task Released_lease_with_a_verified_incomplete_operation_blocks_normal_acquire()
    {
        using var directory = new TemporaryDirectory();
        var fixture = new LeaseFixture(directory.Path, AcquiredUtc);
        var operationId = Guid.Parse("814f7a73-a87b-4bed-aab7-0cb5798fb751");
        var first = Assert.IsType<GlobalMutationLeaseHandle>(
            await fixture.Create(fixture.Owner(98, 16, MutationLeasePackageRole.App))
                .TryAcquireAsync(operationId, CancellationToken.None));
        await AppendPlannedAsync(fixture.Paths, operationId);
        await first.DisposeAsync();

        var next = await fixture.Create(
                fixture.Owner(99, 17, MutationLeasePackageRole.App))
            .TryAcquireAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(next);
    }

    [Fact]
    public async Task Released_lease_with_an_incomplete_retention_marker_blocks_normal_acquire()
    {
        using var directory = new TemporaryDirectory();
        var fixture = new LeaseFixture(directory.Path, AcquiredUtc);
        var transactionId = Guid.Parse("391e0d3d-9197-46e5-bdce-1ad5aa923410");
        var first = Assert.IsType<GlobalMutationLeaseHandle>(
            await fixture.Create(fixture.Owner(108, 19, MutationLeasePackageRole.App))
                .TryAcquireAsync(transactionId, CancellationToken.None));
        await AppendRetentionApprovedAsync(fixture.Paths, fixture.Clock, transactionId, first);
        await first.DisposeAsync();

        var next = await fixture.Create(
                fixture.Owner(109, 20, MutationLeasePackageRole.App))
            .TryAcquireAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(next);
    }

    [Fact]
    public async Task Exact_incomplete_retention_marker_allows_recovery_takeover()
    {
        using var directory = new TemporaryDirectory();
        var fixture = new LeaseFixture(directory.Path, AcquiredUtc);
        var transactionId = Guid.Parse("ff39d746-34d3-4cab-aa8b-514542b98dfa");
        var first = Assert.IsType<GlobalMutationLeaseHandle>(
            await fixture.Create(fixture.Owner(110, 21, MutationLeasePackageRole.App))
                .TryAcquireAsync(transactionId, CancellationToken.None));
        await AppendRetentionApprovedAsync(fixture.Paths, fixture.Clock, transactionId, first);
        await first.DisposeAsync();

        await using var recovered = Assert.IsType<GlobalMutationLeaseHandle>(
            await fixture.Create(fixture.Owner(111, 22, MutationLeasePackageRole.App))
                .TryAcquireRecoveryAsync(transactionId, CancellationToken.None));

        Assert.True(recovered.IsRecoveryTakeover);
        Assert.Equal(2, recovered.Epoch);
        Assert.Equal(transactionId, recovered.OperationId);
    }

    [Fact]
    public async Task Corrupt_operation_history_fails_closed_before_normal_acquire()
    {
        using var directory = new TemporaryDirectory();
        var fixture = new LeaseFixture(directory.Path, AcquiredUtc);
        var operationId = Guid.Parse("3f188638-c5db-40ed-a0fb-9d8c125fc84f");
        await AppendPlannedAsync(fixture.Paths, operationId);
        var transitionPath = Assert.Single(Directory.EnumerateFiles(
            Path.Combine(
                fixture.Paths.GetOperationDirectory(operationId.ToString("N")),
                "Transitions"),
            "*.json"));
        var json = await File.ReadAllTextAsync(transitionPath);
        await File.WriteAllTextAsync(
            transitionPath,
            json.Replace("\"state\":0", "\"state\":1", StringComparison.Ordinal));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Create(fixture.Owner(100, 18, MutationLeasePackageRole.App))
                .TryAcquireAsync(Guid.NewGuid(), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Acquire_persists_random_lease_identity_epoch_and_validated_owner()
    {
        using var directory = new TemporaryDirectory();
        var fixture = new LeaseFixture(directory.Path, AcquiredUtc);
        var owner = fixture.Owner(101, 11, MutationLeasePackageRole.App);
        fixture.Validator.Set(owner, MutationLeaseOwnerStatus.Alive);
        var lease = fixture.Create(owner);
        var operationId = Guid.Parse("eab02c35-fb6f-4cc0-8a1c-1f0fbf69c8de");

        await using var handle = Assert.IsType<GlobalMutationLeaseHandle>(
            await lease.TryAcquireAsync(operationId, CancellationToken.None));

        var record = await ReadRecordAsync(fixture.Paths);
        Assert.NotEqual(Guid.Empty, handle.LeaseId);
        Assert.False(handle.IsRecoveryTakeover);
        Assert.Equal(handle.LeaseId, record.LeaseId);
        Assert.Equal(operationId, record.OperationId);
        Assert.Equal(1, record.Epoch);
        Assert.Equal(1, record.Revision);
        Assert.Equal(AcquiredUtc, record.AcquiredUtc);
        Assert.Equal(AcquiredUtc, record.LastHeartbeatUtc);
        var persistedOwner = Assert.Single(record.Owners);
        Assert.Equal(owner.ProcessId, persistedOwner.ProcessId);
        Assert.Equal(owner.ProcessStartTimeFileTimeUtc, persistedOwner.ProcessStartTimeFileTimeUtc);
        Assert.Equal(owner.UserSid, persistedOwner.UserSid);
        Assert.Equal(MutationLeasePackageRole.App, persistedOwner.PackageRole);
        Assert.Equal(AcquiredUtc, persistedOwner.JoinedUtc);
        Assert.Equal(AcquiredUtc, persistedOwner.LastHeartbeatUtc);
        Assert.Null(record.ReleasedUtc);
    }

    [Fact]
    public async Task Second_instance_is_busy_while_any_validated_owner_is_alive()
    {
        using var directory = new TemporaryDirectory();
        var fixture = new LeaseFixture(directory.Path, AcquiredUtc);
        var firstOwner = fixture.Owner(201, 21, MutationLeasePackageRole.App);
        var secondOwner = fixture.Owner(202, 22, MutationLeasePackageRole.App);
        fixture.Validator.Set(firstOwner, MutationLeaseOwnerStatus.Alive);
        var first = fixture.Create(firstOwner);
        var second = fixture.Create(secondOwner);

        await using var held = Assert.IsType<GlobalMutationLeaseHandle>(
            await first.TryAcquireAsync(Guid.NewGuid(), CancellationToken.None));

        Assert.Null(await second.TryAcquireAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.Equal(1, (await ReadRecordAsync(fixture.Paths)).Revision);
    }

    [Fact]
    public async Task Stale_takeover_advances_epoch_only_after_every_owner_is_proven_dead()
    {
        using var directory = new TemporaryDirectory();
        var fixture = new LeaseFixture(directory.Path, AcquiredUtc);
        var appOwner = fixture.Owner(301, 31, MutationLeasePackageRole.App);
        var helperOwner = fixture.Owner(302, 32, MutationLeasePackageRole.ElevatedHost);
        var replacementOwner = fixture.Owner(303, 33, MutationLeasePackageRole.App);
        fixture.Validator.Set(appOwner, MutationLeaseOwnerStatus.Alive);
        fixture.Validator.Set(helperOwner, MutationLeaseOwnerStatus.Alive);
        var app = fixture.Create(appOwner);
        var helper = fixture.Create(helperOwner);
        var replacement = fixture.Create(replacementOwner);
        var operationId = Guid.NewGuid();
        await using var appHandle = Assert.IsType<GlobalMutationLeaseHandle>(
            await app.TryAcquireAsync(operationId, CancellationToken.None));
        await AppendPlannedAsync(fixture.Paths, operationId);
        await using var helperHandle = Assert.IsType<GlobalMutationLeaseHandle>(
            await helper.TryJoinAsync(
                operationId,
                appHandle.LeaseId,
                appHandle.Epoch,
                CancellationToken.None));

        fixture.Validator.Set(appOwner, MutationLeaseOwnerStatus.ProvenDead);
        fixture.Validator.Set(helperOwner, MutationLeaseOwnerStatus.Unverifiable);
        Assert.Null(await replacement.TryAcquireAsync(Guid.NewGuid(), CancellationToken.None));

        fixture.Validator.Set(helperOwner, MutationLeaseOwnerStatus.ProvenDead);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(await replacement.TryAcquireAsync(Guid.NewGuid(), CancellationToken.None));
        await using var recovered = Assert.IsType<GlobalMutationLeaseHandle>(
            await replacement.TryAcquireRecoveryAsync(operationId, CancellationToken.None));

        Assert.Equal(2, recovered.Epoch);
        Assert.True(recovered.IsRecoveryTakeover);
        Assert.Equal(operationId, recovered.OperationId);
        var record = await ReadRecordAsync(fixture.Paths);
        Assert.Equal(3, record.Revision);
        Assert.Equal(recovered.LeaseId, record.LeaseId);
        Assert.Equal(replacementOwner.ProcessId, Assert.Single(record.Owners).ProcessId);
    }

    [Fact]
    public async Task Abandoned_coordination_mutex_alone_does_not_authorize_takeover()
    {
        using var directory = new TemporaryDirectory();
        var fixture = new LeaseFixture(directory.Path, AcquiredUtc);
        var owner = fixture.Owner(401, 41, MutationLeasePackageRole.App);
        var contender = fixture.Owner(402, 42, MutationLeasePackageRole.App);
        fixture.Validator.Set(owner, MutationLeaseOwnerStatus.Alive);
        var first = fixture.Create(owner);
        await using var held = Assert.IsType<GlobalMutationLeaseHandle>(
            await first.TryAcquireAsync(Guid.NewGuid(), CancellationToken.None));

        AbandonMutexFromAnotherThread(fixture.MutexName);

        Assert.Null(await fixture.Create(contender).TryAcquireAsync(
            Guid.NewGuid(),
            CancellationToken.None));
        Assert.Equal(1, (await ReadRecordAsync(fixture.Paths)).Revision);
    }

    [Fact]
    public async Task Helper_joins_only_the_matching_active_lease_and_keeps_it_busy_after_app_exits()
    {
        using var directory = new TemporaryDirectory();
        var fixture = new LeaseFixture(directory.Path, AcquiredUtc);
        var appOwner = fixture.Owner(501, 51, MutationLeasePackageRole.App);
        var helperOwner = fixture.Owner(502, 52, MutationLeasePackageRole.ElevatedHost);
        var contenderOwner = fixture.Owner(503, 53, MutationLeasePackageRole.App);
        fixture.Validator.Set(appOwner, MutationLeaseOwnerStatus.Alive);
        fixture.Validator.Set(helperOwner, MutationLeaseOwnerStatus.Alive);
        var app = fixture.Create(appOwner);
        var helper = fixture.Create(helperOwner);
        var operationId = Guid.NewGuid();
        await using var appHandle = Assert.IsType<GlobalMutationLeaseHandle>(
            await app.TryAcquireAsync(operationId, CancellationToken.None));

        Assert.Null(await helper.TryJoinAsync(
            operationId,
            Guid.NewGuid(),
            appHandle.Epoch,
            CancellationToken.None));
        var helperHandle = Assert.IsType<GlobalMutationLeaseHandle>(
            await helper.TryJoinAsync(
                operationId,
                appHandle.LeaseId,
                appHandle.Epoch,
                CancellationToken.None));

        await appHandle.DisposeAsync();
        Assert.Equal(MutationLeasePackageRole.ElevatedHost, Assert.Single(
            (await ReadRecordAsync(fixture.Paths)).Owners).PackageRole);
        Assert.Null(await fixture.Create(contenderOwner).TryAcquireAsync(
            Guid.NewGuid(),
            CancellationToken.None));
        Assert.Equal(helperOwner.ProcessId, Assert.Single(
            (await ReadRecordAsync(fixture.Paths)).Owners).ProcessId);

        await helperHandle.DisposeAsync();
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await using var next = Assert.IsType<GlobalMutationLeaseHandle>(
            await fixture.Create(contenderOwner).TryAcquireAsync(
                Guid.NewGuid(),
                CancellationToken.None));
        Assert.Equal(2, next.Epoch);
    }

    [Fact]
    public async Task Duplicate_helper_join_is_busy_instead_of_issuing_two_releasing_handles()
    {
        using var directory = new TemporaryDirectory();
        var fixture = new LeaseFixture(directory.Path, AcquiredUtc);
        var appOwner = fixture.Owner(551, 55, MutationLeasePackageRole.App);
        var helperOwner = fixture.Owner(552, 56, MutationLeasePackageRole.ElevatedHost);
        fixture.Validator.Set(appOwner, MutationLeaseOwnerStatus.Alive);
        fixture.Validator.Set(helperOwner, MutationLeaseOwnerStatus.Alive);
        var operationId = Guid.NewGuid();
        await using var appHandle = Assert.IsType<GlobalMutationLeaseHandle>(
            await fixture.Create(appOwner).TryAcquireAsync(operationId, CancellationToken.None));
        var helper = fixture.Create(helperOwner);
        await using var helperHandle = Assert.IsType<GlobalMutationLeaseHandle>(
            await helper.TryJoinAsync(
                operationId,
                appHandle.LeaseId,
                appHandle.Epoch,
                CancellationToken.None));

        Assert.Null(await helper.TryJoinAsync(
            operationId,
            appHandle.LeaseId,
            appHandle.Epoch,
            CancellationToken.None));
        Assert.Equal(2, (await ReadRecordAsync(fixture.Paths)).Revision);
    }

    [Fact]
    public async Task Heartbeat_and_release_are_durable_and_release_is_idempotent()
    {
        using var directory = new TemporaryDirectory();
        var fixture = new LeaseFixture(directory.Path, AcquiredUtc);
        var owner = fixture.Owner(601, 61, MutationLeasePackageRole.App);
        fixture.Validator.Set(owner, MutationLeaseOwnerStatus.Alive);
        var handle = Assert.IsType<GlobalMutationLeaseHandle>(
            await fixture.Create(owner).TryAcquireAsync(Guid.NewGuid(), CancellationToken.None));

        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(await handle.HeartbeatAsync(CancellationToken.None));
        var heartbeat = await ReadRecordAsync(fixture.Paths);
        Assert.Equal(2, heartbeat.Revision);
        Assert.Equal(fixture.Clock.GetUtcNow(), heartbeat.LastHeartbeatUtc);
        Assert.Equal(fixture.Clock.GetUtcNow(), Assert.Single(heartbeat.Owners).LastHeartbeatUtc);

        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        await handle.DisposeAsync();
        await handle.DisposeAsync();
        var released = await ReadRecordAsync(fixture.Paths);
        Assert.Equal(3, released.Revision);
        Assert.Empty(released.Owners);
        Assert.Equal(fixture.Clock.GetUtcNow(), released.ReleasedUtc);
    }

    [Fact]
    public async Task Active_core_handle_revalidates_exact_durable_membership()
    {
        using var directory = new TemporaryDirectory();
        var fixture = new LeaseFixture(directory.Path, AcquiredUtc);
        var owner = fixture.Owner(621, 62, MutationLeasePackageRole.App);
        IMutationLease lease = fixture.Create(owner);
        await using var handle = Assert.IsAssignableFrom<IMutationLeaseHandle>(
            await lease.TryAcquireAsync(Guid.NewGuid(), CancellationToken.None));

        Assert.True(await handle.RevalidateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Superseded_core_handle_fails_revalidation_without_changing_the_new_lease()
    {
        using var directory = new TemporaryDirectory();
        var fixture = new LeaseFixture(directory.Path, AcquiredUtc);
        var oldOwner = fixture.Owner(631, 63, MutationLeasePackageRole.App);
        var newOwner = fixture.Owner(632, 64, MutationLeasePackageRole.App);
        fixture.Validator.Set(oldOwner, MutationLeaseOwnerStatus.Alive);
        var oldHandle = Assert.IsType<GlobalMutationLeaseHandle>(
            await fixture.Create(oldOwner).TryAcquireAsync(
                Guid.NewGuid(),
                CancellationToken.None));
        fixture.Validator.Set(oldOwner, MutationLeaseOwnerStatus.ProvenDead);
        await using var newHandle = Assert.IsType<GlobalMutationLeaseHandle>(
            await fixture.Create(newOwner).TryAcquireAsync(
                Guid.NewGuid(),
                CancellationToken.None));

        Assert.False(await oldHandle.RevalidateAsync(CancellationToken.None));
        Assert.Equal(newHandle.LeaseId, (await ReadRecordAsync(fixture.Paths)).LeaseId);
        await oldHandle.DisposeAsync();
    }

    [Fact]
    public async Task Recovery_can_rebind_a_released_lease_to_the_same_incomplete_operation()
    {
        using var directory = new TemporaryDirectory();
        var fixture = new LeaseFixture(directory.Path, AcquiredUtc);
        var firstOwner = fixture.Owner(651, 65, MutationLeasePackageRole.App);
        var recoveryOwner = fixture.Owner(652, 66, MutationLeasePackageRole.App);
        var operationId = Guid.NewGuid();
        var first = Assert.IsType<GlobalMutationLeaseHandle>(
            await fixture.Create(firstOwner).TryAcquireAsync(operationId, CancellationToken.None));
        await AppendPlannedAsync(fixture.Paths, operationId);
        await first.DisposeAsync();

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await using var recovered = Assert.IsType<GlobalMutationLeaseHandle>(
            await fixture.Create(recoveryOwner).TryAcquireRecoveryAsync(
                operationId,
                CancellationToken.None));

        Assert.True(recovered.IsRecoveryTakeover);
        Assert.Equal(2, recovered.Epoch);
        Assert.Equal(operationId, recovered.OperationId);
    }

    [Fact]
    public void Coordination_mutex_has_exact_protected_current_user_and_system_acl()
    {
        using var directory = new TemporaryDirectory();
        var fixture = new LeaseFixture(directory.Path, AcquiredUtc);
        _ = fixture.Create(fixture.Owner(701, 71, MutationLeasePackageRole.App));
        using var opened = MutexAcl.OpenExisting(fixture.MutexName, MutexRights.ReadPermissions);

        var security = opened.GetAccessControl();
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<MutexAccessRule>()
            .ToArray();
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var userSid = identity.User!;
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(2, rules.Length);
        Assert.All(rules, rule =>
        {
            Assert.False(rule.IsInherited);
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.Equal(MutexRights.FullControl, rule.MutexRights);
        });
        Assert.Contains(rules, rule => userSid.Equals(rule.IdentityReference));
        Assert.Contains(rules, rule => systemSid.Equals(rule.IdentityReference));
    }

    [Fact]
    public void Preexisting_coordination_mutex_with_broader_acl_is_rejected()
    {
        using var directory = new TemporaryDirectory();
        var fixture = new LeaseFixture(directory.Path, AcquiredUtc);
        var permissive = new MutexSecurity();
        permissive.AddAccessRule(new MutexAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            MutexRights.FullControl,
            AccessControlType.Allow));
        using var adversarial = MutexAcl.Create(false, fixture.MutexName, out _, permissive);

        Assert.Throws<InvalidOperationException>(() =>
            fixture.Create(fixture.Owner(702, 72, MutationLeasePackageRole.App)));
    }

    [Fact]
    public async Task Real_second_process_is_busy_and_recovery_takes_over_only_after_owner_exit()
    {
        using var directory = new TemporaryDirectory();
        var root = directory.File("lease-root");
        var mutexName = $"Global\\Winora.Mutation.ProcessTests.{Guid.NewGuid():N}";
        var operationId = Guid.NewGuid();
        var heldReady = directory.File("held-ready");
        var unusedRelease = directory.File("unused-release");
        using var holder = StartLeaseHost(
            "hold",
            root,
            mutexName,
            operationId,
            heldReady,
            unusedRelease);
        try
        {
            await WaitForFileAsync(heldReady);
            await AppendPlannedAsync(new WinoraDataPaths(root), operationId);

            using (var contender = StartLeaseHost(
                       "busy",
                       root,
                       mutexName,
                       Guid.NewGuid(),
                       directory.File("busy-ready"),
                       unusedRelease))
            {
                await contender.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(0, contender.ExitCode);
            }

            holder.Kill(entireProcessTree: true);
            await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            if (!holder.HasExited)
            {
                holder.Kill(entireProcessTree: true);
                await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        var recoveryReady = directory.File("recovery-ready");
        using var recovery = StartLeaseHost(
            "recover",
            root,
            mutexName,
            operationId,
            recoveryReady,
            unusedRelease);
        await recovery.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, recovery.ExitCode);
        Assert.Equal("2", await File.ReadAllTextAsync(recoveryReady));
    }

    [Fact]
    public async Task Real_second_process_makes_apply_and_rollback_busy_before_any_journal_or_system_write()
    {
        using var directory = new TemporaryDirectory();
        var root = directory.File("coordinator-lease-root");
        var paths = new WinoraDataPaths(root);
        var mutexName = $"Global\\Winora.Mutation.CoordinatorProcessTests.{Guid.NewGuid():N}";
        var readyPath = directory.File("coordinator-holder-ready");
        var releasePath = directory.File("coordinator-holder-release");
        using var holder = StartLeaseHost(
            "hold",
            root,
            mutexName,
            Guid.NewGuid(),
            readyPath,
            releasePath);
        try
        {
            await WaitForFileAsync(readyPath);
            var identity = new RealProcessLeaseIdentity();
            using var lease = new GlobalMutationLease(
                paths,
                MutationLeasePackageRole.App,
                identity,
                identity,
                TimeProvider.System,
                mutexName);
            var journal = new NoWriteDurableOperationJournal();
            var backups = new NoWriteBackupRepository();
            var operation = new NoWriteOperation("test.operation");
            var confirmations = new ConfirmationAuthority();
            var coordinator = new ChangeCoordinator(
                journal,
                backups,
                lease,
                new FixedLeaseClock(),
                confirmations);
            var plan = TestPlan.Create(Guid.NewGuid());
            var rollback = RollbackPlan.Create(
                Guid.NewGuid(),
                plan,
                BackupReceipt.Verified(
                    "backup",
                    "BACKUP-DIGEST",
                    plan.Digest,
                    plan.SourceFingerprint,
                    plan.SourceFingerprint),
                plan.Steps[^1].ResultFingerprint);

            var applyResult = await coordinator.ApplyAsync(
                operation,
                plan,
                confirmations.Confirm(plan),
                CancellationToken.None);
            var rollbackResult = await coordinator.RollbackAsync(
                operation,
                rollback,
                confirmations.Confirm(rollback),
                CancellationToken.None);

            Assert.Equal(CoordinatorDisposition.OperationBusy, applyResult.Disposition);
            Assert.Equal(CoordinatorDisposition.OperationBusy, rollbackResult.Disposition);
            Assert.Equal(0, journal.CallCount);
            Assert.Equal(0, backups.CallCount);
            Assert.Equal(0, operation.CallCount);
        }
        finally
        {
            await File.WriteAllTextAsync(releasePath, "release");
            await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(0, holder.ExitCode);
    }

    [Fact]
    public async Task Real_helper_owner_keeps_the_lease_busy_after_the_app_process_exits()
    {
        using var directory = new TemporaryDirectory();
        var root = directory.File("helper-lease-root");
        var mutexName = $"Global\\Winora.Mutation.HelperProcessTests.{Guid.NewGuid():N}";
        var operationId = Guid.NewGuid();
        var appReady = directory.File("app-ready");
        var helperReady = directory.File("helper-ready");
        var helperRelease = directory.File("helper-release");
        var unusedRelease = directory.File("unused-release");
        using var app = StartLeaseHost(
            "hold",
            root,
            mutexName,
            operationId,
            appReady,
            unusedRelease);
        Process? helper = null;
        try
        {
            await WaitForFileAsync(appReady);
            helper = StartLeaseHost(
                "helper-hold",
                root,
                mutexName,
                operationId,
                helperReady,
                helperRelease);
            await WaitForFileAsync(helperReady);

            app.Kill(entireProcessTree: true);
            await app.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            using (var contender = StartLeaseHost(
                       "busy",
                       root,
                       mutexName,
                       Guid.NewGuid(),
                       directory.File("helper-busy-ready"),
                       unusedRelease))
            {
                await contender.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(0, contender.ExitCode);
            }

            await File.WriteAllTextAsync(helperRelease, "release");
            await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, helper.ExitCode);

            var acquiredReady = directory.File("acquired-ready");
            using var replacement = StartLeaseHost(
                "acquire",
                root,
                mutexName,
                Guid.NewGuid(),
                acquiredReady,
                unusedRelease);
            await replacement.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, replacement.ExitCode);
            Assert.Equal("2", await File.ReadAllTextAsync(acquiredReady));
        }
        finally
        {
            if (!app.HasExited)
            {
                app.Kill(entireProcessTree: true);
                await app.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }

            if (helper is { HasExited: false })
            {
                helper.Kill(entireProcessTree: true);
                await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }

            helper?.Dispose();
        }
    }

    private static async Task<MutationLeaseRecord> ReadRecordAsync(WinoraDataPaths paths)
    {
        var result = await new AtomicJsonFile(
                paths,
                (JsonDocumentSerializer?)null,
                TimeProvider.System)
            .ReadProjectionAsync<MutationLeaseRecord>(
            paths.MutationLeaseDocument,
            CancellationToken.None);
        Assert.Equal(ProjectionReadSource.Primary, result.Source);
        return result.Document.Payload;
    }

    private static async Task AppendPlannedAsync(
        WinoraDataPaths paths,
        Guid operationId)
    {
        var plan = TestPlan.Create(operationId);
        var transition = OperationTransition.Create(
            operationId,
            DurableOperationFacts.From(plan),
            expectedRevision: 0,
            expectedState: null,
            OperationState.Planned,
            stepId: null,
            AcquiredUtc);
        var result = await new DurableOperationJournal(paths, DurableJournalActor.App)
            .CompareAndAppendAsync(transition, CancellationToken.None);
        Assert.True(result.IsDurable);
    }

    private static async Task AppendTerminalAsync(
        WinoraDataPaths paths,
        Guid operationId)
    {
        var plan = TestPlan.Create(operationId);
        var facts = DurableOperationFacts.From(plan);
        var journal = new DurableOperationJournal(paths, DurableJournalActor.App);
        var planned = OperationTransition.Create(
            operationId,
            facts,
            expectedRevision: 0,
            expectedState: null,
            OperationState.Planned,
            stepId: null,
            AcquiredUtc);
        Assert.True((await journal.CompareAndAppendAsync(
            planned,
            CancellationToken.None)).IsDurable);
        var terminal = OperationTransition.Create(
            operationId,
            facts,
            expectedRevision: 1,
            expectedState: OperationState.Planned,
            OperationState.PlanInvalidatedNoChanges,
            stepId: null,
            AcquiredUtc.AddSeconds(1),
            previousFacts: facts);
        Assert.True((await journal.CompareAndAppendAsync(
            terminal,
            CancellationToken.None)).IsDurable);
    }

    private static async Task AppendRetentionApprovedAsync(
        WinoraDataPaths paths,
        TimeProvider timeProvider,
        Guid transactionId,
        IMutationLeaseHandle lease)
    {
        var request = new ActionJournalRetentionRequest(
            completedOperationId: null,
            linkedChangeOperationIds: new HashSet<Guid>(),
            maximumAge: TimeSpan.FromDays(365),
            maximumEventCount: 25_000);
        var boundary = await new DurableRetentionJournal(paths, timeProvider)
            .CreateApprovedAsync(
                transactionId,
                lease,
                request,
                RetentionArtifactSelection.Empty,
                CancellationToken.None);
        Assert.Equal(RetentionLifecycleState.Approved, boundary.State);
    }

    private static void AbandonMutexFromAnotherThread(string mutexName)
    {
        using var acquired = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            using var mutex = MutexAcl.OpenExisting(
                mutexName,
                MutexRights.Synchronize | MutexRights.Modify);
            mutex.WaitOne();
            acquired.Set();
        });
        thread.Start();
        Assert.True(acquired.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }

    private static Process StartLeaseHost(
        string mode,
        string root,
        string mutexName,
        Guid operationId,
        string readyPath,
        string releasePath)
    {
        var testAssembly = typeof(GlobalMutationLeaseTests).Assembly.Location;
        var startInfo = new ProcessStartInfo("dotnet")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("--runtimeconfig");
        startInfo.ArgumentList.Add(Path.ChangeExtension(testAssembly, ".runtimeconfig.json"));
        startInfo.ArgumentList.Add("--depsfile");
        startInfo.ArgumentList.Add(Path.ChangeExtension(testAssembly, ".deps.json"));
        startInfo.ArgumentList.Add(typeof(LeaseProcessHostMarker).Assembly.Location);
        startInfo.ArgumentList.Add(mode);
        startInfo.ArgumentList.Add(root);
        startInfo.ArgumentList.Add(mutexName);
        startInfo.ArgumentList.Add(operationId.ToString("D"));
        startInfo.ArgumentList.Add(readyPath);
        startInfo.ArgumentList.Add(releasePath);
        return Process.Start(startInfo) ??
            throw new InvalidOperationException("The mutation lease process host could not start.");
    }

    private static async Task WaitForFileAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(path))
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private sealed class LeaseFixture
    {
        internal LeaseFixture(string root, DateTimeOffset now)
        {
            Paths = new WinoraDataPaths(root);
            Clock = new AdjustableLeaseTimeProvider(now);
            MutexName = $"Global\\Winora.Mutation.Tests.{Guid.NewGuid():N}";
        }

        internal WinoraDataPaths Paths { get; }

        internal AdjustableLeaseTimeProvider Clock { get; }

        internal FakeLeaseOwnerValidator Validator { get; } = new();

        internal string MutexName { get; }

        internal MutationLeaseOwnerIdentity Owner(
            int processId,
            long startTime,
            MutationLeasePackageRole role) =>
            new(processId, startTime, CurrentUserSid(), role);

        internal GlobalMutationLease Create(MutationLeaseOwnerIdentity owner) =>
            new(
                Paths,
                owner.PackageRole,
                new FakeLeaseOwnerIdentityProvider(owner),
                Validator,
                Clock,
                MutexName);

        private static string CurrentUserSid()
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            return identity.User!.Value;
        }
    }
}

internal sealed class FakeLeaseOwnerIdentityProvider(MutationLeaseOwnerIdentity owner) :
    IMutationLeaseOwnerIdentityProvider
{
    public MutationLeaseOwnerIdentity GetCurrent(MutationLeasePackageRole packageRole)
    {
        Assert.Equal(owner.PackageRole, packageRole);
        return owner;
    }
}

internal sealed class FakeLeaseOwnerValidator : IMutationLeaseOwnerValidator
{
    private readonly Dictionary<(int ProcessId, long StartTime), MutationLeaseOwnerStatus> _statuses = [];

    internal void Set(MutationLeaseOwnerIdentity owner, MutationLeaseOwnerStatus status) =>
        _statuses[(owner.ProcessId, owner.ProcessStartTimeFileTimeUtc)] = status;

    public MutationLeaseOwnerStatus Validate(MutationLeaseOwner owner) =>
        _statuses.GetValueOrDefault(
            (owner.ProcessId, owner.ProcessStartTimeFileTimeUtc),
            MutationLeaseOwnerStatus.Unverifiable);
}

internal sealed class AdjustableLeaseTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    internal void Advance(TimeSpan amount) => _utcNow += amount;
}

internal sealed class RealProcessLeaseIdentity :
    IMutationLeaseOwnerIdentityProvider,
    IMutationLeaseOwnerValidator
{
    private readonly string _userSid;

    internal RealProcessLeaseIdentity()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        _userSid = identity.User?.Value ??
            throw new InvalidOperationException("The test process does not have a user SID.");
    }

    public MutationLeaseOwnerIdentity GetCurrent(MutationLeasePackageRole packageRole)
    {
        using var process = Process.GetCurrentProcess();
        return new MutationLeaseOwnerIdentity(
            Environment.ProcessId,
            process.StartTime.ToUniversalTime().ToFileTimeUtc(),
            _userSid,
            packageRole);
    }

    public MutationLeaseOwnerStatus Validate(MutationLeaseOwner owner)
    {
        if (!StringComparer.Ordinal.Equals(owner.UserSid, _userSid) ||
            !Enum.IsDefined(owner.PackageRole))
        {
            return MutationLeaseOwnerStatus.Unverifiable;
        }

        try
        {
            using var process = Process.GetProcessById(owner.ProcessId);
            return process.StartTime.ToUniversalTime().ToFileTimeUtc() ==
                owner.ProcessStartTimeFileTimeUtc
                ? MutationLeaseOwnerStatus.Alive
                : MutationLeaseOwnerStatus.ProvenDead;
        }
        catch (ArgumentException)
        {
            return MutationLeaseOwnerStatus.ProvenDead;
        }
        catch (InvalidOperationException)
        {
            return MutationLeaseOwnerStatus.ProvenDead;
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return MutationLeaseOwnerStatus.Unverifiable;
        }
    }
}

internal sealed class NoWriteDurableOperationJournal : IDurableOperationJournal
{
    internal int CallCount { get; private set; }

    public ValueTask<DurableOperationBoundary?> ReadVerifiedBoundaryAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        CallCount++;
        throw new InvalidOperationException("The busy coordinator must not read the operation journal.");
    }

    public ValueTask<IReadOnlyList<DurableOperationBoundary>> ScanIncompleteAsync(
        CancellationToken cancellationToken)
    {
        CallCount++;
        throw new InvalidOperationException("The busy coordinator must not scan the operation journal.");
    }

    public ValueTask<DurableTransitionResult> CompareAndAppendAsync(
        OperationTransition transition,
        CancellationToken cancellationToken)
    {
        CallCount++;
        throw new InvalidOperationException("The busy coordinator must not append a transition.");
    }
}

internal sealed class NoWriteBackupRepository : IBackupRepository
{
    internal int CallCount { get; private set; }

    public ValueTask<BackupReceipt> ReadAndVerifyOperationBackupAsync(
        ChangePlan plan,
        string backupId,
        string backupDigest,
        CancellationToken cancellationToken) => Fail();

    public ValueTask<BackupReceipt> ReadAndVerifyAsync(
        RollbackPlan plan,
        CancellationToken cancellationToken) => Fail();

    public ValueTask<BackupReceipt> ReadAndVerifyRecoveryCheckpointAsync(
        RollbackPlan plan,
        string checkpointId,
        string checkpointDigest,
        CancellationToken cancellationToken) => Fail();

    public ValueTask<BackupReceipt> CreateAndVerifyAsync(
        ChangePlan plan,
        CancellationToken cancellationToken) => Fail();

    public ValueTask<BackupReceipt> CreateRecoveryCheckpointAsync(
        RollbackPlan plan,
        CancellationToken cancellationToken) => Fail();

    private ValueTask<BackupReceipt> Fail()
    {
        CallCount++;
        throw new InvalidOperationException("The busy coordinator must not access backups.");
    }
}

internal sealed class NoWriteOperation(string operationId) :
    IOperation,
    IConditionalSystemMutation
{
    public string OperationId { get; } = operationId;

    public string ConditionalMutationMechanismId => "test.atomic";

    internal int CallCount { get; private set; }

    public ValueTask<OperationCapability> ProbeAsync(
        OperationTarget target,
        CancellationToken cancellationToken) => Fail<OperationCapability>();

    public ValueTask<ChangePlan> PreviewAsync(
        OperationDraft draft,
        CancellationToken cancellationToken) => Fail<ChangePlan>();

    public ValueTask<StepResult> ApplyStepAsync(
        ChangePlan plan,
        ChangeStep step,
        CancellationToken cancellationToken) => Fail<StepResult>();

    public ValueTask<VerificationResult> VerifyStepAsync(
        ChangePlan plan,
        ChangeStep step,
        CancellationToken cancellationToken) => Fail<VerificationResult>();

    public ValueTask<StepResult> RollbackStepAsync(
        RollbackPlan plan,
        ChangeStep step,
        CancellationToken cancellationToken) => Fail<StepResult>();

    private ValueTask<T> Fail<T>()
    {
        CallCount++;
        throw new InvalidOperationException("The busy coordinator must not inspect or mutate the system.");
    }
}

internal sealed class FixedLeaseClock : IClock
{
    public DateTimeOffset UtcNow =>
        new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
}
