using Winora.System.Windows;
using Xunit;

namespace Winora.System.Tests.Platform;

/// <summary>
/// Matching a sound file to an event is a guess, tested on the names real downloaded packs use.
/// </summary>
public sealed class SoundFolderScannerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("winora-sound-folder").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// The trap in this domain: "disconnect" contains "connect". Getting it backwards would play
    /// the unplug sound when a device is plugged in, on every pack.
    /// </summary>
    [Theory]
    [InlineData("Connect.wav", SoundEvent.DeviceConnect)]
    [InlineData("Disconnect.wav", SoundEvent.DeviceDisconnect)]
    [InlineData("usb on.wav", SoundEvent.DeviceConnect)]
    [InlineData("usb off.wav", SoundEvent.DeviceDisconnect)]
    public void Connect_and_disconnect_are_not_swapped(string fileName, SoundEvent expected)
    {
        Assert.Equal(expected, SoundFolderScanner.EventForFileName(fileName));
    }

    /// <summary>Names taken verbatim from the Beauty, Crystallio and Ubuntu packs.</summary>
    [Theory]
    [InlineData("default sound.wav", SoundEvent.Notification)]
    [InlineData("uved.wav", SoundEvent.Notification)]
    [InlineData("notify.wav", SoundEvent.Notification)]
    [InlineData("Balloon.wav", SoundEvent.Notification)]
    [InlineData("errorr.wav", SoundEvent.DeviceFail)]
    [InlineData("fail.wav", SoundEvent.DeviceFail)]
    [InlineData("usb error.wav", SoundEvent.DeviceFail)]
    public void Real_pack_names_resolve(string fileName, SoundEvent expected)
    {
        Assert.Equal(expected, SoundFolderScanner.EventForFileName(fileName));
    }

    [Fact]
    public void An_unrecognisable_name_matches_nothing()
    {
        Assert.Null(SoundFolderScanner.EventForFileName("ir_inter.wav"));
    }

    /// <summary>
    /// Sound packs ship .reg files that apply the scheme by importing somebody else's registry
    /// commands. Winora runs elevated and must never write one to disk, let alone run it.
    /// </summary>
    [Fact]
    public void Registry_scripts_shipped_with_a_pack_are_never_read()
    {
        var pack = Path.Combine(_root, "Crystallio");
        Directory.CreateDirectory(pack);
        File.WriteAllText(Path.Combine(pack, "notify.wav"), "RIFF");
        File.WriteAllText(Path.Combine(pack, "Crystallio (8, 10).reg"), "REGEDIT4");

        var found = Assert.Single(new SoundFolderScanner().Packs(_root));

        Assert.All(found.Files.Values, file =>
            Assert.DoesNotContain(".reg", file, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_pack_picture_is_offered_when_it_ships_one()
    {
        var pack = Path.Combine(_root, "Crystallio");
        Directory.CreateDirectory(pack);
        File.WriteAllText(Path.Combine(pack, "notify.wav"), "RIFF");
        File.WriteAllText(Path.Combine(pack, "Crystallio.jpg"), "JPEG");

        var found = Assert.Single(new SoundFolderScanner().Packs(_root));

        Assert.EndsWith("Crystallio.jpg", found.ImagePath, StringComparison.Ordinal);
    }

    /// <summary>Winora's own generated packs are offered separately and must not be listed twice.</summary>
    [Fact]
    public void The_generated_packs_are_not_read_as_user_packs()
    {
        foreach (var definition in SoundPackBuilder.Definitions)
        {
            var directory = Path.Combine(_root, definition.Id);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "Notification.wav"), "RIFF");
        }

        Assert.Empty(new SoundFolderScanner().Packs(_root));
    }

    [Fact]
    public void A_folder_with_no_sounds_is_not_a_pack()
    {
        var pack = Path.Combine(_root, "docs");
        Directory.CreateDirectory(pack);
        File.WriteAllText(Path.Combine(pack, "EN.pdf"), "%PDF");

        Assert.Empty(new SoundFolderScanner().Packs(_root));
    }
}
