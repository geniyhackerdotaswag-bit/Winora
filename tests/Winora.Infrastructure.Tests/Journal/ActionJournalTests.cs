using System.Security.Cryptography;
using Winora.Core.Contracts;
using Winora.Core.Journal;
using Winora.Infrastructure.Journal;
using Winora.Infrastructure.Paths;
using Winora.Infrastructure.Persistence;
using Xunit;
using TestContext = Winora.Infrastructure.Tests.Journal.JournalTestContext;

namespace Winora.Infrastructure.Tests.Journal;

public sealed class ActionJournalTests
{
    private static readonly DateTimeOffset CreatedUtc =
        new(2026, 7, 14, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Append_publishes_one_immutable_sanitized_event_and_rebuildable_index()
    {
        using var fixture = JournalFixture.Create(CreatedUtc);
        var draft = fixture.CreateDraft(
            status: ActionJournalStatus.Succeeded,
            targetCorrelationHash: new string('A', 64));

        var appended = await fixture.Journal.AppendAsync(draft, TestContext.Current.CancellationToken);

        Assert.Equal(CreatedUtc, appended.TimestampUtc);
        Assert.Equal(draft.OperationId, appended.OperationId);
        Assert.Equal(draft.CatalogOperationId, appended.CatalogOperationId);
        Assert.Equal(draft.CorrelationId, appended.CorrelationId);
        Assert.Matches("^[0-9a-f]{32}$", appended.EventId);

        var eventFiles = Directory.GetFiles(
            fixture.Paths.JournalEventsDirectory,
            "*.json",
            SearchOption.TopDirectoryOnly);
        Assert.Single(eventFiles);
        Assert.Equal($"{appended.EventId}.json", Path.GetFileName(eventFiles[0]));

        var persisted = await File.ReadAllTextAsync(
            eventFiles[0],
            TestContext.Current.CancellationToken);
        Assert.Contains("\"operationId\"", persisted, StringComparison.Ordinal);
        Assert.Contains("\"catalogOperationId\"", persisted, StringComparison.Ordinal);
        Assert.Contains("\"category\"", persisted, StringComparison.Ordinal);
        Assert.Contains("\"status\"", persisted, StringComparison.Ordinal);
        Assert.Contains("\"risk\"", persisted, StringComparison.Ordinal);
        Assert.Contains("\"privilege\"", persisted, StringComparison.Ordinal);
        Assert.Contains("\"supportStatus\"", persisted, StringComparison.Ordinal);
        Assert.Contains("\"correlationId\"", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("exception", persisted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("commandLine", persisted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("environment", persisted, StringComparison.OrdinalIgnoreCase);

        var index = await fixture.Journal.RebuildIndexAsync(TestContext.Current.CancellationToken);
        Assert.Equal([appended], index.Events);
        Assert.True(File.Exists(fixture.Paths.JournalIndexFile));
    }

    [Fact]
    public async Task Corrupt_or_missing_index_is_rebuilt_from_verified_immutable_events()
    {
        using var fixture = JournalFixture.Create(CreatedUtc);
        var first = await fixture.Journal.AppendAsync(
            fixture.CreateDraft(status: ActionJournalStatus.Succeeded),
            TestContext.Current.CancellationToken);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var second = await fixture.Journal.AppendAsync(
            fixture.CreateDraft(status: ActionJournalStatus.Failed),
            TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(
            fixture.Paths.JournalIndexFile,
            "{ definitely-not-valid-json",
            TestContext.Current.CancellationToken);

        var rebuilt = await fixture.Journal.RebuildIndexAsync(TestContext.Current.CancellationToken);

        Assert.Equal([second.EventId, first.EventId], rebuilt.Events.Select(item => item.EventId));
        var storedIndex = await new AtomicJsonFile(
                fixture.Paths,
                (JsonDocumentSerializer?)null,
                timeProvider: null)
            .ReadProjectionAsync<ActionJournalIndexDocument>(
                fixture.Paths.JournalIndexDocument,
                TestContext.Current.CancellationToken);
        Assert.Equal(
            [second.EventId, first.EventId],
            storedIndex.Document.Payload.Events.Select(item => item.EventId));
    }

    [Fact]
    public async Task Corrupt_authoritative_event_fails_closed_and_does_not_publish_partial_index()
    {
        using var fixture = JournalFixture.Create(CreatedUtc);
        var appended = await fixture.Journal.AppendAsync(
            fixture.CreateDraft(status: ActionJournalStatus.Succeeded),
            TestContext.Current.CancellationToken);
        File.Delete(fixture.Paths.JournalIndexFile);
        await File.WriteAllTextAsync(
            fixture.Paths.GetJournalEventFile(appended.EventId),
            "{ malformed",
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fixture.Journal.RebuildIndexAsync(TestContext.Current.CancellationToken));

        Assert.False(File.Exists(fixture.Paths.JournalIndexFile));
    }

    [Fact]
    public async Task Unexpected_event_store_file_fails_closed()
    {
        using var fixture = JournalFixture.Create(CreatedUtc);
        var appended = await fixture.Journal.AppendAsync(
            fixture.CreateDraft(),
            TestContext.Current.CancellationToken);
        File.Delete(fixture.Paths.JournalIndexFile);
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Paths.JournalEventsDirectory, "unexpected.bin"),
            "not a documented staging artifact",
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fixture.Journal.RebuildIndexAsync(TestContext.Current.CancellationToken));

        Assert.True(File.Exists(fixture.Paths.GetJournalEventFile(appended.EventId)));
        Assert.False(File.Exists(fixture.Paths.JournalIndexFile));
    }

    [Fact]
    public async Task Documented_orphan_staging_artifact_is_ignored_during_rebuild()
    {
        using var fixture = JournalFixture.Create(CreatedUtc);
        var appended = await fixture.Journal.AppendAsync(
            fixture.CreateDraft(),
            TestContext.Current.CancellationToken);
        var staging = Path.Combine(
            fixture.Paths.JournalEventsDirectory,
            $"{Guid.NewGuid():N}.json.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(
            staging,
            "interrupted staging bytes",
            TestContext.Current.CancellationToken);

        var rebuilt = await fixture.Journal.RebuildIndexAsync(TestContext.Current.CancellationToken);

        Assert.Equal([appended.EventId], rebuilt.Events.Select(entry => entry.EventId));
    }

    [Fact]
    public async Task Persisted_unknown_type_is_rejected_by_the_allowlist()
    {
        using var fixture = JournalFixture.Create(CreatedUtc);
        var eventId = Guid.NewGuid().ToString("N");
        var validDraft = fixture.CreateDraft();
        var payload = new ActionJournalDocument(
            eventId,
            CreatedUtc,
            validDraft.OperationId,
            validDraft.CatalogOperationId,
            validDraft.Kind,
            validDraft.Category,
            (ActionJournalStatus)999,
            validDraft.Risk,
            validDraft.Privilege,
            validDraft.SupportStatus,
            validDraft.CorrelationId,
            validDraft.TargetCorrelationHash,
            validDraft.AffectedItemCount);
        var serializer = new JsonDocumentSerializer();
        var envelope = serializer.CreateEnvelope(eventId, CreatedUtc, payload);
        Directory.CreateDirectory(fixture.Paths.JournalEventsDirectory);
        await File.WriteAllBytesAsync(
            fixture.Paths.GetJournalEventFile(eventId),
            serializer.Serialize(envelope),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fixture.Journal.RebuildIndexAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(ActionJournalEventKind.Operation, (ActionJournalCategory)999)]
    [InlineData((ActionJournalEventKind)999, ActionJournalCategory.WindowsPersonalization)]
    public async Task Append_rejects_unknown_schema_types_before_creating_a_file(
        ActionJournalEventKind kind,
        ActionJournalCategory category)
    {
        using var fixture = JournalFixture.Create(CreatedUtc);
        var draft = fixture.CreateDraft(kind: kind, category: category);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await fixture.Journal.AppendAsync(draft, TestContext.Current.CancellationToken));

        Assert.False(Directory.Exists(fixture.Paths.JournalEventsDirectory));
    }

    [Fact]
    public async Task Append_rejects_unknown_catalog_operation_before_creating_a_file()
    {
        using var fixture = JournalFixture.Create(CreatedUtc);
        var draft = fixture.CreateDraft() with
        {
            CatalogOperationId = "unknown.operation",
        };

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await fixture.Journal.AppendAsync(draft, TestContext.Current.CancellationToken));

        Assert.False(Directory.Exists(fixture.Paths.JournalEventsDirectory));
    }

    [Fact]
    public void Core_contract_is_implemented_without_exposing_persistence_types()
    {
        Assert.True(typeof(IActionJournal).IsAssignableFrom(typeof(ActionJournal)));
        Assert.DoesNotContain(
            typeof(IActionJournal).GetMethods().SelectMany(method =>
                method.GetParameters().Select(parameter => parameter.ParameterType)),
            type => type.Namespace?.StartsWith(
                "Winora.Infrastructure",
                StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Read_all_returns_verified_events_when_index_publication_fails()
    {
        using var fixture = JournalFixture.Create(CreatedUtc);
        var expected = await fixture.Journal.AppendAsync(
            fixture.CreateDraft(),
            TestContext.Current.CancellationToken);
        var documents = new AtomicJsonFile(
            fixture.Paths,
            publisher: new FailJournalProjectionPublisher(),
            timeProvider: fixture.Clock);
        var reader = new ActionJournal(
            fixture.Paths,
            new FixedActionJournalOperationCatalog(
                ["windows.effects.transparency", "winora.retention"]),
            documents,
            fixture.Clock);

        var entries = await reader.ReadAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal([expected], entries);
    }

    [Fact]
    public async Task Reused_event_identity_never_overwrites_the_first_event()
    {
        using var fixture = JournalFixture.Create(CreatedUtc, fixedEventId: new string('a', 32));
        var first = await fixture.Journal.AppendAsync(
            fixture.CreateDraft(status: ActionJournalStatus.Succeeded),
            TestContext.Current.CancellationToken);
        var firstBytes = await File.ReadAllBytesAsync(
            fixture.Paths.GetJournalEventFile(first.EventId),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IOException>(async () =>
            await fixture.Journal.AppendAsync(
                fixture.CreateDraft(status: ActionJournalStatus.Failed),
                TestContext.Current.CancellationToken));

        Assert.Equal(
            firstBytes,
            await File.ReadAllBytesAsync(
                fixture.Paths.GetJournalEventFile(first.EventId),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Sensitive_target_is_persisted_only_as_a_salted_local_hash()
    {
        using var fixture = JournalFixture.Create(CreatedUtc);
        const string sensitiveTarget = @"C:\Users\alice\Documents\private\theme.reg";
        var localSalt = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var otherSalt = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        var hasher = new ActionTargetCorrelationHasher(localSalt);
        var hash = hasher.Hash(sensitiveTarget);

        Assert.Equal(hash, hasher.Hash(sensitiveTarget));
        Assert.NotEqual(hash, new ActionTargetCorrelationHasher(otherSalt).Hash(sensitiveTarget));
        Assert.Equal(64, hash.Length);
        Assert.True(hash.All(character => char.IsAsciiHexDigit(character) && !char.IsAsciiLetterLower(character)));

        var appended = await fixture.Journal.AppendAsync(
            fixture.CreateDraft(targetCorrelationHash: hash),
            TestContext.Current.CancellationToken);
        var persisted = await File.ReadAllTextAsync(
            fixture.Paths.GetJournalEventFile(appended.EventId),
            TestContext.Current.CancellationToken);

        Assert.Contains(hash, persisted, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveTarget, persisted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alice", persisted, StringComparison.OrdinalIgnoreCase);
        CryptographicOperations.ZeroMemory(localSalt);
        CryptographicOperations.ZeroMemory(otherSalt);
    }

    private sealed class JournalFixture : IDisposable
    {
        private JournalFixture(
            string root,
            WinoraDataPaths paths,
            MutableJournalTimeProvider clock,
            ActionJournal journal)
        {
            Root = root;
            Paths = paths;
            Clock = clock;
            Journal = journal;
        }

        internal string Root { get; }

        internal WinoraDataPaths Paths { get; }

        internal MutableJournalTimeProvider Clock { get; }

        internal ActionJournal Journal { get; }

        internal static JournalFixture Create(DateTimeOffset now, string? fixedEventId = null)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "Winora.Tests",
                "ActionJournal",
                Guid.NewGuid().ToString("N"));
            var paths = new WinoraDataPaths(root);
            var clock = new MutableJournalTimeProvider(now);
            var documents = new AtomicJsonFile(paths, (JsonDocumentSerializer?)null, clock);
            var journal = new ActionJournal(
                paths,
                new FixedActionJournalOperationCatalog(
                    ["windows.effects.transparency", "winora.retention"]),
                documents,
                clock,
                fixedEventId is null ? null : () => fixedEventId);
            return new JournalFixture(root, paths, clock, journal);
        }

        internal ActionJournalEntryDraft CreateDraft(
            ActionJournalEventKind kind = ActionJournalEventKind.Operation,
            ActionJournalCategory category = ActionJournalCategory.WindowsPersonalization,
            ActionJournalStatus status = ActionJournalStatus.Succeeded,
            string? targetCorrelationHash = null) =>
            new(
                Guid.NewGuid(),
                "windows.effects.transparency",
                kind,
                category,
                status,
                ActionJournalRisk.Low,
                ActionJournalPrivilege.StandardUser,
                ActionJournalSupportStatus.Supported,
                Guid.NewGuid(),
                targetCorrelationHash,
                AffectedItemCount: null);

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}

internal sealed class MutableJournalTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    internal void Advance(TimeSpan value) => _utcNow += value;
}

internal static class JournalTestContext
{
    internal static JournalTestContextState Current { get; } = new();
}

internal sealed class JournalTestContextState
{
    internal CancellationToken CancellationToken => CancellationToken.None;
}

internal sealed class FailJournalProjectionPublisher : IWriteThroughPublisher
{
    private readonly WriteThroughPublisher _inner = new();

    public ValueTask PublishNewAsync(
        ValidatedFileHandle temporaryFile,
        string finalPath,
        ReadOnlyMemory<byte> expectedHash,
        CancellationToken cancellationToken) =>
        _inner.PublishNewAsync(temporaryFile, finalPath, expectedHash, cancellationToken);

    public ValueTask ReplaceProjectionAsync(
        ValidatedFileHandle temporaryFile,
        ValidatedFileHandle targetFile,
        string finalPath,
        ValidatedFileHandle? existingLastKnownGoodFile,
        string lastKnownGoodPath,
        ReadOnlyMemory<byte> expectedHash,
        CancellationToken cancellationToken) =>
        ValueTask.FromException(new IOException("Injected journal-index failure."));
}
