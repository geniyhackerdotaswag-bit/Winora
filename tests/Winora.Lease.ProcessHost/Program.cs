using System.Diagnostics;
using System.Security.Principal;
using Winora.Infrastructure.Leases;
using Winora.Infrastructure.Paths;
using Winora.Infrastructure.Persistence;

namespace Winora.Lease.ProcessHost;

public static class LeaseProcessHostMarker;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length != 6 || !Guid.TryParse(args[3], out var operationId))
        {
            return 64;
        }

        var mode = args[0];
        var root = args[1];
        var mutexName = args[2];
        var readyPath = args[4];
        var releasePath = args[5];
        var identity = new TestProcessLeaseIdentity();
        var packageRole = StringComparer.Ordinal.Equals(mode, "helper-hold")
            ? MutationLeasePackageRole.ElevatedHost
            : MutationLeasePackageRole.App;
        var paths = new WinoraDataPaths(root);
        using var lease = new GlobalMutationLease(
            paths,
            packageRole,
            identity,
            identity,
            TimeProvider.System,
            mutexName);

        if (StringComparer.Ordinal.Equals(mode, "hold"))
        {
            return Hold(lease, operationId, readyPath, releasePath);
        }

        if (StringComparer.Ordinal.Equals(mode, "busy"))
        {
            return lease.TryAcquireAsync(operationId, CancellationToken.None)
                .AsTask().GetAwaiter().GetResult() is null ? 0 : 2;
        }

        if (StringComparer.Ordinal.Equals(mode, "recover"))
        {
            var recovered = lease.TryAcquireRecoveryAsync(operationId, CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            if (recovered is null)
            {
                return 3;
            }

            File.WriteAllText(readyPath, recovered.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture));
            recovered.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return recovered.Epoch == 2 && recovered.IsRecoveryTakeover ? 0 : 4;
        }

        if (StringComparer.Ordinal.Equals(mode, "helper-hold"))
        {
            var read = new AtomicJsonFile(
                    paths,
                    (JsonDocumentSerializer?)null,
                    TimeProvider.System)
                .ReadProjectionAsync<MutationLeaseRecord>(
                    paths.MutationLeaseDocument,
                    CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            if (read.Source != ProjectionReadSource.Primary)
            {
                return 5;
            }

            var record = read.Document.Payload;
            var joined = lease.TryJoinAsync(
                    operationId,
                    record.LeaseId,
                    record.Epoch,
                    CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            if (joined is null)
            {
                return 6;
            }

            File.WriteAllText(readyPath, joined.Epoch.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
            while (!File.Exists(releasePath))
            {
                Thread.Sleep(10);
            }

            joined.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return 0;
        }

        if (StringComparer.Ordinal.Equals(mode, "acquire"))
        {
            var acquired = lease.TryAcquireAsync(operationId, CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            if (acquired is null)
            {
                return 7;
            }

            File.WriteAllText(readyPath, acquired.Epoch.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
            acquired.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return 0;
        }

        return 64;
    }

    private static int Hold(
        GlobalMutationLease lease,
        Guid operationId,
        string readyPath,
        string releasePath)
    {
        var handle = lease.TryAcquireAsync(operationId, CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        if (handle is null)
        {
            return 2;
        }

        File.WriteAllText(readyPath, handle.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture));
        while (!File.Exists(releasePath))
        {
            Thread.Sleep(10);
        }

        handle.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return 0;
    }
}

internal sealed class TestProcessLeaseIdentity :
    IMutationLeaseOwnerIdentityProvider,
    IMutationLeaseOwnerValidator
{
    private readonly string _userSid;

    internal TestProcessLeaseIdentity()
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
