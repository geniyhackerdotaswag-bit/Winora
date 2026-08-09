using Winora.Core.Appearance;
using Winora.Infrastructure.Paths;
using Winora.Infrastructure.Persistence;

namespace Winora.Infrastructure.Appearance;

/// <summary>
/// The colour scheme as it sits in <c>app-settings.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Colours are hex strings rather than channel objects so the file is legible when someone opens it
/// to see what went wrong: <c>{"canvas":"#0C0C0F"}</c> can be read at a glance and
/// <c>{"canvas":{"r":12,"g":12,"b":15}}</c> cannot. Legible is not the same as editable — the
/// envelope carries a SHA-256 of the payload, so a hand edit invalidates the whole document and
/// Winora falls back to the default scheme. Measured while writing these tests, after this comment
/// first claimed the opposite. The integrity check is deliberate and is not weakened for a cosmetic
/// preference; the appearance screen is the way to change these colours.
/// </para>
/// <para>
/// Every field is nullable so that a file written by an older build, which simply lacks the newer
/// ones, still loads.
/// </para>
/// </remarks>
public sealed record ColorSchemeDocument
{
    public string? PresetId { get; init; }

    public string? Canvas { get; init; }

    public string? Accent { get; init; }

    public string? OnAccent { get; init; }

    public string? TextPrimary { get; init; }

    public string? TextMuted { get; init; }

    public string? TextFaint { get; init; }

    public string? Card { get; init; }

    public string? CardHover { get; init; }

    public string? Divider { get; init; }

    public string? Stroke { get; init; }
}

/// <summary>
/// Everything Winora remembers about itself between sessions.
/// </summary>
/// <remarks>
/// One document rather than a file per preference. <c>app-settings.json</c> has been declared in
/// <see cref="WinoraDataPaths" /> since the store was designed and had no writer until now; the
/// appearance scheme is its first content, and anything else that becomes a genuine preference
/// joins it here rather than growing a second file.
/// </remarks>
public sealed record AppSettingsPayload
{
    public ColorSchemeDocument? ColorScheme { get; init; }
}

/// <inheritdoc />
public sealed class ColorSchemeStore : IColorSchemeStore
{
    private readonly WinoraDataPaths _paths;
    private readonly AtomicJsonFile _files;

    public ColorSchemeStore(WinoraDataPaths paths, AtomicJsonFile files)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _files = files ?? throw new ArgumentNullException(nameof(files));
    }

    /// <inheritdoc />
    public async ValueTask<ColorSchemeLoad> LoadAsync(CancellationToken cancellationToken = default)
    {
        ProjectionJsonReadResult<AppSettingsPayload> read;
        try
        {
            read = await _files
                .ReadProjectionAsync<AppSettingsPayload>(_paths.AppSettingsDocument, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return Fallback(ColorSchemeLoadOutcome.Missing);
        }
        catch (DirectoryNotFoundException)
        {
            return Fallback(ColorSchemeLoadOutcome.Missing);
        }
        catch (InvalidDataException)
        {
            // Malformed JSON, a failed payload hash, a schema version this build does not know.
            return Fallback(ColorSchemeLoadOutcome.Unreadable);
        }
        catch (IOException)
        {
            return Fallback(ColorSchemeLoadOutcome.Unreadable);
        }
        catch (UnauthorizedAccessException)
        {
            return Fallback(ColorSchemeLoadOutcome.Unreadable);
        }

        var document = read.Document.Payload?.ColorScheme;
        if (document is null)
        {
            return Fallback(ColorSchemeLoadOutcome.Missing);
        }

        if (!TryReadScheme(document, out var scheme) || scheme is null)
        {
            return Fallback(ColorSchemeLoadOutcome.Unreadable);
        }

        // The gate the editor enforces, enforced again here. The editor cannot save an unreadable
        // scheme, so the only way to arrive at one is by editing the file — and an app that opens
        // with text nobody can read cannot be used to fix itself.
        return SchemeContrast.Measure(SchemeDerivation.Derive(scheme)).CanApply
            ? new ColorSchemeLoad(scheme, ColorSchemeLoadOutcome.Stored)
            : Fallback(ColorSchemeLoadOutcome.Rejected);
    }

    /// <inheritdoc />
    public async ValueTask SaveAsync(
        WinoraColorScheme scheme,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scheme);

        Directory.CreateDirectory(_paths.DataDirectory);

        await _files.WriteProjectionAsync(
            _paths.AppSettingsDocument,
            new AppSettingsPayload { ColorScheme = ToDocument(scheme) },
            cancellationToken).ConfigureAwait(false);
    }

    private static ColorSchemeLoad Fallback(ColorSchemeLoadOutcome outcome) =>
        new(ColorSchemePresets.Default, outcome);

    private static bool TryReadScheme(ColorSchemeDocument document, out WinoraColorScheme? scheme)
    {
        scheme = null;

        // The two required colours. A file missing either describes nothing usable, and guessing
        // one of them from the other would invent a scheme the user never chose.
        if (!ColorValue.TryParse(document.Canvas, out var canvas) ||
            !ColorValue.TryParse(document.Accent, out var accent))
        {
            return false;
        }

        if (!TryReadOptional(document.OnAccent, out var onAccent) ||
            !TryReadOptional(document.TextPrimary, out var textPrimary) ||
            !TryReadOptional(document.TextMuted, out var textMuted) ||
            !TryReadOptional(document.TextFaint, out var textFaint) ||
            !TryReadOptional(document.Card, out var card) ||
            !TryReadOptional(document.CardHover, out var cardHover) ||
            !TryReadOptional(document.Divider, out var divider) ||
            !TryReadOptional(document.Stroke, out var stroke))
        {
            return false;
        }

        // An identifier this build does not know means a preset that was removed or renamed. The
        // colours are still exactly what the user last saw, so they are kept and the association is
        // simply dropped — the scheme becomes a custom one rather than being thrown away.
        var presetId = ColorSchemePresets.TryGet(document.PresetId, out _) ? document.PresetId : null;

        scheme = new WinoraColorScheme
        {
            Canvas = canvas,
            Accent = accent,
            OnAccent = onAccent,
            TextPrimary = textPrimary,
            TextMuted = textMuted,
            TextFaint = textFaint,
            Card = card,
            CardHover = cardHover,
            Divider = divider,
            Stroke = stroke,
            PresetId = presetId,
        };

        return true;
    }

    /// <summary>
    /// An absent optional colour is fine; a present but malformed one is not. Treating the second
    /// case as the first would silently discard an override and leave no sign of it.
    /// </summary>
    private static bool TryReadOptional(string? text, out ColorValue? value)
    {
        if (text is null)
        {
            value = null;
            return true;
        }

        if (ColorValue.TryParse(text, out var parsed))
        {
            value = parsed;
            return true;
        }

        value = null;
        return false;
    }

    private static ColorSchemeDocument ToDocument(WinoraColorScheme scheme) => new()
    {
        PresetId = scheme.PresetId,
        Canvas = scheme.Canvas.ToHex(),
        Accent = scheme.Accent.ToHex(),
        OnAccent = scheme.OnAccent?.ToHex(),
        TextPrimary = scheme.TextPrimary?.ToHex(),
        TextMuted = scheme.TextMuted?.ToHex(),
        TextFaint = scheme.TextFaint?.ToHex(),
        Card = scheme.Card?.ToHex(),
        CardHover = scheme.CardHover?.ToHex(),
        Divider = scheme.Divider?.ToHex(),
        Stroke = scheme.Stroke?.ToHex(),
    };
}
