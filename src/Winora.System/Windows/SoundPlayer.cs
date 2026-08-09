using System.Runtime.InteropServices;

namespace Winora.System.Windows;

/// <summary>Plays a sound file so the user can hear it before choosing.</summary>
public interface ISoundPlayer
{
    void Play(string file);
}

/// <summary>
/// Plays a WAV through the documented <c>PlaySound</c>.
/// </summary>
/// <remarks>
/// Asynchronous and set to fail silently: a preview must never block the screen it was clicked on,
/// and a missing or malformed file should leave the user with no sound rather than an error dialog
/// about a sound they were only auditioning.
/// Microsoft Learn: https://learn.microsoft.com/en-us/previous-versions/dd743680(v=vs.85)
/// </remarks>
public sealed partial class WindowsSoundPlayer : ISoundPlayer
{
    private const uint SndFilename = 0x00020000;
    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;

    public void Play(string file)
    {
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
        {
            return;
        }

        try
        {
            PlaySound(file, nint.Zero, SndFilename | SndAsync | SndNoDefault);
        }
        catch (Exception)
        {
        }
    }

    [LibraryImport("winmm.dll", EntryPoint = "PlaySoundW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PlaySound(string? sound, nint module, uint flags);
}
