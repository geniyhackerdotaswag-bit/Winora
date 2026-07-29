using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Winora.Core.Contracts;
using Winora.Infrastructure.Journal;
using Winora.Infrastructure.Operations;
using Winora.Infrastructure.Paths;
using Winora.Infrastructure.Persistence;

namespace Winora.Infrastructure.Leases;

public enum MutationLeasePackageRole
{
    App = 0,
    ElevatedHost = 1,
}

public sealed class GlobalMutationLease : IMutationLease, IDisposable
{
    private const int CurrentRecordSchemaVersion = 1;

    private readonly WinoraDataPaths _paths;
    private readonly MutationLeasePackageRole _packageRole;
    private readonly IMutationLeaseOwnerIdentityProvider _identityProvider;
    private readonly IMutationLeaseOwnerValidator _ownerValidator;
    private readonly TimeProvider _timeProvider;
    private readonly AtomicJsonFile _storage;
    private readonly DurableOperationJournal _operationJournal;
    private readonly DurableRetentionJournal _retentionJournal;
    private readonly GlobalPersistenceMutex _coordinationMutex;
    private int _disposed;

    public GlobalMutationLease(
        WinoraDataPaths paths,
        MutationLeasePackageRole packageRole = MutationLeasePackageRole.App)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        ValidateRole(packageRole);
        _packageRole = packageRole;
        var windowsIdentity = new WindowsMutationLeaseOwnerIdentity(packageRole);
        _identityProvider = windowsIdentity;
        _ownerValidator = windowsIdentity;
        _timeProvider = TimeProvider.System;
        _storage = new AtomicJsonFile(_paths, (JsonDocumentSerializer?)null, _timeProvider);
        _operationJournal = CreateOperationJournal(_paths, _packageRole, _timeProvider);
        _retentionJournal = new DurableRetentionJournal(_paths, _timeProvider);
        _coordinationMutex = new GlobalPersistenceMutex(
            GetCoordinationMutexName(windowsIdentity.CurrentUserSid));
    }

    internal GlobalMutationLease(
        WinoraDataPaths paths,
        MutationLeasePackageRole packageRole,
        IMutationLeaseOwnerIdentityProvider identityProvider,
        IMutationLeaseOwnerValidator ownerValidator,
        TimeProvider timeProvider,
        string coordinationMutexName)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        ValidateRole(packageRole);
        _packageRole = packageRole;
        _identityProvider = identityProvider ?? throw new ArgumentNullException(nameof(identityProvider));
        _ownerValidator = ownerValidator ?? throw new ArgumentNullException(nameof(ownerValidator));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _storage = new AtomicJsonFile(_paths, (JsonDocumentSerializer?)null, _timeProvider);
        _operationJournal = CreateOperationJournal(_paths, _packageRole, _timeProvider);
        _retentionJournal = new DurableRetentionJournal(_paths, _timeProvider);
        _coordinationMutex = new GlobalPersistenceMutex(coordinationMutexName);
    }

    public ValueTask<GlobalMutationLeaseHandle?> TryAcquireAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentOutOfRangeException.ThrowIfEqual(operationId, Guid.Empty);
        if (_packageRole != MutationLeasePackageRole.App)
        {
            throw new InvalidOperationException(
                "The elevated host must join an authenticated active lease and cannot acquire one independently.");
        }

        return ExecuteCoordinatedAsync(
            () => TryAcquireCore(operationId, cancellationToken),
            cancellationToken);
    }

    public ValueTask<GlobalMutationLeaseHandle?> TryJoinAsync(
        Guid operationId,
        Guid leaseId,
        long epoch,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentOutOfRangeException.ThrowIfEqual(operationId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(leaseId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(epoch);
        if (_packageRole != MutationLeasePackageRole.ElevatedHost)
        {
            throw new InvalidOperationException("Only the elevated host can join an active mutation lease.");
        }

        return ExecuteCoordinatedAsync(
            () => TryJoinCore(operationId, leaseId, epoch, cancellationToken),
            cancellationToken);
    }

    public ValueTask<GlobalMutationLeaseHandle?> TryAcquireRecoveryAsync(
        Guid incompleteOperationId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentOutOfRangeException.ThrowIfEqual(incompleteOperationId, Guid.Empty);
        if (_packageRole != MutationLeasePackageRole.App)
        {
            throw new InvalidOperationException("Only the App can take recovery ownership of an incomplete operation.");
        }

        return ExecuteCoordinatedAsync(
            () => TryAcquireRecoveryCore(incompleteOperationId, cancellationToken),
            cancellationToken);
    }

    async ValueTask<IMutationLeaseHandle?> IMutationLease.TryAcquireAsync(
        Guid operationId,
        CancellationToken cancellationToken) =>
        await TryAcquireAsync(operationId, cancellationToken).ConfigureAwait(false);

    async ValueTask<IMutationLeaseHandle?> IMutationLease.TryAcquireRecoveryAsync(
        Guid incompleteOperationId,
        CancellationToken cancellationToken) =>
        await TryAcquireRecoveryAsync(incompleteOperationId, cancellationToken).ConfigureAwait(false);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _coordinationMutex.Dispose();
        }
    }

    internal static string GetCoordinationMutexName(string userSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        var sidHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userSid)));
        return $"Global\\Winora.Mutation.{sidHash}";
    }

    private GlobalMutationLeaseHandle? TryAcquireCore(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = ReadCurrentOrNull(cancellationToken);
        if (current is not null)
        {
            ValidateRecord(current);
            if (current.Owners.Any(owner =>
                    _ownerValidator.Validate(owner) != MutationLeaseOwnerStatus.ProvenDead))
            {
                return null;
            }
        }

        if (ReadVerifiedIncompleteBindingIds(cancellationToken).Count != 0)
        {
            return null;
        }

        var identity = CaptureCurrentIdentity();
        var now = GetUtcNow();
        var record = new MutationLeaseRecord(
            CurrentRecordSchemaVersion,
            Guid.NewGuid(),
            operationId,
            checked((current?.Epoch ?? 0) + 1),
            checked((current?.Revision ?? 0) + 1),
            now,
            now,
            [MutationLeaseOwner.From(identity, now)],
            null,
            false);
        Persist(record, cancellationToken);
        return CreateHandle(record, identity);
    }

    private static DurableOperationJournal CreateOperationJournal(
        WinoraDataPaths paths,
        MutationLeasePackageRole packageRole,
        TimeProvider timeProvider) =>
        new(
            paths,
            packageRole == MutationLeasePackageRole.App
                ? DurableJournalActor.App
                : DurableJournalActor.ElevatedHost,
            timeProvider);

    private IReadOnlyList<Guid> ReadVerifiedIncompleteBindingIds(
        CancellationToken cancellationToken)
    {
        var operationIds = _operationJournal.ScanIncompleteAsync(cancellationToken)
            .AsTask().GetAwaiter().GetResult()
            .Select(boundary => boundary.OperationId);
        var retentionIds = _retentionJournal.ScanIncompleteAsync(cancellationToken)
            .AsTask().GetAwaiter().GetResult()
            .Select(boundary => boundary.Intent.TransactionId);
        return Array.AsReadOnly(operationIds
            .Concat(retentionIds)
            .Order()
            .ToArray());
    }

    private GlobalMutationLeaseHandle? TryAcquireRecoveryCore(
        Guid incompleteOperationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = ReadCurrentOrNull(cancellationToken);
        if (current is null)
        {
            return null;
        }

        ValidateRecord(current);
        if (current.Owners.Any(owner =>
                _ownerValidator.Validate(owner) != MutationLeaseOwnerStatus.ProvenDead))
        {
            return null;
        }

        var incomplete = ReadVerifiedIncompleteBindingIds(cancellationToken);
        if (incomplete.Count != 1 || incomplete[0] != incompleteOperationId)
        {
            return null;
        }

        var identity = CaptureCurrentIdentity();
        var now = GetUtcNow();
        var recovered = new MutationLeaseRecord(
            CurrentRecordSchemaVersion,
            Guid.NewGuid(),
            incompleteOperationId,
            checked(current.Epoch + 1),
            checked(current.Revision + 1),
            now,
            now,
            [MutationLeaseOwner.From(identity, now)],
            null,
            true);
        Persist(recovered, cancellationToken);
        return CreateHandle(recovered, identity);
    }

    private GlobalMutationLeaseHandle? TryJoinCore(
        Guid operationId,
        Guid leaseId,
        long epoch,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = ReadCurrentOrNull(cancellationToken);
        if (current is null)
        {
            return null;
        }

        ValidateRecord(current);
        if (current.ReleasedUtc is not null ||
            current.OperationId != operationId ||
            current.LeaseId != leaseId ||
            current.Epoch != epoch)
        {
            return null;
        }

        var identity = CaptureCurrentIdentity();
        var survivors = new List<MutationLeaseOwner>(current.Owners.Count + 1);
        var hasLiveApp = false;
        foreach (var owner in current.Owners)
        {
            var status = _ownerValidator.Validate(owner);
            if (status == MutationLeaseOwnerStatus.Unverifiable)
            {
                return null;
            }

            if (status == MutationLeaseOwnerStatus.ProvenDead)
            {
                continue;
            }

            if (owner.PackageRole == MutationLeasePackageRole.App)
            {
                hasLiveApp = true;
            }

            if (owner.PackageRole == MutationLeasePackageRole.ElevatedHost &&
                !owner.Matches(identity))
            {
                return null;
            }

            survivors.Add(owner);
        }

        if (!hasLiveApp)
        {
            return null;
        }

        var existing = survivors.SingleOrDefault(owner => owner.Matches(identity));
        if (existing is not null)
        {
            return null;
        }

        var now = GetUtcNow();
        survivors.Add(MutationLeaseOwner.From(identity, now));
        var joined = current with
        {
            Revision = checked(current.Revision + 1),
            LastHeartbeatUtc = now,
            Owners = survivors,
        };
        Persist(joined, cancellationToken);
        return CreateHandle(joined, identity);
    }

    private async ValueTask<T> ExecuteCoordinatedAsync<T>(
        Func<T> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        return await Task.Run(
            () => _coordinationMutex.Execute(action, cancellationToken),
            CancellationToken.None).ConfigureAwait(false);
    }

    private MutationLeaseRecord? ReadCurrentOrNull(CancellationToken cancellationToken)
    {
        try
        {
            var read = _storage.ReadProjectionAsync<MutationLeaseRecord>(
                    _paths.MutationLeaseDocument,
                    cancellationToken)
                .AsTask().GetAwaiter().GetResult();
            if (read.Source != ProjectionReadSource.Primary)
            {
                throw new InvalidDataException(
                    "The active mutation lease primary record is unavailable; stale fallback metadata cannot authorize mutation.");
            }

            return read.Document.Payload;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private void Persist(MutationLeaseRecord record, CancellationToken cancellationToken)
    {
        ValidateRecord(record);
        _storage.WriteProjectionAsync(
                _paths.MutationLeaseDocument,
                record,
                cancellationToken)
            .AsTask().GetAwaiter().GetResult();
    }

    private MutationLeaseOwnerIdentity CaptureCurrentIdentity()
    {
        var identity = _identityProvider.GetCurrent(_packageRole);
        ValidateIdentity(identity, _packageRole);
        return identity;
    }

    private GlobalMutationLeaseHandle CreateHandle(
        MutationLeaseRecord record,
        MutationLeaseOwnerIdentity owner) =>
        new(
            this,
            record.LeaseId,
            record.OperationId,
            record.Epoch,
            owner,
            record.IsRecoveryTakeover);

    internal ValueTask<bool> HeartbeatAsync(
        Guid leaseId,
        Guid operationId,
        long epoch,
        MutationLeaseOwnerIdentity identity,
        CancellationToken cancellationToken) =>
        ExecuteCoordinatedAsync(
            () => HeartbeatCore(leaseId, operationId, epoch, identity, cancellationToken),
            cancellationToken);

    internal ValueTask<bool> RevalidateAsync(
        Guid leaseId,
        Guid operationId,
        long epoch,
        MutationLeaseOwnerIdentity identity,
        CancellationToken cancellationToken) =>
        ExecuteCoordinatedAsync(
            () => RevalidateCore(leaseId, operationId, epoch, identity, cancellationToken),
            cancellationToken);

    internal async ValueTask ReleaseAsync(
        Guid leaseId,
        Guid operationId,
        long epoch,
        MutationLeaseOwnerIdentity identity) =>
        _ = await ExecuteCoordinatedAsync(
            () =>
            {
                ReleaseCore(leaseId, operationId, epoch, identity);
                return true;
            },
            CancellationToken.None).ConfigureAwait(false);

    private bool HeartbeatCore(
        Guid leaseId,
        Guid operationId,
        long epoch,
        MutationLeaseOwnerIdentity identity,
        CancellationToken cancellationToken)
    {
        var current = ReadCurrentOrNull(cancellationToken);
        if (!MatchesLease(current, leaseId, operationId, epoch))
        {
            return false;
        }

        ValidateRecord(current!);
        var ownerIndex = -1;
        for (var index = 0; index < current!.Owners.Count; index++)
        {
            if (current.Owners[index].Matches(identity))
            {
                ownerIndex = index;
                break;
            }
        }
        if (ownerIndex < 0)
        {
            return false;
        }

        var now = GetUtcNow();
        var owners = current.Owners.ToList();
        owners[ownerIndex] = owners[ownerIndex] with { LastHeartbeatUtc = now };
        var heartbeat = current with
        {
            Revision = checked(current.Revision + 1),
            LastHeartbeatUtc = now,
            Owners = owners,
        };
        Persist(heartbeat, cancellationToken);
        return true;
    }

    private bool RevalidateCore(
        Guid leaseId,
        Guid operationId,
        long epoch,
        MutationLeaseOwnerIdentity identity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = ReadCurrentOrNull(cancellationToken);
        if (!MatchesLease(current, leaseId, operationId, epoch))
        {
            return false;
        }

        ValidateRecord(current!);
        var currentIdentity = CaptureCurrentIdentity();
        return currentIdentity == identity &&
            current!.Owners.Any(owner => owner.Matches(identity));
    }

    private void ReleaseCore(
        Guid leaseId,
        Guid operationId,
        long epoch,
        MutationLeaseOwnerIdentity identity)
    {
        var current = ReadCurrentOrNull(CancellationToken.None);
        if (!MatchesLease(current, leaseId, operationId, epoch))
        {
            return;
        }

        ValidateRecord(current!);
        var owners = current!.Owners.Where(owner => !owner.Matches(identity)).ToList();
        if (owners.Count == current.Owners.Count)
        {
            return;
        }

        var now = GetUtcNow();
        var released = current with
        {
            Revision = checked(current.Revision + 1),
            LastHeartbeatUtc = now,
            Owners = owners,
            ReleasedUtc = owners.Count == 0 ? now : null,
        };
        Persist(released, CancellationToken.None);
    }

    private static bool MatchesLease(
        MutationLeaseRecord? record,
        Guid leaseId,
        Guid operationId,
        long epoch) =>
        record is not null &&
        record.LeaseId == leaseId &&
        record.OperationId == operationId &&
        record.Epoch == epoch &&
        record.ReleasedUtc is null;

    private DateTimeOffset GetUtcNow()
    {
        var now = _timeProvider.GetUtcNow();
        if (now == default || now.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("The mutation lease clock must return a non-default UTC timestamp.");
        }

        return now;
    }

    private static void ValidateRecord(MutationLeaseRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.SchemaVersion != CurrentRecordSchemaVersion ||
            record.LeaseId == Guid.Empty ||
            record.OperationId == Guid.Empty ||
            record.Epoch <= 0 ||
            record.Revision <= 0 ||
            !IsUtc(record.AcquiredUtc) ||
            !IsUtc(record.LastHeartbeatUtc) ||
            record.LastHeartbeatUtc < record.AcquiredUtc ||
            (record.ReleasedUtc is { } releasedUtc &&
                (!IsUtc(releasedUtc) || releasedUtc < record.LastHeartbeatUtc)) ||
            record.Owners is null ||
            record.Owners.Count > 2 ||
            (record.Owners.Count == 0) != (record.ReleasedUtc is not null))
        {
            throw new InvalidDataException("The durable mutation lease record is invalid.");
        }

        var ownerKeys = new HashSet<(int, long, MutationLeasePackageRole)>();
        var roles = new HashSet<MutationLeasePackageRole>();
        foreach (var owner in record.Owners)
        {
            ValidateOwner(owner, record.AcquiredUtc, record.LastHeartbeatUtc);
            if (!ownerKeys.Add((owner.ProcessId, owner.ProcessStartTimeFileTimeUtc, owner.PackageRole)) ||
                !roles.Add(owner.PackageRole))
            {
                throw new InvalidDataException("The durable mutation lease contains duplicate owners or package roles.");
            }
        }

    }

    private static void ValidateOwner(
        MutationLeaseOwner owner,
        DateTimeOffset acquiredUtc,
        DateTimeOffset recordHeartbeatUtc)
    {
        ArgumentNullException.ThrowIfNull(owner);
        try
        {
            _ = new SecurityIdentifier(owner.UserSid);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("A mutation lease owner SID is invalid.", exception);
        }

        if (owner.ProcessId <= 0 ||
            owner.ProcessStartTimeFileTimeUtc <= 0 ||
            !Enum.IsDefined(owner.PackageRole) ||
            !IsUtc(owner.JoinedUtc) ||
            !IsUtc(owner.LastHeartbeatUtc) ||
            owner.JoinedUtc < acquiredUtc ||
            owner.LastHeartbeatUtc < owner.JoinedUtc ||
            owner.LastHeartbeatUtc > recordHeartbeatUtc)
        {
            throw new InvalidDataException("A durable mutation lease owner is invalid.");
        }
    }

    private static void ValidateIdentity(
        MutationLeaseOwnerIdentity identity,
        MutationLeasePackageRole expectedRole)
    {
        ArgumentNullException.ThrowIfNull(identity);
        try
        {
            _ = new SecurityIdentifier(identity.UserSid);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("The current mutation lease owner SID is invalid.", exception);
        }

        if (identity.ProcessId <= 0 ||
            identity.ProcessStartTimeFileTimeUtc <= 0 ||
            identity.PackageRole != expectedRole)
        {
            throw new InvalidOperationException("The current mutation lease owner identity is invalid.");
        }
    }

    private static void ValidateRole(MutationLeasePackageRole role)
    {
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }
    }

    private static bool IsUtc(DateTimeOffset value) =>
        value != default && value.Offset == TimeSpan.Zero;
}

