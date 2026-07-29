using System.Text;
using Winora.Core.Changes;
using Winora.Core.Contracts;
using Winora.Infrastructure.Backups;
using Winora.Infrastructure.Operations;
using Winora.Infrastructure.Paths;
using Winora.Infrastructure.Persistence;

namespace Winora.Infrastructure.ProcessHost;

public static class ProcessHostMarker;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length != 4)
        {
            return 64;
        }

        var mode = args[0];
        if (StringComparer.Ordinal.Equals(mode, "journal-append"))
        {
            return RunJournalAppend(args[1], args[2], args[3]);
        }

        if (StringComparer.Ordinal.Equals(
                mode,
                "state-restore-crash-after-publication"))
        {
            return RunStateRestoreCrashAfterPublication(args[1], args[2]);
        }

        if (StringComparer.Ordinal.Equals(
                mode,
                "state-restore-crash-after-first-rename"))
        {
            return RunStateRestoreCrashAfterFirstRename(args[1], args[2]);
        }

        var mutexName = args[1];
        var readyPath = args[2];
        var releasePath = args[3];
        using var mutex = new GlobalPersistenceMutex(mutexName);
        return mutex.Execute(
            () =>
            {
                File.WriteAllText(readyPath, "ready");
                if (StringComparer.Ordinal.Equals(mode, "abandon"))
                {
                    Environment.Exit(0);
                }

                while (!File.Exists(releasePath))
                {
                    Thread.Sleep(10);
                }

                return 0;
            },
            CancellationToken.None);
    }

    private static int RunJournalAppend(
        string root,
        string readyPath,
        string releasePath)
    {
        File.WriteAllText(readyPath, "ready");
        while (!File.Exists(releasePath))
        {
            Thread.Sleep(10);
        }

        var plan = CreateJournalPlan();
        var transition = OperationTransition.Create(
            plan.PlanId,
            DurableOperationFacts.From(plan),
            0,
            null,
            OperationState.Planned,
            null,
            new DateTimeOffset(2026, 7, 13, 10, 0, 0, TimeSpan.Zero));
        var result = new DurableOperationJournal(
                new WinoraDataPaths(root),
                DurableJournalActor.App)
            .CompareAndAppendAsync(transition, CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        return result.IsDurable ? 0 : 2;
    }

    private static int RunStateRestoreCrashAfterPublication(
        string root,
        string readyPath)
    {
        var paths = new WinoraDataPaths(root);
        var restorer = new WinoraStateRestorer(
            paths,
            publicationRaceHook: new ExitAfterStatePublicationHook(readyPath));
        restorer.Restore(
            [
                BackupArtifact.Create(
                    "data/app-settings.json",
                    "winora-state-file",
                    Encoding.UTF8.GetBytes("backup-from-crashed-process")),
            ],
            CancellationToken.None);
        return 70;
    }

    private static int RunStateRestoreCrashAfterFirstRename(
        string root,
        string readyPath)
    {
        var paths = new WinoraDataPaths(root);
        var restorer = new WinoraStateRestorer(
            paths,
            fileOperations: new ExitAfterFirstRenameOperations(readyPath));
        restorer.Restore(
            [
                BackupArtifact.Create(
                    "data/app-settings.json",
                    "winora-state-file",
                    Encoding.UTF8.GetBytes("backup-from-crashed-process")),
            ],
            CancellationToken.None);
        return 71;
    }

    private static ChangePlan CreateJournalPlan() =>
        ChangePlan.Create(
            Guid.Parse("5f739542-2a1b-4194-8e89-19dd9e79af9d"),
            "test.operation",
            "Test",
            "Test change",
            "A deterministic persistence test plan.",
            [
                CreateStep("step-1", "source-1", "result-1"),
                CreateStep("step-2", "source-2", "result-2"),
            ],
            RiskLevel.Low,
            PrivilegeRequirement.StandardUser,
            RollbackCapability.Full,
            RestartRequirement.None,
            SupportStatus.Supported,
            Fingerprint("source-plan"),
            new Uri("https://learn.microsoft.com/windows/"),
            BackupRequirement.Required,
            requiresRestorePoint: false);

    private static ChangeStep CreateStep(string id, string source, string result) =>
        new(
            id,
            new OperationTarget($"target-{id}"),
            new DisplayValue("text", source),
            new DisplayValue("text", result),
            Fingerprint(source),
            Fingerprint(result),
            new VerificationProbe($"verify-{id}", result));

    private static StateFingerprint Fingerprint(string value) =>
        new("SHA-256", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value))));
}

internal sealed class ExitAfterStatePublicationHook(string readyPath) :
    IWinoraStateRestoreRaceHook
{
    public void AfterInitialTargetValidation(WinoraStateRestorePublicationContext context)
    {
    }

    public void AfterPublicationBeforeJournal(WinoraStateRestorePublicationContext context)
    {
        File.WriteAllText(readyPath, "published");
        Environment.Exit(86);
    }
}

internal sealed class ExitAfterFirstRenameOperations(string readyPath) :
    IAtomicFileOperations
{
    private readonly WindowsAtomicFileOperations _inner = new();
    private int _renameCount;

    public void RenameNoReplace(
        ValidatedFileHandle sourceFile,
        string destinationPath)
    {
        _inner.RenameNoReplace(sourceFile, destinationPath);
        if (Interlocked.Increment(ref _renameCount) == 1)
        {
            File.WriteAllText(readyPath, "first-rename-durable");
            Environment.Exit(87);
        }
    }

    public void Delete(ValidatedFileHandle file) => _inner.Delete(file);
}
