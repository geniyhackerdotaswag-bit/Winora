using Winora.Core.Profile;
using Winora.Infrastructure.Profile;

namespace Winora.App.Services;

/// <param name="Colour">Already resolved, so the view model never asks how a colour is chosen.</param>
/// <param name="Initial">The one letter the mark shows.</param>
public sealed record ProfileView(
    string Name,
    string Email,
    int Avatar,
    DateTimeOffset CreatedUtc,
    string Colour,
    string Initial);

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
    private readonly IActionJournalReader _journal;

    public ProfileService(IUserProfileStore store, IActionJournalReader journal)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
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
                    ProfileAvatar.ColourFor(stored.Name, stored.Avatar),
                    ProfileAvatar.InitialFor(stored.Name));
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
        // writing anyway would store an empty digest, and the file would then fail Read's own guard
        // on the next launch. That costs the person the whole profile, not just the password, and
        // reports success while doing it. Registration is the only thing that creates a profile.
        if (existing?.Password is null)
        {
            return false;
        }

        return _store.Write(
            new UserProfile(
                trimmed,
                email?.Trim() ?? string.Empty,
                avatar,
                existing.CreatedUtc,
                existing.Password));
    }

    /// <summary>Creates the profile the registration window fills in.</summary>
    /// <remarks>
    /// The one place a profile comes into being. The password is hashed here rather than by a
    /// caller, so nothing above this layer ever holds the plain text longer than the keystroke
    /// that produced it. Not on <see cref="IProfileService"/> yet — the registration window that
    /// reaches it through the interface is a later task; it exists now so <see cref="Save"/> has a
    /// real digest to carry across.
    /// </remarks>
    public bool Register(string name, string email, string password)
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
                DateTimeOffset.UtcNow,
                PasswordHash.Create(password)));
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