public sealed class GlobalMutationLeaseHandle : IMutationLeaseHandle
{
    private readonly GlobalMutationLease _owner;
    private readonly MutationLeaseOwnerIdentity _identity;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private bool _disposed;

    internal GlobalMutationLeaseHandle(
        GlobalMutationLease owner,
        Guid leaseId,
        Guid operationId,
        long epoch,
        MutationLeaseOwnerIdentity identity,
        bool isRecoveryTakeover)
    {
        _owner = owner;
        LeaseId = leaseId;
        OperationId = operationId;
        Epoch = epoch;
        IsRecoveryTakeover = isRecoveryTakeover;
        _identity = identity;
    }

    public Guid LeaseId { get; }

    public Guid OperationId { get; }

    public long Epoch { get; }

    public bool IsRecoveryTakeover { get; }

    public async ValueTask<bool> HeartbeatAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return false;
            }

            return await _owner.HeartbeatAsync(
                LeaseId,
                OperationId,
                Epoch,
                _identity,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask<bool> RevalidateAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return false;
            }

            return await _owner.RevalidateAsync(
                LeaseId,
                OperationId,
                Epoch,
                _identity,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            await _owner.ReleaseAsync(LeaseId, OperationId, Epoch, _identity).ConfigureAwait(false);
            _disposed = true;
        }
        finally
        {
            _lifecycle.Release();
        }
    }
}

