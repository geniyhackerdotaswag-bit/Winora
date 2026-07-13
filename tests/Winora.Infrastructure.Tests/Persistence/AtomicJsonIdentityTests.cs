using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Winora.Infrastructure.Paths;
using Winora.Infrastructure.Persistence;
using Xunit;

namespace Winora.Infrastructure.Tests.Persistence;

public sealed class AtomicJsonIdentityTests
{
    private static readonly DateTimeOffset CreatedUtc =
        new(2026, 7, 13, 8, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Projection_read_rejects_a_junction_ancestor()
    {
        using var root = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        var paths = new WinoraDataPaths(root.Path);
        var serializer = new JsonDocumentSerializer();
        await File.WriteAllBytesAsync(
            Path.Combine(outside.Path, "change-index.json"),
            serializer.Serialize(serializer.CreateEnvelope(
                "change-index",
                CreatedUtc,
                new ValuePayload(7))));
        CreateJunction(paths.DataDirectory, outside.Path);

        try
        {
            await Assert.ThrowsAnyAsync<IOException>(() =>
                CreateFile(paths).ReadProjectionAsync<ValuePayload>(
                    paths.ChangeIndexDocument,
                    CancellationToken.None).AsTask());
        }
        finally
        {
            Directory.Delete(paths.DataDirectory);
        }
    }

    [Fact]
    public async Task Authoritative_read_rejects_a_hard_link_alias()
    {
        using var root = new TemporaryDirectory();
        var paths = new WinoraDataPaths(root.Path);
        var file = CreateFile(paths);
        var destination = paths.GetJournalEventDocument("event-id");
        await file.CreateNewAsync(destination, new ValuePayload(1), CancellationToken.None);
        CreateHardLinkOrThrow(Path.Combine(root.Path, "alias.json"), destination.FilePath);

        await Assert.ThrowsAnyAsync<IOException>(() =>
            file.ReadAuthoritativeAsync<ValuePayload>(destination, CancellationToken.None).AsTask());
    }

    [Fact]
    public void Prepared_staging_file_cannot_be_replaced_before_publication()
    {
        using var root = new TemporaryDirectory();
        var paths = new WinoraDataPaths(root.Path);
        var file = CreateFile(paths);
        using var prepared = file.PrepareAuthoritative(
            paths.GetJournalEventDocument("event-id"),
            new ValuePayload(1),
            CancellationToken.None);

        Assert.Throws<IOException>(() =>
            File.Move(prepared.TemporaryPath, prepared.TemporaryPath + ".swapped"));
    }

    [Fact]
    public async Task Validated_read_keeps_the_same_leaf_handle_until_bytes_are_consumed()
    {
        using var root = new TemporaryDirectory();
        var paths = new WinoraDataPaths(root.Path);
        var destination = paths.GetJournalEventDocument("event-id");
        await CreateFile(paths).CreateNewAsync(
            destination,
            new ValuePayload(1),
            CancellationToken.None);
        var observer = new SwapAttemptObserver();
        var reader = new AtomicJsonFile(
            paths,
            serializer: new JsonDocumentSerializer(),
            timeProvider: new FixedTimeProvider(CreatedUtc),
            validatedFileObserver: observer);

        var result = await reader.ReadAuthoritativeAsync<ValuePayload>(
            destination,
            CancellationToken.None);

        Assert.Equal(1, result.Payload.Value);
        Assert.True(observer.SwapWasBlocked);
    }

    [Fact]
    public async Task Validated_read_blocks_a_hard_link_alias_created_after_identity_validation()
    {
        using var root = new TemporaryDirectory();
        var paths = new WinoraDataPaths(root.Path);
        var destination = paths.GetJournalEventDocument("event-id");
        await CreateFile(paths).CreateNewAsync(
            destination,
            new ValuePayload(1),
            CancellationToken.None);
        var observer = new HardLinkAttemptObserver();
        var reader = new AtomicJsonFile(
            paths,
            serializer: new JsonDocumentSerializer(),
            timeProvider: new FixedTimeProvider(CreatedUtc),
            validatedFileObserver: observer);

        await Assert.ThrowsAnyAsync<IOException>(() =>
            reader.ReadAuthoritativeAsync<ValuePayload>(
                destination,
                CancellationToken.None).AsTask());

        Assert.True(observer.HardLinkWasCreated);
        Assert.True(File.Exists(destination.FilePath + ".alias"));
    }

    [Theory]
    [InlineData(PublicationSwapTarget.Staging)]
    [InlineData(PublicationSwapTarget.Final)]
    [InlineData(PublicationSwapTarget.Backup)]
    public async Task Publication_rejects_identity_swap_after_initial_preflight(
        PublicationSwapTarget swapTarget)
    {
        using var root = new TemporaryDirectory();
        var paths = new WinoraDataPaths(root.Path);
        var baseline = CreateFile(paths);
        await baseline.WriteProjectionAsync(
            paths.ChangeIndexDocument,
            new ValuePayload(1),
            CancellationToken.None);
        await baseline.WriteProjectionAsync(
            paths.ChangeIndexDocument,
            new ValuePayload(2),
            CancellationToken.None);
        var file = new AtomicJsonFile(
            paths,
            serializer: new JsonDocumentSerializer(),
            timeProvider: new FixedTimeProvider(CreatedUtc),
            publicationRaceHook: new SwapPublicationIdentityHook(swapTarget));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            file.WriteProjectionAsync(
                paths.ChangeIndexDocument,
                new ValuePayload(3),
                CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Publication_rejects_a_staging_hard_link_created_after_initial_preflight()
    {
        using var root = new TemporaryDirectory();
        var paths = new WinoraDataPaths(root.Path);
        var file = new AtomicJsonFile(
            paths,
            serializer: new JsonDocumentSerializer(),
            timeProvider: new FixedTimeProvider(CreatedUtc),
            publicationRaceHook: new StagingHardLinkPublicationHook());
        var destination = paths.GetJournalEventDocument("event-id");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            file.CreateNewAsync(
                destination,
                new ValuePayload(1),
                CancellationToken.None).AsTask());

        Assert.False(File.Exists(destination.FilePath));
    }

    [Fact]
    public async Task Temp_delete_failure_releases_lease_and_preserves_primary_failure_first()
    {
        using var root = new TemporaryDirectory();
        var paths = new WinoraDataPaths(root.Path);
        var file = new AtomicJsonFile(
            paths,
            publisher: new ThrowBeforePublishPublisher(),
            serializer: new JsonDocumentSerializer(),
            timeProvider: new FixedTimeProvider(CreatedUtc),
            cleanup: new ThrowingAtomicFileCleanup());

        var failure = await Assert.ThrowsAsync<AggregateException>(() =>
            file.CreateNewAsync(
                paths.GetJournalEventDocument("event-id"),
                new ValuePayload(1),
                CancellationToken.None).AsTask());

        Assert.IsType<InjectedStorageException>(failure.InnerExceptions[0]);
        Assert.IsType<InjectedCleanupException>(failure.InnerExceptions[1]);
        Directory.Move(paths.JournalDirectory, paths.JournalDirectory + "-moved");
    }

    [Fact]
    public void Preparation_failure_and_temp_delete_failure_release_the_lease_and_preserve_order()
    {
        using var root = new TemporaryDirectory();
        var paths = new WinoraDataPaths(root.Path);
        var file = new AtomicJsonFile(
            paths,
            serializer: new JsonDocumentSerializer(),
            timeProvider: new FixedTimeProvider(CreatedUtc),
            validatedFileObserver: new CorruptValidatedReadObserver(
                ValidatedFileUse.StagingReadback),
            cleanup: new ThrowingAtomicFileCleanup());

        var failure = Assert.Throws<AggregateException>(() =>
            file.PrepareAuthoritative(
                paths.GetJournalEventDocument("event-id"),
                new ValuePayload(1),
                CancellationToken.None));

        Assert.IsType<InvalidDataException>(failure.InnerExceptions[0]);
        Assert.IsType<InjectedCleanupException>(failure.InnerExceptions[1]);
        Directory.Move(paths.JournalDirectory, paths.JournalDirectory + "-moved");
    }

    private static AtomicJsonFile CreateFile(WinoraDataPaths paths) =>
        new(
            paths,
            serializer: new JsonDocumentSerializer(),
            timeProvider: new FixedTimeProvider(CreatedUtc));

    private static void CreateJunction(string linkPath, string targetPath)
    {
        using var process = Process.Start(new ProcessStartInfo(
            "cmd.exe",
            $"/d /c mklink /J \"{linkPath}\" \"{targetPath}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("Unable to start junction helper.");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(process.StandardError.ReadToEnd());
        }
    }

    private static void CreateHardLinkOrThrow(string newPath, string existingPath)
    {
        if (!HardLinkNativeMethods.TryCreate(newPath, existingPath))
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException(
                $"Unable to create test hard link (Win32 error {error}).",
                new Win32Exception(error));
        }
    }

}

public enum PublicationSwapTarget
{
    Staging,
    Final,
    Backup,
}

internal sealed class SwapAttemptObserver : IValidatedFileObserver
{
    internal bool SwapWasBlocked { get; private set; }

    public void OnValidated(
        string path,
        ValidatedFileIdentity identity,
        ValidatedFileUse use)
    {
        if (use != ValidatedFileUse.PublicRead)
        {
            return;
        }

        try
        {
            File.Move(path, path + ".swapped");
            File.Move(path + ".swapped", path);
        }
        catch (IOException)
        {
            SwapWasBlocked = true;
        }
    }
}

internal sealed class HardLinkAttemptObserver : IValidatedFileObserver
{
    internal bool HardLinkWasCreated { get; private set; }

    public void OnValidated(
        string path,
        ValidatedFileIdentity identity,
        ValidatedFileUse use)
    {
        if (use != ValidatedFileUse.PublicRead)
        {
            return;
        }

        HardLinkWasCreated = HardLinkNativeMethods.TryCreate(
            path + ".alias",
            path);
    }
}

internal sealed class SwapPublicationIdentityHook(
    PublicationSwapTarget target) : IAtomicPublicationRaceHook
{
    public void AfterInitialIdentityValidation(AtomicPublicationContext context)
    {
        var path = target switch
        {
            PublicationSwapTarget.Staging => context.TemporaryPath,
            PublicationSwapTarget.Final => context.FinalPath,
            PublicationSwapTarget.Backup => context.BackupPath!,
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };
        var moved = path + ".race-original";
        File.Move(path, moved);
        File.Copy(moved, path);
    }
}

internal sealed class StagingHardLinkPublicationHook : IAtomicPublicationRaceHook
{
    public void AfterInitialIdentityValidation(AtomicPublicationContext context)
    {
        if (!HardLinkNativeMethods.TryCreate(
                context.TemporaryPath + ".alias",
                context.TemporaryPath))
        {
            throw new IOException("Unable to create a staging hard link for the race test.");
        }
    }
}

internal static class HardLinkNativeMethods
{
    internal static bool TryCreate(string newPath, string existingPath) =>
        CreateHardLink(newPath, existingPath, IntPtr.Zero);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateHardLinkW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);
}

internal sealed class ThrowingAtomicFileCleanup : IAtomicFileCleanup
{
    public void Delete(string path) => throw new InjectedCleanupException();
}

internal sealed class InjectedCleanupException : IOException;
