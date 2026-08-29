using System.Text.Json;
using System.Text.Json.Serialization;
using Winora.Core.Profile;
using Winora.Infrastructure.Paths;

namespace Winora.Infrastructure.Profile;

/// <summary>Where the four fields live.</summary>
public interface IUserProfileStore
{
    /// <summary>The stored profile, or null when there is not a usable one.</summary>
    UserProfile? Read();

    /// <summary>Stores the profile. False when it could not be written.</summary>
    bool Write(UserProfile profile);
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// Plain JSON written to a temporary file and moved into place, rather than
/// <c>AtomicJsonFile</c>, which the rest of the state uses. That type carries schema versions,
/// digests and an authoritative-versus-projection distinction, all of which exist because losing
/// the journal or a backup record would leave a machine changed with no way back. Losing a name
/// and an avatar means being asked for them again. Borrowing that machinery here would suggest
/// this file matters as much as those, and it does not.
/// </para>
/// <para>
/// The move is still atomic: a reader sees either the old file or the new one, never a half-written
/// one. That much is worth having for a file written while the app is running.
/// </para>
/// </remarks>
public sealed class UserProfileStore : IUserProfileStore
{
    private const string FileName = "profile.json";

    /// <summary>
    /// The current shape of profile.json.
    /// </summary>
    /// <remarks>
    /// Version 3 added the two picture file names. Nothing about it makes an older file unusable,
    /// so unlike version 2 it is not a bar to reading one — see <see cref="ReadableSchemaVersion"/>.
    /// </remarks>
    private const int CurrentSchemaVersion = 3;

    /// <summary>
    /// The oldest file this store will still read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Version 2 is where the password arrived, and the password is what makes a file a profile: a
    /// version 1 file was written before registration existed, so it describes somebody who never
    /// registered, and it reads as no profile at all.
    /// </para>
    /// <para>
    /// Version 3 only added two optional picture names. A version 2 file is a complete, registered
    /// profile that simply has no pictures yet, and discarding it would cost its owner their name,
    /// their address, their joining date and their password over a feature they have not used.
    /// The check is therefore against this floor and not against
    /// <see cref="CurrentSchemaVersion"/> — which is what it used to be, and which would have
    /// thrown every existing profile away the moment the version was raised.
    /// </para>
    /// </remarks>
    private const int ReadableSchemaVersion = 2;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true,
    };

    private readonly string _directory;

    public UserProfileStore()
        : this(WinoraDataPaths.RootForCurrentUser())
    {
    }

    public UserProfileStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
    }

    private string Path => global::System.IO.Path.Combine(_directory, FileName);

    public UserProfile? Read()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return null;
            }

            var text = File.ReadAllText(Path);
            var stored = JsonSerializer.Deserialize<StoredProfile>(text, Options);

            if (stored is null)
            {
                return null;
            }

            var normalisedName = ProfileRules.NormaliseName(stored.Name);

            // A profile with no name has nothing for the card to show and nothing for the initial
            // to take, so it is not one. The welcome window asks again.
            if (string.IsNullOrEmpty(normalisedName))
            {
                return null;
            }

            // If the name is too long but present, truncate it to fit rather than discard
            // the entire profile. The stored name may come from a hand edit or a looser build.
            var finalName = normalisedName.Length > ProfileRules.NameMaxLength
                ? normalisedName.Substring(0, ProfileRules.NameMaxLength)
                : normalisedName;

            // A name and a readable schema are the whole test now. The password used to be part of
            // it, and that was the wrong bar twice over: it threw away a perfectly good profile
            // whose hash had gone missing, and it made a stored secret load-bearing for a check
            // nothing ever performed.
            if (stored.SchemaVersion < ReadableSchemaVersion)
            {
                return null;
            }

            var profile = new UserProfile(
                finalName,
                stored.Email?.Trim() ?? string.Empty,
                stored.Avatar,
                stored.CreatedUtc,

                // A name that is not one this program generates is dropped, not obeyed. It reaches
                // here from a text file anybody can edit, and joining "..\..\Windows\x.png" to the
                // media folder resolves somewhere else entirely — which an elevated program must
                // never follow. A dropped name is a card with the drawn mark on it, nothing worse.
                Safe(stored.AvatarFile),
                Safe(stored.BackgroundFile));

            // A file written by an older build still carries the password hash and its salt.
            // Dropping the fields from the shape above stops them being read, but it does not take
            // them off the disk — and a secret that no longer does anything is worse lying about
            // than deleted, because nothing will ever remove it. Rewriting here, once, is what
            // actually removes it.
            if (HadStoredPassword(text))
            {
                Write(profile);
            }

            return profile;
        }
        catch (Exception)
        {
            // Unreadable, half-written, or edited by hand into something that is not JSON. All of
            // it means the same thing to everybody upstream: there is no profile yet.
            return null;
        }
    }

    /// <summary>
    /// Whether the file on disk was written before the password was taken out.
    /// </summary>
    /// <remarks>
    /// Asked of the raw text rather than of the parsed shape, because the parsed shape no longer
    /// has anywhere to put the answer. The property name is what the camel-case policy writes, and
    /// it is matched without regard to case so a hand-edited file is caught too.
    /// </remarks>
    public static bool HadStoredPassword(string? text) =>
        text is not null &&
        text.Contains("passwordHash", StringComparison.OrdinalIgnoreCase);

    public bool Write(UserProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        try
        {
            var temporary = Path + ".tmp";

            Directory.CreateDirectory(_directory);

            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(
                    new StoredProfile(
                        CurrentSchemaVersion,
                        profile.Name,
                        profile.Email,
                        profile.Avatar,
                        profile.CreatedUtc,
                        Safe(profile.AvatarFile),
                        Safe(profile.BackgroundFile)),
                    Options));

            File.Move(temporary, Path, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            TryDelete(Path + ".tmp");
            return false;
        }
    }

    /// <summary>A picture file name, or null if it is not one this program could have written.</summary>
    /// <remarks>
    /// Applied on the way in and on the way out. On the way in because the file is editable text;
    /// on the way out so that a name that somehow got past the layer above is not written back and
    /// made to look legitimate.
    /// </remarks>
    private static string? Safe(string? fileName) =>
        ProfilePictureRules.IsStoredFileName(fileName) ? fileName : null;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Nothing depends on it going, and nothing here is worth an exception.
        }
    }

    /// <remarks>
    /// The three password fields are gone from this shape and are not read. A file written by an
    /// older build still holds them; unknown properties are ignored on the way in, and the first
    /// save writes the file without them. See <see cref="HadStoredPassword"/> for why that save is
    /// made to happen immediately rather than waited for.
    /// </remarks>
    private sealed record StoredProfile(
        int SchemaVersion,
        string? Name,
        string? Email,
        int Avatar,
        DateTimeOffset CreatedUtc,
        string? AvatarFile = null,
        string? BackgroundFile = null);
}