internal sealed record MutationLeaseRecord(
    int SchemaVersion,
    Guid LeaseId,
    Guid OperationId,
    long Epoch,
    long Revision,
    DateTimeOffset AcquiredUtc,
    DateTimeOffset LastHeartbeatUtc,
    IReadOnlyList<MutationLeaseOwner> Owners,
    DateTimeOffset? ReleasedUtc,
    bool IsRecoveryTakeover);

internal sealed record MutationLeaseOwner(
    int ProcessId,
    long ProcessStartTimeFileTimeUtc,
    string UserSid,
    MutationLeasePackageRole PackageRole,
    DateTimeOffset JoinedUtc,
    DateTimeOffset LastHeartbeatUtc)
{
    internal static MutationLeaseOwner From(
        MutationLeaseOwnerIdentity identity,
        DateTimeOffset now) =>
        new(
            identity.ProcessId,
            identity.ProcessStartTimeFileTimeUtc,
            identity.UserSid,
            identity.PackageRole,
            now,
            now);

    internal bool Matches(MutationLeaseOwnerIdentity identity) =>
        ProcessId == identity.ProcessId &&
        ProcessStartTimeFileTimeUtc == identity.ProcessStartTimeFileTimeUtc &&
        StringComparer.Ordinal.Equals(UserSid, identity.UserSid) &&
        PackageRole == identity.PackageRole;
}

