using Winora.Core.Profile;
using Winora.Infrastructure.Paths;

namespace Winora.Infrastructure.Profile;

/// <param name="Verdict">Why the file was kept or turned away.</param>
/// <param name="FileName">The name of the copy, and only when <paramref name="Verdict"/> is Ok.</param>
public sealed record ProfileMediaOutcome(PictureVerdict Verdict, string? FileName);

/// <summary>Where the profile's own copies of the two pictures live.</summary>
public interface IProfileMediaStore
{
    /// <summary>
    /// The full path of a stored picture, or null when there is nothing usable to point at.
    /// </summary>
    /// <remarks>
    /// Null covers all three ways a name can fail to be a picture: no name at all, a name the store
    /// would not have written, and a name whose file is no longer there. All three mean the same
    /// thing to the card, which is that it draws the initial.
    /// </remarks>
    string? PathFor(string? fileName);

    /// <summary>Checks a file the person chose and, if it passes, copies it in.</summary>
    ProfileMediaOutcome Save(ProfilePictureKind kind, string sourcePath);

    /// <summary>Deletes a stored picture. Does nothing at all for a name that is not one.</summary>
    void Remove(string? fileName);
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// The copy is the whole point. Storing the path the person picked from would mean the card shows a
/// picture until the day they empty their Downloads folder, and then quietly stops — with nothing
/// on screen to say why, and nothing they could do about it. A copy under
/// <c>%USERPROFILE%\Winora\State\media</c> is theirs, sits beside the profile it belongs to, and
/// survives an uninstall for the same reason the journal does.
/// </para>
/// <para>
/// The judging is not here. <see cref="ProfilePictureRules"/> in Winora.Core decides what a picture
/// is and whether it fits; this reads bytes, writes bytes and deletes files, which is all an
/// infrastructure type is for.
/// </para>
/// </remarks>
public sealed class ProfileMediaStore : IProfileMediaStore
{
    /// <summary>Beside profile.json rather than under it: it holds files, not a document.</summary>
    private const string FolderName = "media";

    /// <summary>
    /// How much of a file is read before it is judged.
    /// </summary>
    /// <remarks>
    /// PNG and WebP publish their size in the first thirty bytes. JPEG does not: its frame header
    /// sits behind however much metadata the camera wrote, and an EXIF block with a thumbnail in it
    /// routinely runs to tens of kilobytes. A quarter of a megabyte covers that with room to spare
    /// and still refuses to read four megabytes of a file that is about to be rejected.
    /// </remarks>
    private const int HeadLength = 256 * 1024;

    private readonly string _folder;

    public ProfileMediaStore()
        : this(WinoraDataPaths.RootForCurrentUser())
    {
    }

    public ProfileMediaStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _folder = Path.Combine(rootDirectory, FolderName);
    }

    /// <summary>The folder the copies live in. Not created until there is something to put in it.</summary>
    public string Folder => _folder;

    public string? PathFor(string? fileName)
    {
        if (!ProfilePictureRules.IsStoredFileName(fileName))
        {
            return null;
        }

        try
        {
            var path = Path.Combine(_folder, fileName!);
            return File.Exists(path) ? path : null;
        }
        catch (Exception)
        {
            // A disk that stopped answering is a card with no picture on it, not an exception out
            // of a property read on the way to drawing a screen.
            return null;
        }
    }

    public ProfileMediaOutcome Save(ProfilePictureKind kind, string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        try
        {
            var file = new FileInfo(sourcePath);

            if (!file.Exists)
            {
                return new ProfileMediaOutcome(PictureVerdict.Unreadable, null);
            }

            // Length first, so four megabytes of something that was never going to be accepted is
            // never read at all.
            if (file.Length > ProfilePictureRules.MaxBytes)
            {
                return new ProfileMediaOutcome(PictureVerdict.TooLarge, null);
            }

            using var stream = File.OpenRead(sourcePath);

            var head = new byte[(int)Math.Min(file.Length, HeadLength)];
            var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);

            var verdict = ProfilePictureRules.Check(kind, file.Length, head.AsSpan(0, read));

            if (verdict != PictureVerdict.Ok)
            {
                return new ProfileMediaOutcome(verdict, null);
            }

            var name = ProfilePictureRules.NewFileName(
                kind,
                ProfilePictureRules.Inspect(head.AsSpan(0, read)).Format);

            Directory.CreateDirectory(_folder);

            var destination = Path.Combine(_folder, name);
            var temporary = destination + ".tmp";

            // Copied through this process rather than with File.Copy, and from the handle already
            // open, so what lands in the media folder is the file that was judged. A separate copy
            // would re-open by path and could pick up something else entirely — the source is in
            // somebody's Downloads folder, where anything at all may be writing.
            stream.Position = 0;

            using (var output = File.Create(temporary))
            {
                stream.CopyTo(output);
            }

            // The same move UserProfileStore uses: a reader sees a whole file or no file.
            File.Move(temporary, destination, overwrite: true);

            return new ProfileMediaOutcome(PictureVerdict.Ok, name);
        }
        catch (Exception)
        {
            // Locked by whatever produced it, on a disk that went away, or refused by permissions.
            // None of that is the picture's fault, and none of it is worth an exception out of a
            // button press.
            return new ProfileMediaOutcome(PictureVerdict.Unreadable, null);
        }
    }

    public void Remove(string? fileName)
    {
        if (!ProfilePictureRules.IsStoredFileName(fileName))
        {
            return;
        }

        try
        {
            var path = Path.Combine(_folder, fileName!);

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // The profile has already stopped pointing at it by the time this runs. A file left
            // behind is wasted space, not a fault worth reporting to anybody.
        }
    }
}
