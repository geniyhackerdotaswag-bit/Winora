using System.Text;
using Winora.Core.Appearance;
using Winora.Infrastructure.Appearance;
using Winora.Infrastructure.Paths;
using Winora.Infrastructure.Persistence;
using Winora.Infrastructure.Tests.Persistence;
using Xunit;

namespace Winora.Infrastructure.Tests.Appearance;

/// <summary>
/// The user's colours, across a restart.
/// </summary>
/// <remarks>
/// Loading is required never to throw. This is a cosmetic preference, and an app that refuses to
/// start because someone mistyped a hex code in a file it invited them to open would be a far worse
/// defect than the wrong shade of grey.
/// </remarks>
public sealed class ColorSchemeStoreTests
{
    private static ColorSchemeStore Store(TemporaryDirectory directory)
    {
        var paths = new WinoraDataPaths(directory.Path);
        // The casts pick the public constructor. This assembly can see the internal overload too,
        // which makes the one-argument call ambiguous; the neighbouring journal tests do the same.
        return new ColorSchemeStore(
            paths,
            new AtomicJsonFile(paths, (JsonDocumentSerializer?)null, (TimeProvider?)null));
    }

    private static WinoraColorScheme Custom() => new()
    {
        Canvas = ColorValue.Parse("#101318"),
        Accent = ColorValue.Parse("#E0D7AF"),
        TextFaint = ColorValue.Parse("#9AA0A6"),
    };

    [Fact]
    public async Task A_saved_scheme_comes_back_exactly()
    {
        using var directory = new TemporaryDirectory();
        var store = Store(directory);
        var scheme = Custom();

        await store.SaveAsync(scheme, CancellationToken.None);
        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(ColorSchemeLoadOutcome.Stored, loaded.Outcome);
        Assert.Equal(scheme, loaded.Scheme);
    }

    [Fact]
    public async Task A_preset_choice_survives_the_round_trip()
    {
        using var directory = new TemporaryDirectory();
        var store = Store(directory);
        var preset = ColorSchemePresets.Require("violet-light").Scheme;

        await store.SaveAsync(preset, CancellationToken.None);
        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal("violet-light", loaded.Scheme.PresetId);
    }

    [Fact]
    public async Task An_empty_store_yields_the_default_scheme()
    {
        using var directory = new TemporaryDirectory();

        var loaded = await Store(directory).LoadAsync(CancellationToken.None);

        Assert.Equal(ColorSchemeLoadOutcome.Missing, loaded.Outcome);
        Assert.Equal(ColorSchemePresets.Default, loaded.Scheme);
    }

    [Fact]
    public async Task A_corrupt_file_yields_the_default_scheme_rather_than_throwing()
    {
        using var directory = new TemporaryDirectory();
        var paths = new WinoraDataPaths(directory.Path);
        Directory.CreateDirectory(paths.DataDirectory);
        await File.WriteAllTextAsync(
            paths.AppSettingsFile,
            "{ not json at all",
            Encoding.UTF8,
            CancellationToken.None);

        var loaded = await Store(directory).LoadAsync(CancellationToken.None);

        Assert.Equal(ColorSchemePresets.Default, loaded.Scheme);
        Assert.NotEqual(ColorSchemeLoadOutcome.Stored, loaded.Outcome);
    }

    /// <summary>
    /// The one route to an unreadable scheme is a person editing the file, because the editor will
    /// not save one. It is refused on the way back in, since an app whose text cannot be read cannot
    /// be used to repair itself.
    /// </summary>
    [Fact]
    public async Task A_hand_edited_unreadable_scheme_is_refused_on_load()
    {
        using var directory = new TemporaryDirectory();
        var store = Store(directory);

        await store.SaveAsync(
            new WinoraColorScheme
            {
                Canvas = ColorValue.Parse("#7A7A7A"),
                Accent = ColorValue.Parse("#7C7C7C"),
            },
            CancellationToken.None);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(ColorSchemeLoadOutcome.Rejected, loaded.Outcome);
        Assert.Equal(ColorSchemePresets.Default, loaded.Scheme);
    }

