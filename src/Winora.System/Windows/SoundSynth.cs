using System.Text;

namespace Winora.System.Windows;

/// <param name="StartHz">Pitch at the start of the tone.</param>
/// <param name="EndHz">Pitch at the end; equal to <paramref name="StartHz"/> for a steady tone.</param>
/// <param name="Seconds">Duration of this tone.</param>
/// <param name="Gain">Peak level, 0 to 1.</param>
/// <param name="DelaySeconds">Silence before the tone starts, for two-note figures.</param>
public readonly record struct SoundTone(
    double StartHz,
    double EndHz,
    double Seconds,
    double Gain,
    double DelaySeconds = 0);

/// <summary>
/// Renders short, soft notification tones to WAV bytes.
/// </summary>
/// <remarks>
/// <para>
/// Sounds are synthesised rather than downloaded. A sound pack from a theme site is somebody else's
/// audio with somebody else's licence, and this app runs elevated; generating them removes the
/// download, the licence question and the trust question in one go, and makes the result something
/// this project can actually tune.
/// </para>
/// <para>
/// The shaping is deliberate and is what makes these easier to live with than a sharp system beep:
/// a sine fundamental with a quiet octave above it, a short but non-zero attack so nothing clicks,
/// and an exponential decay. A tone that starts instantly at full amplitude produces a click on
/// every playback, which is most of what makes cheap notification sounds unpleasant.
/// </para>
/// </remarks>
public static class SoundSynth
{
    private const int SampleRate = 44100;
    private const int BitsPerSample = 16;
    private const double AttackSeconds = 0.012;

    /// <summary>Renders one or more tones, mixed onto a single mono track, as a RIFF/WAVE file.</summary>
    public static byte[] Render(IReadOnlyList<SoundTone> tones, double masterGain = 1.0)
    {
        ArgumentNullException.ThrowIfNull(tones);
        if (tones.Count == 0)
        {
            throw new ArgumentException("A sound needs at least one tone.", nameof(tones));
        }

        var totalSeconds = tones.Max(static tone => tone.DelaySeconds + tone.Seconds);

        // A short tail so the decay is never cut off mid-fall, which would click just as badly as a
        // hard attack does.
        var sampleCount = (int)((totalSeconds + 0.05) * SampleRate);
        var mix = new double[sampleCount];

        foreach (var tone in tones)
        {
            var start = (int)(tone.DelaySeconds * SampleRate);
            var length = (int)(tone.Seconds * SampleRate);
            var phase = 0.0;

            for (var index = 0; index < length && start + index < sampleCount; index++)
            {
                var position = (double)index / length;
                var hz = tone.StartHz + ((tone.EndHz - tone.StartHz) * position);
                phase += 2 * Math.PI * hz / SampleRate;

                // A quiet octave gives the tone a little body without making it brighter or harsher.
                var sample = Math.Sin(phase) + (0.18 * Math.Sin(2 * phase));

                mix[start + index] += sample * tone.Gain * Envelope(index, length);
            }
        }

        return Encode(mix, masterGain);
    }

    private static double Envelope(int index, int length)
    {
        var seconds = (double)index / SampleRate;
        var attack = seconds < AttackSeconds ? seconds / AttackSeconds : 1.0;

        // Exponential rather than linear: a linear fade still ends abruptly to the ear.
        var decay = Math.Exp(-4.0 * index / length);
        return attack * decay;
    }

    private static byte[] Encode(double[] mix, double masterGain)
    {
        // Normalised before the master gain so a two-tone sound is not quietly louder than a
        // one-tone sound purely because more of them overlap.
        var peak = mix.Max(Math.Abs);
        var scale = peak > 0 ? masterGain / peak : 0;

        var dataBytes = mix.Length * (BitsPerSample / 8);
        using var stream = new MemoryStream(44 + dataBytes);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(SampleRate);
        writer.Write(SampleRate * (BitsPerSample / 8));
        writer.Write((short)(BitsPerSample / 8));
        writer.Write((short)BitsPerSample);
        writer.Write("data"u8);
        writer.Write(dataBytes);

        foreach (var sample in mix)
        {
            var scaled = Math.Clamp(sample * scale, -1.0, 1.0);
            writer.Write((short)(scaled * short.MaxValue));
        }

        writer.Flush();
        return stream.ToArray();
    }
}
