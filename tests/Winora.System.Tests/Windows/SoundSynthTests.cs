using System.Text;
using Winora.System.Windows;
using Xunit;

namespace Winora.System.Tests.Platform;

/// <summary>
/// The audio is generated, so nothing external guarantees it is a valid file or that it sounds like
/// anything. These tests read the bytes back and check the properties that make the difference
/// between a usable notification and an unpleasant one.
/// </summary>
public sealed class SoundSynthTests
{
    private static readonly SoundTone[] Simple = [new(440, 440, 0.15, 0.9)];

    [Fact]
    public void The_output_is_a_valid_riff_wave_header()
    {
        var wav = SoundSynth.Render(Simple);

        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));

        // Declared sizes must match the real payload, or players reject the file.
        Assert.Equal(wav.Length - 8, BitConverter.ToInt32(wav, 4));
        Assert.Equal(wav.Length - 44, BitConverter.ToInt32(wav, 40));
    }

    [Fact]
    public void The_format_is_16_bit_mono_at_44100()
    {
        var wav = SoundSynth.Render(Simple);

        Assert.Equal(1, BitConverter.ToInt16(wav, 20));
        Assert.Equal(1, BitConverter.ToInt16(wav, 22));
        Assert.Equal(44100, BitConverter.ToInt32(wav, 24));
        Assert.Equal(16, BitConverter.ToInt16(wav, 34));
    }

    /// <summary>
    /// The property that makes these pleasant. A tone starting at full amplitude clicks on every
    /// playback, which is most of what makes a cheap notification sound unpleasant.
    /// </summary>
    [Fact]
    public void The_sound_starts_from_silence_rather_than_clicking()
    {
        var wav = SoundSynth.Render(Simple);

        var first = Math.Abs(SampleAt(wav, 0));
        var peak = Enumerable.Range(0, SampleCount(wav)).Max(index => Math.Abs(SampleAt(wav, index)));

        Assert.True(peak > 3000, "the sound is audible");
        Assert.True(first < peak / 20, $"first sample {first} is far below the peak {peak}");
    }

    /// <summary>And it must end from silence too, or the decay is cut off with a click.</summary>
    [Fact]
    public void The_sound_ends_in_silence()
    {
        var wav = SoundSynth.Render(Simple);
        var count = SampleCount(wav);

        Assert.True(Math.Abs(SampleAt(wav, count - 1)) < 500);
    }

    /// <summary>
    /// Levels are what the packs actually differ by, so a quieter pack must really be quieter.
    /// </summary>
    [Fact]
    public void A_lower_master_gain_produces_a_quieter_file()
    {
        var loud = SoundSynth.Render(Simple, 0.9);
        var soft = SoundSynth.Render(Simple, 0.2);

        Assert.True(Peak(soft) < Peak(loud) / 3);
    }

    /// <summary>
    /// Normalisation before the master gain: otherwise a two-tone sound would come out louder than
    /// a one-tone sound purely because the tones overlap.
    /// </summary>
    [Fact]
    public void A_two_tone_sound_is_not_louder_than_a_one_tone_sound()
    {
        var single = Peak(SoundSynth.Render([new(440, 440, 0.15, 0.9)], 0.6));
        var pair = Peak(SoundSynth.Render(
            [new(440, 440, 0.15, 0.9), new(660, 660, 0.15, 0.9, 0.05)],
            0.6));

        Assert.InRange(pair, single - 400, single + 400);
    }

    [Fact]
    public void Every_generated_event_sound_is_audible()
    {
        var root = Directory.CreateTempSubdirectory("winora-sounds").FullName;
        try
        {
            var builder = new SoundPackBuilder(root);
            var packs = builder.EnsurePacks();

            Assert.NotEmpty(packs);
            foreach (var pack in packs)
            {
                var files = builder.FilesFor(pack);
                Assert.NotEmpty(files);
                foreach (var file in files.Values)
                {
                    Assert.True(Peak(File.ReadAllBytes(file)) > 1000, $"{file} is silent");
                }
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A user who replaced a generated sound with their own must keep it.</summary>
    [Fact]
    public void An_existing_sound_file_is_not_overwritten()
    {
        var root = Directory.CreateTempSubdirectory("winora-sounds-keep").FullName;
        try
        {
            var builder = new SoundPackBuilder(root);
            builder.EnsurePacks();

            var file = builder.FilesFor("soft")[SoundEvent.Notification];
            File.WriteAllBytes(file, [1, 2, 3]);

            builder.EnsurePacks();

            Assert.Equal(3, new FileInfo(file).Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static int SampleCount(byte[] wav) => (wav.Length - 44) / 2;

    private static short SampleAt(byte[] wav, int index) => BitConverter.ToInt16(wav, 44 + (index * 2));

    private static int Peak(byte[] wav) =>
        Enumerable.Range(0, SampleCount(wav)).Max(index => Math.Abs((int)SampleAt(wav, index)));
}