    /// <summary>
    /// A malformed override is not the same as an absent one. Reading it as absent would discard
    /// the user's choice and leave nothing anywhere to say it happened.
    /// </summary>
    /// <remarks>
    /// The document is written straight through <see cref="AtomicJsonFile" /> rather than by editing
    /// the file, because an edited file never reaches the colour parsing at all — see
    /// <see cref="A_hand_edited_file_is_rejected_before_its_colours_are_even_read" />. Writing a
    /// properly sealed document with a bad colour inside it is the only way to exercise this path.
    /// </remarks>
    [Fact]
    public async Task A_malformed_colour_is_not_read_as_an_absent_one()
    {
        using var directory = new TemporaryDirectory();
        await WriteSealedAsync(
            directory,
            new ColorSchemeDocument
            {
                Canvas = "#101318",
                Accent = "#E0D7AF",
                TextFaint = "not-a-colour",
            });

        var loaded = await Store(directory).LoadAsync(CancellationToken.None);

        Assert.Equal(ColorSchemeLoadOutcome.Unreadable, loaded.Outcome);
        Assert.Equal(ColorSchemePresets.Default, loaded.Scheme);
    }

    /// <summary>
    /// The envelope carries a SHA-256 of its payload, so the settings file is tamper-evident: any
    /// edit by hand invalidates the document as a whole and Winora starts from the default.
    /// </summary>
    /// <remarks>
    /// Pinned as a test because the comment on <see cref="ColorSchemeDocument" /> originally claimed
    /// the file was meant to be edited by hand, and it is not. Someone will otherwise rediscover
    /// this by changing a hex code, seeing the app ignore it, and concluding the store is broken.
    /// </remarks>
    [Fact]
    public async Task A_hand_edited_file_is_rejected_before_its_colours_are_even_read()
    {
        using var directory = new TemporaryDirectory();
        var store = Store(directory);
        await store.SaveAsync(Custom(), CancellationToken.None);

        var file = new WinoraDataPaths(directory.Path).AppSettingsFile;
        var text = await File.ReadAllTextAsync(file, CancellationToken.None);
        await File.WriteAllTextAsync(
            file,
            text.Replace("#E0D7AF", "#CE3535", StringComparison.Ordinal),
            CancellationToken.None);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(ColorSchemeLoadOutcome.Unreadable, loaded.Outcome);
        Assert.Equal(ColorSchemePresets.Default, loaded.Scheme);
    }

    /// <summary>
    /// A preset that no longer exists must not take the user's colours down with it. They keep what
    /// they last saw; only the association with a named preset is dropped.
    /// </summary>
    [Fact]
    public async Task An_unknown_preset_identifier_keeps_the_colours_and_drops_the_association()
    {
        using var directory = new TemporaryDirectory();
        var scheme = Custom() with { PresetId = "retired-preset" };

        await Store(directory).SaveAsync(scheme, CancellationToken.None);
        var loaded = await Store(directory).LoadAsync(CancellationToken.None);

        Assert.Equal(ColorSchemeLoadOutcome.Stored, loaded.Outcome);
        Assert.Null(loaded.Scheme.PresetId);
        Assert.Equal(scheme.Canvas, loaded.Scheme.Canvas);
        Assert.Equal(scheme.Accent, loaded.Scheme.Accent);
        Assert.Equal(scheme.TextFaint, loaded.Scheme.TextFaint);
    }

    /// <summary>
    /// Writes a settings document with a valid envelope, so the load path reaches the colour
    /// parsing rather than stopping at the payload hash.
    /// </summary>
    private static async Task WriteSealedAsync(
        TemporaryDirectory directory,
        ColorSchemeDocument document)
    {
        var paths = new WinoraDataPaths(directory.Path);
        Directory.CreateDirectory(paths.DataDirectory);

        await new AtomicJsonFile(paths, (JsonDocumentSerializer?)null, (TimeProvider?)null)
            .WriteProjectionAsync(
                paths.AppSettingsDocument,
                new AppSettingsPayload { ColorScheme = document },
                CancellationToken.None);
    }

    [Fact]
    public async Task Saving_twice_replaces_rather_than_appends()
    {
        using var directory = new TemporaryDirectory();
        var store = Store(directory);

        await store.SaveAsync(Custom(), CancellationToken.None);
        await store.SaveAsync(
            ColorSchemePresets.Require("red-dark").Scheme,
            CancellationToken.None);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal("red-dark", loaded.Scheme.PresetId);
    }
}
