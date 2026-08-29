using Winora.Core.Profile;
using Winora.Infrastructure.Profile;

namespace Winora.App.Services;

/// <param name="Colour">Already resolved, so the view model never asks how a colour is chosen.</param>
/// <param name="Initial">The one letter the mark shows.</param>
/// <param name="AvatarImagePath">
/// The full path of the stored avatar picture, or empty when the drawn mark is what to show.
/// </param>
/// <param name="BackgroundImagePath">The card's background picture, on the same terms.</param>
/// <remarks>
/// The two paths are resolved here, not stored here: what the profile holds is a file name, and
/// turning that into a path — and finding that the file is no longer there — is the media store's
/// business. A missing file arrives as an empty string, which is the same thing to everything above
/// as never having chosen one.
/// </remarks>
public sealed record ProfileView(
    string Name,
    string Email,
    int Avatar,
    DateTimeOffset CreatedUtc,
    string Colour,
    string Initial,
    string AvatarImagePath = "",
    string BackgroundImagePath = "");

/// <summary>The profile, and the numbers the card shows beside it.</summary>
public interface IProfileService
{
    /// <summary>The stored profile, or null when nobody has introduced themselves yet.</summary>
    ProfileView? Current { get; }

    /// <summary>What to put in the name field before anybody types: the Windows account name.</summary>
    string SuggestedName { get; }

    /// <summary>The colours the picker offers. Resolved here so the view model need not know how.</summary>
    IReadOnlyList<string> Palette { get; }

    /// <summary>Stores the profile. False when it could not be written.</summary>
    bool Save(string name, string email, int avatar);

    /// <summary>Creates the profile the registration window filled in.</summary>
    bool Register(string name, string email);

    /// <summary>
    /// Checks the file the person picked, keeps a copy of it, and points the profile at the copy.
    /// </summary>
    /// <returns>
    /// Ok, or the one rule the file broke. Every other value has its own sentence on screen, which
    /// is why the whole verdict comes back rather than a bare false.
    /// </returns>
    PictureVerdict SetPicture(ProfilePictureKind kind, string sourcePath);

    /// <summary>
    /// Takes the picture back off, leaving the drawn mark.
    /// </summary>
    /// <remarks>
    /// There has to be a way back. Somebody who picks a photograph that turns out to look wrong at
    /// ninety-six points must not be left with it because the only way to change it is to pick
    /// another one.
    /// </remarks>
    bool RemovePicture(ProfilePictureKind kind);

    /// <summary>How many actions the journal holds for this person.</summary>
    Task<int> RecordedChangesAsync();
}

/// <inheritdoc />
/// <remarks>
/// Exists because view models may not reach into Winora.Core or Winora.Infrastructure directly —
/// see SolutionStructureTests. The same shape as BypassService and AppUpdateService: translate at
/// the boundary, hand the layer above only what it needs to show.
/// </remarks>
public sealed class ProfileService : IProfileService
{
    private readonly IUserProfileStore _store;
    private readonly IProfileMediaStore _media;
    private readonly IActionJournalReader _journal;

