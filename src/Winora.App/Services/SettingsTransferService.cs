using System.Text.Json;
using System.Text.Json.Serialization;
using Winora.Core.Changes;
using Winora.Core.Contracts;
using Winora.Core.Settings;

namespace Winora.App.Services;

/// <param name="OperationId">Which setting.</param>
/// <param name="Value">What it is set to on this machine.</param>
public sealed record CapturedSetting(string OperationId, string Value);

/// <param name="Applied">Settings written on this machine.</param>
/// <param name="Unchanged">Settings the file agreed with, which were therefore left alone.</param>
/// <param name="Refused">Entries this build would not act on, and why.</param>
/// <param name="Failed">Settings that were attempted and did not take.</param>
public sealed record SettingsTransferReport(
    int Applied,
    int Unchanged,
    IReadOnlyList<SettingsCandidate> Refused,
    IReadOnlyList<string> Failed);

/// <summary>Carrying a machine's settings to another machine, as a file.</summary>
public interface ISettingsTransferService
{
    /// <summary>How many settings this build offers to carry.</summary>
    int PortableCount { get; }

    /// <summary>Reads every transferable setting from this machine.</summary>
    Task<IReadOnlyList<CapturedSetting>> CaptureAsync(CancellationToken cancellationToken = default);

    /// <summary>Writes a capture to a file. False when the file could not be written.</summary>
    Task<bool> SaveAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Applies a file to this machine. Null when the file is not one Winora wrote.</summary>
    Task<SettingsTransferReport?> ApplyAsync(string path, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// Nothing dangerous is invented here. Reading is <see cref="IOperation.ProbeAsync"/>, the same
/// call every screen makes to show what a setting currently is. Writing is
/// <see cref="IChangeExecutor.ApplyAsync"/>, the same call a click makes, with the same plan,
/// backup, verification and undo. What is new is a file format, and the discipline of treating
/// what the file says as a proposal rather than as instructions.
/// </para>
/// <para>
/// Startup entries are deliberately not carried. They name programs installed on the machine that
/// wrote the file, and enabling one where it is not installed would write a run entry pointing at
/// nothing.
/// </para>
/// </remarks>
public sealed class SettingsTransferService : ISettingsTransferService
{
    /// <summary>
    /// Which settings travel, matched on the front of the identifier.
    /// </summary>
    /// <remarks>
    /// Prefixes rather than a list of names, so a value added to a screen is carried without
    /// anybody having to remember to add it here as well — a list kept by hand would drift, and
    /// the drift would be silent.
    /// </remarks>
    private static readonly string[] Portable =
    [
        "winora.shell.",
        "winora.explorer.",
        "winora.visual-effects.",
        "windows.appearance.theme",
    ];

    /// <summary>
    /// What a probe says when Windows is applying its own default.
    /// </summary>
    /// <remarks>
    /// Not carried. Writing the word to another machine would ask it to store that literal text;
    /// leaving the setting out of the file asks for nothing, which is what "I have not chosen this"
    /// actually means.
    /// </remarks>
    private const string Unset = "unset";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true,
    };

    private readonly IReadOnlyList<IOperation> _operations;
    private readonly IChangeExecutor _executor;
    private readonly ILocalizationService _text;

    public SettingsTransferService(
        IEnumerable<IOperation> operations,
        IChangeExecutor executor,
        ILocalizationService text)
    {
        ArgumentNullException.ThrowIfNull(operations);

        _operations = operations
            .Where(static operation => Portable.Any(prefix =>
                operation.OperationId.StartsWith(prefix, StringComparison.Ordinal)))
            .OrderBy(static operation => operation.OperationId, StringComparer.Ordinal)
            .ToArray();

        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _text = text ?? throw new ArgumentNullException(nameof(text));
    }

    public int PortableCount => _operations.Count;

    public async Task<IReadOnlyList<CapturedSetting>> CaptureAsync(
        CancellationToken cancellationToken = default)
    {
        var captured = new List<CapturedSetting>(_operations.Count);

        foreach (var operation in _operations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var capability = await operation
                    .ProbeAsync(new OperationTarget(operation.OperationId), cancellationToken)
                    .ConfigureAwait(false);

                var value = capability.CurrentValue?.Text ?? string.Empty;

                if (value.Length > 0 && !string.Equals(value, Unset, StringComparison.Ordinal))
                {
                    captured.Add(new CapturedSetting(operation.OperationId, value));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One unreadable setting is not a reason to produce no file at all.
            }
        }

        return captured;
    }

    public async Task<bool> SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            var captured = await CaptureAsync(cancellationToken).ConfigureAwait(false);

            var document = new TransferDocument(
                SettingsBundle.CurrentSchemaVersion,
                DateTimeOffset.UtcNow,
                [.. captured.Select(static c => new TransferEntry(c.OperationId, c.Value))]);

            await File
                .WriteAllTextAsync(path, JsonSerializer.Serialize(document, Options), cancellationToken)
                .ConfigureAwait(false);

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<SettingsTransferReport?> ApplyAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        TransferDocument? document;

        try
        {
            var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            document = JsonSerializer.Deserialize<TransferDocument>(text, Options);
        }
        catch (Exception)
        {
            // Not JSON, not readable, or not a settings file at all. Guessing at a half-understood
            // file is how a machine ends up holding a value nobody chose.
            return null;
        }

        if (document?.Entries is null)
        {
            return null;
        }

        var known = _operations
            .Select(static operation => operation.OperationId)
            .ToHashSet(StringComparer.Ordinal);

        var examined = SettingsBundle.Examine(
            document.Entries.Select(static e =>
                new SettingsEntry(e.OperationId ?? string.Empty, e.Value ?? string.Empty)),
            known);

        var applied = 0;
        var unchanged = 0;
        var failed = new List<string>();

        foreach (var candidate in examined.Where(static c => c.IsAccepted))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var operation = _operations.First(o =>
                StringComparer.Ordinal.Equals(o.OperationId, candidate.Entry.OperationId));

            try
            {
                var capability = await operation
                    .ProbeAsync(new OperationTarget(operation.OperationId), cancellationToken)
                    .ConfigureAwait(false);

                // A setting already holding the wanted value is left alone. Writing it again would
                // put a backup and a journal entry behind a change that changed nothing, and the
                // journal is what somebody reads to find out what actually happened.
                if (string.Equals(
                    capability.CurrentValue?.Text,
                    candidate.Entry.Value,
                    StringComparison.Ordinal))
                {
                    unchanged++;
                    continue;
                }

                var draft = new OperationDraft(
                    operation.OperationId,
                    "winora.category.personalization",
                    operation.OperationId,
                    _text.Get("Transfer_PlanSummary"),
                    new OperationTarget(operation.OperationId),
                    new DisplayValue("winora.value.shell-preference", candidate.Entry.Value));

                var outcome = await _executor
                    .ApplyAsync(operation, draft, cancellationToken)
                    .ConfigureAwait(false);

                if (outcome.Succeeded)
                {
                    applied++;
                }
                else
                {
                    failed.Add(operation.OperationId);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed.Add(operation.OperationId);
            }
        }

        return new SettingsTransferReport(
            applied,
            unchanged,
            [.. examined.Where(static c => !c.IsAccepted)],
            failed);
    }

    private sealed record TransferEntry(string? OperationId, string? Value);

    private sealed record TransferDocument(
        int SchemaVersion,
        DateTimeOffset CapturedUtc,
        IReadOnlyList<TransferEntry>? Entries);
}