internal sealed record MutationLeaseOwnerIdentity(
    int ProcessId,
    long ProcessStartTimeFileTimeUtc,
    string UserSid,
    MutationLeasePackageRole PackageRole);

internal enum MutationLeaseOwnerStatus
{
    Alive = 0,
    ProvenDead = 1,
    Unverifiable = 2,
}

internal interface IMutationLeaseOwnerIdentityProvider
{
    MutationLeaseOwnerIdentity GetCurrent(MutationLeasePackageRole packageRole);
}

internal interface IMutationLeaseOwnerValidator
{
    MutationLeaseOwnerStatus Validate(MutationLeaseOwner owner);
}

internal sealed class WindowsMutationLeaseOwnerIdentity :
    IMutationLeaseOwnerIdentityProvider,
    IMutationLeaseOwnerValidator
{
    private readonly MutationLeasePackageRole _currentRole;
    private readonly string _currentUserSid;
    private readonly string _packageFullName;
    private readonly string _packageFamilyName;

    internal WindowsMutationLeaseOwnerIdentity(MutationLeasePackageRole currentRole)
    {
        _currentRole = currentRole;
        var current = WindowsLeaseProcessInspector.Inspect(Environment.ProcessId);
        ValidatePackageRole(current, currentRole);
        _currentUserSid = current.UserSid;
        _packageFullName = current.PackageFullName;
        _packageFamilyName = current.PackageFamilyName;
    }

    internal string CurrentUserSid => _currentUserSid;

    public MutationLeaseOwnerIdentity GetCurrent(MutationLeasePackageRole packageRole)
    {
        if (packageRole != _currentRole)
        {
            throw new InvalidOperationException("The requested lease owner role does not match this process.");
        }

        var current = WindowsLeaseProcessInspector.Inspect(Environment.ProcessId);
        if (!StringComparer.Ordinal.Equals(current.UserSid, _currentUserSid) ||
            !StringComparer.Ordinal.Equals(current.PackageFullName, _packageFullName) ||
            !StringComparer.Ordinal.Equals(current.PackageFamilyName, _packageFamilyName))
        {
            throw new InvalidOperationException("The current process package or user identity changed unexpectedly.");
        }

        ValidatePackageRole(current, packageRole);
        return new MutationLeaseOwnerIdentity(
            Environment.ProcessId,
            current.ProcessStartTimeFileTimeUtc,
            current.UserSid,
            packageRole);
    }

    public MutationLeaseOwnerStatus Validate(MutationLeaseOwner owner)
    {
        LeaseProcessSnapshot candidate;
        try
        {
            candidate = WindowsLeaseProcessInspector.Inspect(owner.ProcessId);
        }
        catch (ProcessNotFoundException)
        {
            return MutationLeaseOwnerStatus.ProvenDead;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            return MutationLeaseOwnerStatus.Unverifiable;
        }

        if (candidate.ProcessStartTimeFileTimeUtc != owner.ProcessStartTimeFileTimeUtc)
        {
            return MutationLeaseOwnerStatus.ProvenDead;
        }

        if (!StringComparer.Ordinal.Equals(candidate.UserSid, owner.UserSid) ||
            !StringComparer.Ordinal.Equals(candidate.UserSid, _currentUserSid) ||
            !StringComparer.Ordinal.Equals(candidate.PackageFullName, _packageFullName) ||
            !StringComparer.Ordinal.Equals(candidate.PackageFamilyName, _packageFamilyName))
        {
            return MutationLeaseOwnerStatus.Unverifiable;
        }

        try
        {
            ValidatePackageRole(candidate, owner.PackageRole);
        }
        catch (InvalidOperationException)
        {
            return MutationLeaseOwnerStatus.Unverifiable;
        }

        return MutationLeaseOwnerStatus.Alive;
    }

    private static void ValidatePackageRole(
        LeaseProcessSnapshot process,
        MutationLeasePackageRole role)
    {
        // Package full/family identities are supplied by Windows only for a registered,
        // signed package. Binding the fixed executable leaf to that package's installed
        // root prevents an arbitrary same-user executable from claiming an App/helper role.
        var expectedFileName = role switch
        {
            MutationLeasePackageRole.App => "Winora.App.exe",
            MutationLeasePackageRole.ElevatedHost => "Winora.ElevatedHost.exe",
            _ => throw new InvalidOperationException("The mutation lease package role is unknown."),
        };
        if (!StringComparer.OrdinalIgnoreCase.Equals(
                Path.GetFileName(process.ImagePath),
                expectedFileName) ||
            !WindowsLeaseProcessInspector.IsUnderPackageRoot(
                process.ImagePath,
                process.PackageInstallPath))
        {
            throw new InvalidOperationException(
                "The process executable does not match its signed package role and install root.");
        }
    }
}