    public ProfileService(IUserProfileStore store, IProfileMediaStore media, IActionJournalReader journal)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _media = media ?? throw new ArgumentNullException(nameof(media));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    }

    public ProfileView? Current
    {
        get
        {
            var stored = _store.Read();

            return stored is null
                ? null
                : new ProfileView(
                    stored.Name,
                    stored.Email,
                    stored.Avatar,
                    stored.CreatedUtc,

                    // Resolved even when a picture is set. The colour is the fallback and the
                    // fallback is never thrown away — a picture that is removed, or that goes
                    // missing, must come back to the same colour it had before, not to a new one.
                    ProfileAvatar.ColourFor(stored.Name, stored.Avatar),
                    ProfileAvatar.InitialFor(stored.Name),
                    _media.PathFor(stored.AvatarFile) ?? string.Empty,
                    _media.PathFor(stored.BackgroundFile) ?? string.Empty);
        }
    }

    /// <remarks>
    /// The Windows account name, which is nearly always what the person would type anyway. Offered,
    /// never imposed: the field is editable and the whole window is skippable.
    /// </remarks>
    public string SuggestedName
    {
        get
        {
            try
            {
                return ProfileRules.NormaliseName(Environment.UserName);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }

    public IReadOnlyList<string> Palette => ProfileAvatar.Palette;

    public bool Save(string name, string email, int avatar)
    {
        var trimmed = ProfileRules.NormaliseName(name);

        if (!ProfileRules.IsNameValid(trimmed) || !ProfileRules.IsEmailValid(email))
        {
            return false;
        }

        var existing = _store.Read();

        // Save edits a profile; it does not create one. When there is nothing to read — no profile
        // yet, a transiently unreadable file, or a read that lost a race with another writer —
        // writing anyway would invent one from a half-filled form and report success while doing
        // it. Registration is the only thing that creates a profile.
        if (existing is null)
        {
            return false;
        }

        // Built from what is already stored rather than from a fresh record, so that every field
        // this method does not edit survives it. Constructing a new UserProfile here would have
        // silently dropped both picture names the moment somebody corrected a typo in their name.
        return _store.Write(
            existing with
            {
                Name = trimmed,
                Email = email?.Trim() ?? string.Empty,
                Avatar = avatar,
            });
    }

    /// <summary>Creates the profile the registration window fills in.</summary>
    /// <remarks>
    /// The one place a profile comes into being. It used to take a password and hash it; there was
    /// nothing that ever checked the result, so it took a secret, stored it on disk and protected
    /// nothing with it.
    /// </remarks>
    public bool Register(string name, string email)
    {
        var trimmed = ProfileRules.NormaliseName(name);

        if (!ProfileRules.IsNameValid(trimmed) || !ProfileRules.IsEmailValid(email))
        {
            return false;
        }

        return _store.Write(
            new UserProfile(
                trimmed,
                email?.Trim() ?? string.Empty,
                ProfileAvatar.FromName,
                DateTimeOffset.UtcNow));
    }

    /// <inheritdoc />
    /// <remarks>
    /// The order is the whole of the care here. Copy in, point the profile at the copy, and only
    /// then delete what the profile used to point at. Deleting first would lose the old picture to
    /// a write that then failed, and a person who tried to change their avatar would end up with
    /// neither. If the write fails the fresh copy is taken back out, so a failure leaves the media
    /// folder exactly as it was rather than accumulating files nothing refers to.
    /// </remarks>
    public PictureVerdict SetPicture(ProfilePictureKind kind, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return PictureVerdict.Unreadable;
        }

        var existing = _store.Read();

        // The same guard Save has, for the same reason: a profile is created by registration and
        // by nothing else, and writing one from here would store an empty digest.
        if (existing is null)
        {
            return PictureVerdict.NotStored;
        }

        var outcome = _media.Save(kind, sourcePath);

        if (outcome.Verdict != PictureVerdict.Ok || outcome.FileName is null)
        {
            return outcome.Verdict == PictureVerdict.Ok ? PictureVerdict.NotStored : outcome.Verdict;
        }

        var previous = kind == ProfilePictureKind.Avatar ? existing.AvatarFile : existing.BackgroundFile;

        var updated = kind == ProfilePictureKind.Avatar
            ? existing with { AvatarFile = outcome.FileName }
            : existing with { BackgroundFile = outcome.FileName };

        if (!_store.Write(updated))
        {
            _media.Remove(outcome.FileName);
            return PictureVerdict.NotStored;
        }

        _media.Remove(previous);
        return PictureVerdict.Ok;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Same order, same reason: the profile stops pointing at the file before the file goes. A
    /// delete that succeeded followed by a write that did not would leave the card pointing at
    /// nothing — which draws the initial, so it is survivable, but it is still a lie on disk.
    /// </remarks>
    public bool RemovePicture(ProfilePictureKind kind)
    {
        var existing = _store.Read();

        if (existing is null)
        {
            return false;
        }

        var previous = kind == ProfilePictureKind.Avatar ? existing.AvatarFile : existing.BackgroundFile;

        if (previous is null)
        {
            return true;
        }

        var updated = kind == ProfilePictureKind.Avatar
            ? existing with { AvatarFile = null }
            : existing with { BackgroundFile = null };

        if (!_store.Write(updated))
        {
            return false;
        }

        _media.Remove(previous);
        return true;
    }

    public async Task<int> RecordedChangesAsync()
    {
        try
        {
            return (await _journal.ReadAsync().ConfigureAwait(true)).Count;
        }
        catch (Exception)
        {
            // A number that cannot be read is shown as none rather than taking the card down.
            return 0;
        }
    }
}