internal sealed record LeaseProcessSnapshot(
    long ProcessStartTimeFileTimeUtc,
    string UserSid,
    string ImagePath,
    string PackageFullName,
    string PackageFamilyName,
    string PackageInstallPath);

internal sealed class ProcessNotFoundException : Exception;

internal static partial class WindowsLeaseProcessInspector
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;
    private const uint TokenQuery = 0x0008;

    internal static LeaseProcessSnapshot Inspect(int processId)
    {
        // Microsoft Learn: https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-openprocess
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (process.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == ErrorInvalidParameter)
            {
                throw new ProcessNotFoundException();
            }

            throw new Win32Exception(error, "The lease owner process could not be opened for identity validation.");
        }

        // Microsoft Learn: https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocesstimes
        if (!GetProcessTimes(process, out var creation, out _, out _, out _))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "The lease owner process creation time could not be read.");
        }

        var imagePath = GetImagePath(process);
        var userSid = GetUserSid(process);
        var packageFullName = GetPackageFullNameValue(process);
        var packageFamilyName = GetPackageFamilyNameValue(process);
        var packageInstallPath = GetPackageInstallPath(packageFullName);
        return new LeaseProcessSnapshot(
            creation.ToLong(),
            userSid,
            imagePath,
            packageFullName,
            packageFamilyName,
            packageInstallPath);
    }

    internal static bool IsUnderPackageRoot(string imagePath, string packageRoot)
    {
        var image = Path.GetFullPath(imagePath);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageRoot));
        var relative = Path.GetRelativePath(root, image);
        return relative != "." &&
            !Path.IsPathRooted(relative) &&
            !relative.Equals("..", StringComparison.Ordinal) &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
            !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static unsafe string GetImagePath(SafeProcessHandle process)
    {
        var capacity = 32_768u;
        var buffer = stackalloc char[(int)capacity];
        // Microsoft Learn: https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-queryfullprocessimagenamew
        if (!QueryFullProcessImageName(process, 0, buffer, ref capacity))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "The lease owner executable path could not be validated.");
        }

        return new string(buffer, 0, (int)capacity);
    }

    private static string GetUserSid(SafeProcessHandle process)
    {
        // Microsoft Learn: https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-openprocesstoken
        if (!OpenProcessToken(process, TokenQuery, out var token))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "The lease owner process token could not be opened.");
        }

        using (token)
        {
            _ = GetTokenInformation(token, 1, IntPtr.Zero, 0, out var required);
            var error = Marshal.GetLastPInvokeError();
            if (required == 0 || error != ErrorInsufficientBuffer)
            {
                throw new Win32Exception(error, "The lease owner token SID size could not be read.");
            }

            var buffer = Marshal.AllocHGlobal((int)required);
            try
            {
                // Microsoft Learn: https://learn.microsoft.com/windows/win32/api/securitybaseapi/nf-securitybaseapi-gettokeninformation
                if (!GetTokenInformation(token, 1, buffer, required, out _))
                {
                    throw new Win32Exception(
                        Marshal.GetLastPInvokeError(),
                        "The lease owner token SID could not be read.");
                }

                var tokenUser = Marshal.PtrToStructure<TokenUser>(buffer);
                return new SecurityIdentifier(tokenUser.User.Sid).Value;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private static unsafe string GetPackageFullNameValue(SafeProcessHandle process)
    {
        // Microsoft Learn: https://learn.microsoft.com/windows/win32/api/appmodel/nf-appmodel-getpackagefullname
        var length = 0u;
        var result = GetPackageFullName(process, ref length, null);
        if (result == AppModelErrorNoPackage)
        {
            throw new InvalidOperationException("The mutation lease owner is not running from the signed Winora package.");
        }

        if (result != ErrorInsufficientBuffer || length == 0)
        {
            throw new Win32Exception(result, "The lease owner package full name size could not be read.");
        }

        var buffer = stackalloc char[(int)length];
        result = GetPackageFullName(process, ref length, buffer);
        if (result != 0)
        {
            throw new Win32Exception(result, "The lease owner package full name could not be read.");
        }

        return new string(buffer, 0, checked((int)length - 1));
    }

    private static unsafe string GetPackageFamilyNameValue(SafeProcessHandle process)
    {
        // Microsoft Learn: https://learn.microsoft.com/windows/win32/api/appmodel/nf-appmodel-getpackagefamilyname
        var length = 0u;
        var result = GetPackageFamilyName(process, ref length, null);
        if (result != ErrorInsufficientBuffer || length == 0)
        {
            throw new Win32Exception(result, "The lease owner package family name size could not be read.");
        }

        var buffer = stackalloc char[(int)length];
        result = GetPackageFamilyName(process, ref length, buffer);
        if (result != 0)
        {
            throw new Win32Exception(result, "The lease owner package family name could not be read.");
        }

        return new string(buffer, 0, checked((int)length - 1));
    }

    private static unsafe string GetPackageInstallPath(string packageFullName)
    {
        // Microsoft Learn: https://learn.microsoft.com/windows/win32/api/appmodel/nf-appmodel-getpackagepathbyfullname
        var length = 0u;
        var result = GetPackagePathByFullName(packageFullName, ref length, null);
        if (result != ErrorInsufficientBuffer || length == 0)
        {
            throw new Win32Exception(result, "The Winora package install path size could not be read.");
        }

        var buffer = stackalloc char[(int)length];
        result = GetPackagePathByFullName(packageFullName, ref length, buffer);
        if (result != 0)
        {
            throw new Win32Exception(result, "The Winora package install path could not be read.");
        }

        return new string(buffer, 0, checked((int)length - 1));
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeFileTime
    {
        private readonly uint _low;
        private readonly uint _high;

        internal long ToLong() => ((long)_high << 32) | _low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct SidAndAttributes
    {
        internal readonly IntPtr Sid;
        private readonly uint _attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct TokenUser
    {
        internal readonly SidAndAttributes User;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(
        SafeProcessHandle process,
        out NativeFileTime creationTime,
        out NativeFileTime exitTime,
        out NativeFileTime kernelTime,
        out NativeFileTime userTime);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(
        SafeProcessHandle process,
        uint flags,
        char* executableName,
        ref uint size);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(
        SafeProcessHandle process,
        uint desiredAccess,
        out SafeAccessTokenHandle token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(
        SafeAccessTokenHandle token,
        int tokenInformationClass,
        IntPtr tokenInformation,
        uint tokenInformationLength,
        out uint returnLength);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial int GetPackageFullName(
        SafeProcessHandle process,
        ref uint packageFullNameLength,
        char* packageFullName);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial int GetPackageFamilyName(
        SafeProcessHandle process,
        ref uint packageFamilyNameLength,
        char* packageFamilyName);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int GetPackagePathByFullName(
        string packageFullName,
        ref uint pathLength,
        char* path);
}
