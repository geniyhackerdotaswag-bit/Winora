# Приветствие и личный кабинет — план работ

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Winora представляется при первом запуске, спрашивает имя, почту и аватар, и дальше показывает карточку человека в отдельном разделе и на «Главной».

**Architecture:** Четыре поля в `profile.json` рядом с журналом. Правила полей и цвет аватара — чистые функции в `Winora.Core`, полностью проверяемые. Чтение и запись — в `Winora.Infrastructure`. Окно приветствия, кабинет и карточка — в `Winora.App`. Карточка одна на два места.

**Tech Stack:** .NET 10, WinUI 3, `System.Text.Json`, CommunityToolkit.Mvvm, xunit.

**Спецификация:** `docs/superpowers/specs/2026-08-24-winora-profile-design.md`

## Global Constraints

- `net10.0-windows10.0.26100.0`, `x64`, `LangVersion 14.0`, `Nullable enable`, `TreatWarningsAsErrors=true` — **предупреждение ломает сборку**.
- Комментарии в коде по-английски. Всё, что видно на экране, — только в `src/Winora.App/Strings/ru-RU/Resources.resw`, никогда в коде.
- **Модели представления не обращаются к `Winora.System` и `Winora.Infrastructure` напрямую** — это проверяет `tests/Winora.Architecture.Tests/SolutionStructureTests.cs:84`. Прослойка в `Winora.App/Services/`, как `BypassService` и `AppUpdateService`.
- Явные квалификаторы `System.` не компилируются под этим деревом имён: `Winora.System` затеняет глобальный `System`. Писать `global::System.` или `using`.
- Наблюдаемые свойства только в виде `[ObservableProperty] public partial ... { get; set; }` — иначе MVVMTK0045.
- Файл `Resources.resw` держит записи **в одну строку**, без `xml:space`. Дописывать в том же виде.
- Хранилище: `%USERPROFILE%\Winora\State\profile.json`, через `WinoraDataPaths.RootForCurrentUser()`.
- **Профиль никогда не мешает приложению работать.** Любая неудача чтения или записи — это «профиля нет», а не отказ запуска.
- Сейчас проходит 1279 тестов в пяти проектах: `dotnet test --nologo`. Ни один не должен сломаться.

## Файловая карта

| Файл | За что отвечает |
|---|---|
| `src/Winora.Core/Profile/UserProfile.cs` | четыре поля, правила имени и почты |
| `src/Winora.Core/Profile/ProfileAvatar.cs` | цвет по имени, набор оттенков |
| `src/Winora.Infrastructure/Profile/UserProfileStore.cs` | чтение и запись `profile.json` |
| `src/Winora.App/Services/ProfileService.cs` | прослойка: профиль плюс числа из журнала |
| `src/Winora.App/ViewModels/ProfileViewModel.cs` | кабинет и карточка |
| `src/Winora.App/Controls/ProfileCard.xaml` | карточка, одна на кабинет и «Главную» |
| `src/Winora.App/Views/ProfilePage.xaml` | сам кабинет |
| `src/Winora.App/Views/WelcomeDialog.xaml` | окно первого запуска |

---

### Task 1: Правила полей

**Files:**
- Create: `src/Winora.Core/Profile/UserProfile.cs`
- Test: `tests/Winora.Core.Tests/Profile/UserProfileTests.cs`

**Interfaces:**
- Consumes: ничего.
- Produces:
  - `sealed record UserProfile(string Name, string Email, int Avatar, DateTimeOffset CreatedUtc)`
  - `static class ProfileRules` с `const int NameMaxLength = 32;`, `static bool IsNameValid(string? name)`, `static bool IsEmailValid(string? email)`, `static string NormaliseName(string? name)`

- [ ] **Step 1: Написать падающий тест**

Создать `tests/Winora.Core.Tests/Profile/UserProfileTests.cs`:

```csharp
using Winora.Core.Profile;
using Xunit;

namespace Winora.Core.Tests.Profile;

/// <summary>
/// What the welcome form will and will not accept.
/// </summary>
/// <remarks>
/// The email rule is deliberately shallow: it checks the shape and nothing else. Whether a mailbox
/// exists cannot be established without a server, and this program has none — a check that looked
/// deeper would be pretending to know something it cannot.
/// </remarks>
public sealed class UserProfileTests
{
    [Theory]
    [InlineData("Аня")]
    [InlineData("a")]
    [InlineData("Пользователь Windows")]
    public void A_reasonable_name_is_accepted(string name)
    {
        Assert.True(ProfileRules.IsNameValid(name));
    }

    [Fact]
    public void A_name_of_exactly_the_limit_is_accepted()
    {
        Assert.True(ProfileRules.IsNameValid(new string('a', ProfileRules.NameMaxLength)));
    }

    [Fact]
    public void A_name_one_over_the_limit_is_not()
    {
        Assert.False(ProfileRules.IsNameValid(new string('a', ProfileRules.NameMaxLength + 1)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Nothing_is_not_a_name(string? name)
    {
        Assert.False(ProfileRules.IsNameValid(name));
    }

    /// <summary>Surrounding space is the person's typing, not their name.</summary>
    [Fact]
    public void A_name_is_trimmed_before_it_is_judged_or_kept()
    {
        Assert.True(ProfileRules.IsNameValid("  Аня  "));
        Assert.Equal("Аня", ProfileRules.NormaliseName("  Аня  "));
        Assert.Equal(string.Empty, ProfileRules.NormaliseName(null));
    }

    /// <summary>Empty is allowed: the email is optional and always was.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a@b.ru")]
    [InlineData("very.long.name+tag@mail.example.com")]
    public void An_acceptable_email(string? email)
    {
        Assert.True(ProfileRules.IsEmailValid(email));
    }

    [Theory]
    [InlineData("a@")]
    [InlineData("@b.ru")]
    [InlineData("ab.ru")]
    [InlineData("a@b")]
    [InlineData("a b@c.ru")]
    [InlineData("a@@b.ru")]
    public void An_email_that_is_not_one(string email)
    {
        Assert.False(ProfileRules.IsEmailValid(email));
    }
}
```

- [ ] **Step 2: Убедиться, что тест падает**

```bash
dotnet test tests/Winora.Core.Tests/Winora.Core.Tests.csproj --nologo --filter "FullyQualifiedName~UserProfileTests"
```

Ожидаемо: `CS0234` — пространства имён `Winora.Core.Profile` не существует.

- [ ] **Step 3: Написать**

Создать `src/Winora.Core/Profile/UserProfile.cs`:

```csharp
namespace Winora.Core.Profile;

/// <param name="Name">What to call this person. Never empty once stored.</param>
/// <param name="Email">Optional, and it stays on this machine. May be empty.</param>
/// <param name="Avatar">Index into the palette, or -1 for "work it out from the name".</param>
/// <param name="CreatedUtc">When the introduction happened.</param>
public sealed record UserProfile(string Name, string Email, int Avatar, DateTimeOffset CreatedUtc);

/// <summary>What the welcome form accepts.</summary>
public static class ProfileRules
{
    /// <summary>Long enough for a real name, short enough to fit the card.</summary>
    public const int NameMaxLength = 32;

    /// <summary>Trimmed, because surrounding space is typing rather than a name.</summary>
    public static string NormaliseName(string? name) => name?.Trim() ?? string.Empty;

    public static bool IsNameValid(string? name)
    {
        var trimmed = NormaliseName(name);
        return trimmed.Length is > 0 and <= NameMaxLength;
    }

    /// <summary>
    /// Whether this looks like an email address.
    /// </summary>
    /// <remarks>
    /// Shape only: something before the single @, something after it, and a dot inside a domain
    /// that does not start or end with one. Whether the mailbox exists cannot be established
    /// without sending to it, and this program has no server and sends nothing — a check that
    /// looked deeper would only be pretending. Empty passes: the field is optional.
    /// </remarks>
    public static bool IsEmailValid(string? email)
    {
        var trimmed = email?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            return true;
        }

        if (trimmed.Any(char.IsWhiteSpace))
        {
            return false;
        }

        var parts = trimmed.Split('@');
        if (parts.Length != 2 || parts[0].Length == 0)
        {
            return false;
        }

        var domain = parts[1];
        return domain.Length >= 3 &&
               domain.Contains('.') &&
               !domain.StartsWith('.') &&
               !domain.EndsWith('.');
    }
}
```

- [ ] **Step 4: Убедиться, что тесты проходят**

```bash
dotnet test tests/Winora.Core.Tests/Winora.Core.Tests.csproj --nologo --filter "FullyQualifiedName~UserProfileTests"
```

Ожидаемо: все зелёные.

- [ ] **Step 5: Коммит**

```bash
git add src/Winora.Core/Profile/UserProfile.cs tests/Winora.Core.Tests/Profile/UserProfileTests.cs
git commit -m "feat(profile): decide what the welcome form accepts"
```

---

### Task 2: Цвет аватара

**Files:**
- Create: `src/Winora.Core/Profile/ProfileAvatar.cs`
- Test: `tests/Winora.Core.Tests/Profile/ProfileAvatarTests.cs`

**Interfaces:**
- Consumes: `ProfileRules.NormaliseName` из задачи 1.
- Produces: `static class ProfileAvatar` с `static IReadOnlyList<string> Palette { get; }` (шесть значений `#RRGGBB`), `const int FromName = -1;`, `static string ColourFor(string? name, int avatar)`, `static string InitialFor(string? name)`

- [ ] **Step 1: Написать падающий тест**

Создать `tests/Winora.Core.Tests/Profile/ProfileAvatarTests.cs`:

```csharp
using Winora.Core.Profile;
using Xunit;

namespace Winora.Core.Tests.Profile;

/// <summary>
/// The drawn mark: one letter and a colour.
/// </summary>
/// <remarks>
/// Drawn rather than shipped as pictures — nothing to weigh, nobody else's artwork to license, and
/// no blur at any size. The colour has to be stable: a person whose mark changed colour between
/// launches would reasonably wonder what else the program forgets.
/// </remarks>
public sealed class ProfileAvatarTests
{
    [Fact]
    public void The_same_name_always_gets_the_same_colour()
    {
        var first = ProfileAvatar.ColourFor("Аня", ProfileAvatar.FromName);
        var second = ProfileAvatar.ColourFor("Аня", ProfileAvatar.FromName);

        Assert.Equal(first, second);
    }

    [Fact]
    public void A_chosen_colour_wins_over_the_derived_one()
    {
        Assert.Equal(ProfileAvatar.Palette[2], ProfileAvatar.ColourFor("Аня", 2));
    }

    [Fact]
    public void The_derived_colour_is_always_one_from_the_palette()
    {
        foreach (var name in new[] { "Аня", "Bob", "x", "Пользователь Windows", "12345" })
        {
            Assert.Contains(ProfileAvatar.ColourFor(name, ProfileAvatar.FromName), ProfileAvatar.Palette);
        }
    }

    /// <summary>An index from a future version, or a corrupt file, must not crash the card.</summary>
    [Theory]
    [InlineData(-5)]
    [InlineData(99)]
    public void An_index_outside_the_palette_falls_back_to_the_name(int avatar)
    {
        Assert.Equal(
            ProfileAvatar.ColourFor("Аня", ProfileAvatar.FromName),
            ProfileAvatar.ColourFor("Аня", avatar));
    }

    [Theory]
    [InlineData("Аня", "А")]
    [InlineData("bob", "B")]
    [InlineData("  пётр  ", "П")]
    public void The_initial_is_the_first_letter_in_capitals(string name, string expected)
    {
        Assert.Equal(expected, ProfileAvatar.InitialFor(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Without_a_name_there_is_still_something_to_draw(string? name)
    {
        Assert.False(string.IsNullOrEmpty(ProfileAvatar.InitialFor(name)));
        Assert.Contains(ProfileAvatar.ColourFor(name, ProfileAvatar.FromName), ProfileAvatar.Palette);
    }
}
```

- [ ] **Step 2: Убедиться, что тест падает**

```bash
dotnet test tests/Winora.Core.Tests/Winora.Core.Tests.csproj --nologo --filter "FullyQualifiedName~ProfileAvatarTests"
```

Ожидаемо: `CS0103` — `ProfileAvatar` не найден.

- [ ] **Step 3: Написать**

Создать `src/Winora.Core/Profile/ProfileAvatar.cs`:

```csharp
using System.Globalization;

namespace Winora.Core.Profile;

/// <summary>The drawn mark: one letter on a coloured circle.</summary>
/// <remarks>
/// Drawn rather than shipped as images: nothing to weigh, no third-party artwork to account for,
/// and no blur at any size. The card needs a mark at 32 px and at 96 px from the same source.
/// </remarks>
public static class ProfileAvatar
{
    /// <summary>Stored in place of a chosen colour, meaning "work it out from the name".</summary>
    public const int FromName = -1;

    /// <summary>Shown when there is no name at all to take a letter from.</summary>
    private const string FallbackInitial = "?";

    public static IReadOnlyList<string> Palette { get; } =
    [
        "#7C6BF5",
        "#3FA9F5",
        "#2FBF9E",
        "#E0913A",
        "#D9536F",
        "#8E7CC3",
    ];

    /// <summary>The colour for this person: chosen if they chose one, derived otherwise.</summary>
    /// <remarks>
    /// An index the palette does not contain — from a corrupt file, or from a future version with
    /// more colours — falls back to the derived colour rather than throwing. A card is decoration,
    /// and decoration must not be able to stop a screen from drawing.
    /// </remarks>
    public static string ColourFor(string? name, int avatar)
    {
        if (avatar >= 0 && avatar < Palette.Count)
        {
            return Palette[avatar];
        }

        return Palette[Bucket(ProfileRules.NormaliseName(name))];
    }

    public static string InitialFor(string? name)
    {
        var trimmed = ProfileRules.NormaliseName(name);

        return trimmed.Length == 0
            ? FallbackInitial
            : char.ToUpper(trimmed[0], CultureInfo.CurrentCulture).ToString();
    }

    /// <summary>
    /// Which colour a name lands on.
    /// </summary>
    /// <remarks>
    /// A plain sum of code points, not a cryptographic hash and not <c>string.GetHashCode</c>. The
    /// second is randomised per process in .NET Core, so the same person would get a different
    /// colour on every launch — which is exactly the thing this must not do.
    /// </remarks>
    private static int Bucket(string name)
    {
        var total = 0;
        foreach (var character in name)
        {
            total = (total + character) % Palette.Count;
        }

        return total;
    }
}
```

- [ ] **Step 4: Убедиться, что тесты проходят**

```bash
dotnet test tests/Winora.Core.Tests/Winora.Core.Tests.csproj --nologo --filter "FullyQualifiedName~ProfileAvatarTests"
```

Ожидаемо: все зелёные.

- [ ] **Step 5: Коммит**

```bash
git add src/Winora.Core/Profile/ProfileAvatar.cs tests/Winora.Core.Tests/Profile/ProfileAvatarTests.cs
git commit -m "feat(profile): draw the mark instead of shipping pictures of it"
```

---

### Task 3: Хранилище профиля

**Files:**
- Create: `src/Winora.Infrastructure/Profile/UserProfileStore.cs`
- Test: `tests/Winora.Infrastructure.Tests/Profile/UserProfileStoreTests.cs`

**Interfaces:**
- Consumes: `UserProfile` из задачи 1.
- Produces:
  - `interface IUserProfileStore { UserProfile? Read(); bool Write(UserProfile profile); }`
  - `sealed class UserProfileStore : IUserProfileStore` с конструкторами `()` и `(string directory)`

- [ ] **Step 1: Написать падающий тест**

Создать `tests/Winora.Infrastructure.Tests/Profile/UserProfileStoreTests.cs`:

```csharp
using Winora.Core.Profile;
using Winora.Infrastructure.Profile;
using Xunit;

namespace Winora.Infrastructure.Tests.Profile;

/// <summary>
/// Four fields on disk.
/// </summary>
/// <remarks>
/// Every failure here reads as "there is no profile yet", which sends the person to the welcome
/// window. That is the whole error policy: the profile is decoration, and a program that refused to
/// open because a decoration would not load is a worse program than one with no decoration.
/// </remarks>
public sealed class UserProfileStoreTests : IDisposable
{
    private readonly string _folder;

    public UserProfileStoreTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "winora-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception)
        {
            // A leftover temp folder is not worth failing a passing test over.
        }
    }

    private UserProfileStore Store() => new(_folder);

    private static UserProfile Sample() =>
        new("Аня", "anya@example.com", 2, new DateTimeOffset(2026, 8, 24, 3, 0, 0, TimeSpan.Zero));

    [Fact]
    public void A_written_profile_comes_back_whole()
    {
        var written = Sample();

        Assert.True(Store().Write(written));

        var read = Store().Read();

        Assert.NotNull(read);
        Assert.Equal(written.Name, read.Name);
        Assert.Equal(written.Email, read.Email);
        Assert.Equal(written.Avatar, read.Avatar);
        Assert.Equal(written.CreatedUtc, read.CreatedUtc);
    }

    [Fact]
    public void Without_a_file_there_is_no_profile()
    {
        Assert.Null(Store().Read());
    }

    [Fact]
    public void A_missing_folder_is_not_an_error()
    {
        Assert.Null(new UserProfileStore(Path.Combine(_folder, "absent")).Read());
    }

    /// <summary>A half-written or hand-edited file reads as "not introduced yet", never as a crash.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{")]
    [InlineData("{\"name\":\"Аня\"")]
    [InlineData("[]")]
    public void An_unreadable_file_reads_as_no_profile(string content)
    {
        File.WriteAllText(Path.Combine(_folder, "profile.json"), content);

        Assert.Null(Store().Read());
    }

    /// <summary>
    /// A profile with no name is not a profile: the card has nothing to show and the initial has
    /// nothing to take. Treated as absent so the welcome window asks again.
    /// </summary>
    [Fact]
    public void A_profile_without_a_name_reads_as_no_profile()
    {
        File.WriteAllText(
            Path.Combine(_folder, "profile.json"),
            "{\"name\":\"   \",\"email\":\"\",\"avatar\":0,\"createdUtc\":\"2026-08-24T00:00:00+00:00\"}");

        Assert.Null(Store().Read());
    }

    [Fact]
    public void Writing_twice_leaves_the_second_one()
    {
        Store().Write(Sample());
        Store().Write(Sample() with { Name = "Пётр" });

        Assert.Equal("Пётр", Store().Read()!.Name);
    }

    /// <summary>Nothing half-written is left behind beside the profile.</summary>
    [Fact]
    public void No_temporary_file_survives_a_write()
    {
        Store().Write(Sample());

        Assert.Equal(
            ["profile.json"],
            Directory.GetFiles(_folder).Select(Path.GetFileName).Order());
    }

    /// <summary>A folder that does not exist yet is created rather than refused.</summary>
    [Fact]
    public void Writing_creates_the_folder()
    {
        var nested = Path.Combine(_folder, "State");

        Assert.True(new UserProfileStore(nested).Write(Sample()));
        Assert.NotNull(new UserProfileStore(nested).Read());
    }
}
```

- [ ] **Step 2: Убедиться, что тест падает**

```bash
dotnet test tests/Winora.Infrastructure.Tests/Winora.Infrastructure.Tests.csproj --nologo --filter "FullyQualifiedName~UserProfileStoreTests"
```

Ожидаемо: `CS0234` — `Winora.Infrastructure.Profile` не существует.

- [ ] **Step 3: Написать**

Создать `src/Winora.Infrastructure/Profile/UserProfileStore.cs`:

```csharp
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

            var stored = JsonSerializer.Deserialize<StoredProfile>(File.ReadAllText(Path), Options);

            // A profile with no name has nothing for the card to show and nothing for the initial
            // to take, so it is not one. The welcome window asks again.
            if (stored is null || !ProfileRules.IsNameValid(stored.Name))
            {
                return null;
            }

            return new UserProfile(
                ProfileRules.NormaliseName(stored.Name),
                stored.Email?.Trim() ?? string.Empty,
                stored.Avatar,
                stored.CreatedUtc);
        }
        catch (Exception)
        {
            // Unreadable, half-written, or edited by hand into something that is not JSON. All of
            // it means the same thing to everybody upstream: there is no profile yet.
            return null;
        }
    }

    public bool Write(UserProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var temporary = Path + ".tmp";

        try
        {
            Directory.CreateDirectory(_directory);

            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(
                    new StoredProfile(profile.Name, profile.Email, profile.Avatar, profile.CreatedUtc),
                    Options));

            File.Move(temporary, Path, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            TryDelete(temporary);
            return false;
        }
    }

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

    private sealed record StoredProfile(
        string? Name,
        string? Email,
        int Avatar,
        DateTimeOffset CreatedUtc);
}
```

- [ ] **Step 4: Убедиться, что тесты проходят**

```bash
dotnet test tests/Winora.Infrastructure.Tests/Winora.Infrastructure.Tests.csproj --nologo --filter "FullyQualifiedName~UserProfileStoreTests"
```

Ожидаемо: все зелёные.

- [ ] **Step 5: Коммит**

```bash
git add src/Winora.Infrastructure/Profile/UserProfileStore.cs tests/Winora.Infrastructure.Tests/Profile/UserProfileStoreTests.cs
git commit -m "feat(profile): keep four fields on disk, and lose them harmlessly"
```

---

### Task 4: Прослойка и модель кабинета

**Files:**
- Create: `src/Winora.App/Services/ProfileService.cs`
- Create: `src/Winora.App/ViewModels/ProfileViewModel.cs`
- Modify: `src/Winora.App/Services/ServiceRegistration.cs`
- Modify: `src/Winora.App/Strings/ru-RU/Resources.resw`
- Test: `tests/Winora.App.Tests/ViewModels/ProfileViewModelTests.cs`

**Interfaces:**
- Consumes: `IUserProfileStore` (задача 3), `UserProfile`, `ProfileRules`, `ProfileAvatar` (задачи 1–2), `IActionJournalReader.ReadAsync(CancellationToken) → Task<IReadOnlyList<ActionRecordView>>`, `ILocalizationService.Get(string)`.
- Produces:
  - `sealed record ProfileView(string Name, string Email, int Avatar, DateTimeOffset CreatedUtc, string Colour, string Initial)`
  - `interface IProfileService { ProfileView? Current { get; } bool Save(string name, string email, int avatar); Task<int> RecordedChangesAsync(); string SuggestedName { get; } }`
  - `sealed class ProfileService : IProfileService`
  - `ProfileViewModel` со свойствами `HasProfile`, `Name`, `Email`, `Colour`, `Initial`, `MemberSince`, `RecordedChanges`, `Heading`, `EmailPrivacyNote`, `SaveLabel`, `CanSave`, `StatusMessage` и командой `SaveCommand`

- [ ] **Step 1: Добавить строки**

В `src/Winora.App/Strings/ru-RU/Resources.resw`, перед `</root>`, в том же однострочном виде, что и остальные записи:

```xml
  <data name="Nav_Profile"><value>Профиль</value></data>
  <data name="Profile_Heading"><value>Личный кабинет</value></data>
  <data name="Profile_NameLabel"><value>Имя</value></data>
  <data name="Profile_EmailLabel"><value>Почта</value></data>
  <data name="Profile_EmailPrivacy"><value>Почта остаётся на этом компьютере. Winora ничего никуда не отправляет.</value></data>
  <data name="Profile_AvatarLabel"><value>Значок</value></data>
  <data name="Profile_Save"><value>Сохранить</value></data>
  <data name="Profile_Saved"><value>Сохранено.</value></data>
  <data name="Profile_SaveFailed"><value>Не удалось сохранить профиль. Остальное работает.</value></data>
  <data name="Profile_MemberSince"><value>С нами с {0}</value></data>
  <data name="Profile_RecordedChanges"><value>Изменений записано: {0}</value></data>
  <data name="Welcome_Title"><value>Знакомство</value></data>
  <data name="Welcome_About"><value>Winora меняет оформление Windows: темы, панель задач, курсоры и звуки. Ещё она умеет обходить блокировки Discord и YouTube.</value></data>
  <data name="Welcome_NameHint"><value>Как к вам обращаться</value></data>
  <data name="Welcome_Start"><value>Начать</value></data>
  <data name="Welcome_Skip"><value>Пропустить</value></data>
```

Строка `App_Safety_Statement` уже есть в файле и в окне приветствия используется как есть — заново её не добавлять.

- [ ] **Step 2: Написать падающий тест**

Создать `tests/Winora.App.Tests/ViewModels/ProfileViewModelTests.cs`:

```csharp
using Winora.App.Services;
using Winora.App.ViewModels;
using Xunit;

namespace Winora.App.Tests.ViewModels;

/// <summary>
/// The cabinet: who you are, and what the program has recorded of what you did.
/// </summary>
public sealed class ProfileViewModelTests
{
    private sealed class FakeProfileService : IProfileService
    {
        public ProfileView? Current { get; set; }

        public string SuggestedName { get; init; } = "brawl";

        public bool SaveSucceeds { get; init; } = true;

        public (string Name, string Email, int Avatar)? LastSaved { get; private set; }

        public bool Save(string name, string email, int avatar)
        {
            LastSaved = (name, email, avatar);

            if (!SaveSucceeds)
            {
                return false;
            }

            Current = new ProfileView(name, email, avatar, DateTimeOffset.UnixEpoch, "#7C6BF5", "А");
            return true;
        }

        public Task<int> RecordedChangesAsync() => Task.FromResult(7);
    }

    private sealed class EchoLocalization : ILocalizationService
    {
        public bool IsAvailable => true;

        // The key comes back, except where a test needs a real format template.
        public string Get(string resourceKey) => resourceKey switch
        {
            "Profile_MemberSince" => "с {0}",
            "Profile_RecordedChanges" => "записано {0}",
            _ => resourceKey,
        };
    }

    private static ProfileViewModel Build(IProfileService service) =>
        new(service, new EchoLocalization());

    [Fact]
    public void Without_a_profile_there_is_nothing_to_show()
    {
        var vm = Build(new FakeProfileService());
        vm.Load();

        Assert.False(vm.HasProfile);
    }

    [Fact]
    public void An_existing_profile_fills_the_card()
    {
        var service = new FakeProfileService
        {
            Current = new ProfileView(
                "Аня", "anya@example.com", 2,
                new DateTimeOffset(2026, 8, 24, 0, 0, 0, TimeSpan.Zero), "#2FBF9E", "А"),
        };

        var vm = Build(service);
        vm.Load();

        Assert.True(vm.HasProfile);
        Assert.Equal("Аня", vm.Name);
        Assert.Equal("anya@example.com", vm.Email);
        Assert.Equal("#2FBF9E", vm.Colour);
        Assert.Equal("А", vm.Initial);
        Assert.Contains("2026", vm.MemberSince);
    }

    /// <summary>The button follows the rules, so a bad name cannot be saved by pressing harder.</summary>
    [Theory]
    [InlineData("", "", false)]
    [InlineData("   ", "", false)]
    [InlineData("Аня", "", true)]
    [InlineData("Аня", "a@b.ru", true)]
    [InlineData("Аня", "a@", false)]
    [InlineData("Аня", "ab.ru", false)]
    public void Saving_is_offered_only_for_a_valid_pair(string name, string email, bool expected)
    {
        var vm = Build(new FakeProfileService());
        vm.Load();
        vm.Name = name;
        vm.Email = email;

        Assert.Equal(expected, vm.CanSave);
    }

    [Fact]
    public void Saving_passes_the_trimmed_name_through()
    {
        var service = new FakeProfileService();
        var vm = Build(service);
        vm.Load();
        vm.Name = "  Аня  ";
        vm.Email = "  a@b.ru ";

        vm.SaveCommand.Execute(null);

        Assert.Equal(("Аня", "a@b.ru", ProfileViewModel.NoAvatarChosen), service.LastSaved);
    }

    [Fact]
    public void A_failed_save_says_so_and_changes_nothing()
    {
        var service = new FakeProfileService { SaveSucceeds = false };
        var vm = Build(service);
        vm.Load();
        vm.Name = "Аня";

        vm.SaveCommand.Execute(null);

        Assert.Equal("Profile_SaveFailed", vm.StatusMessage);
        Assert.False(vm.HasProfile);
    }

    [Fact]
    public void A_successful_save_says_so()
    {
        var vm = Build(new FakeProfileService());
        vm.Load();
        vm.Name = "Аня";

        vm.SaveCommand.Execute(null);

        Assert.Equal("Profile_Saved", vm.StatusMessage);
        Assert.True(vm.HasProfile);
    }

    /// <summary>The line under the email field is not optional; it is the honest part of the form.</summary>
    [Fact]
    public void The_email_privacy_note_is_present()
    {
        var vm = Build(new FakeProfileService());
        vm.Load();

        Assert.Equal("Profile_EmailPrivacy", vm.EmailPrivacyNote);
    }

    [Fact]
    public async Task The_recorded_change_count_comes_from_the_journal()
    {
        var vm = Build(new FakeProfileService());
        vm.Load();

        await vm.LoadStatisticsAsync();

        Assert.Equal("записано 7", vm.RecordedChanges);
    }
}
```

- [ ] **Step 3: Убедиться, что тест падает**

```bash
dotnet test tests/Winora.App.Tests/Winora.App.Tests.csproj --nologo --filter "FullyQualifiedName~ProfileViewModelTests"
```

Ожидаемо: `CS0246` — `IProfileService`, `ProfileView`, `ProfileViewModel` не найдены.

- [ ] **Step 4: Написать прослойку**

Создать `src/Winora.App/Services/ProfileService.cs`:

```csharp
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

    public bool Save(string name, string email, int avatar)
    {
        var trimmed = ProfileRules.NormaliseName(name);

        if (!ProfileRules.IsNameValid(trimmed) || !ProfileRules.IsEmailValid(email))
        {
            return false;
        }

        // The introduction date survives an edit: it records when this person started, not when
        // they last changed their mind about an avatar.
        var created = _store.Read()?.CreatedUtc ?? DateTimeOffset.UtcNow;

        return _store.Write(
            new UserProfile(trimmed, email?.Trim() ?? string.Empty, avatar, created));
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
```

- [ ] **Step 5: Написать модель**

Создать `src/Winora.App/ViewModels/ProfileViewModel.cs`:

```csharp
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winora.App.Services;

namespace Winora.App.ViewModels;

/// <summary>The personal cabinet: who you are, and what Winora has recorded of what you did.</summary>
public sealed partial class ProfileViewModel : ObservableObject
{
    /// <summary>
    /// Stored when nobody picked a colour, meaning "work it out from the name".
    /// </summary>
    /// <remarks>
    /// Taken from the core rule rather than written as -1 again. Two constants with the same value
    /// in two layers agree until the day one of them changes, and then they disagree silently.
    /// </remarks>
    public const int NoAvatarChosen = Winora.Core.Profile.ProfileAvatar.FromName;

    private readonly IProfileService _profile;
    private readonly ILocalizationService _text;

    public ProfileViewModel(IProfileService profile, ILocalizationService text)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _text = text ?? throw new ArgumentNullException(nameof(text));
    }

    /// <remarks>
    /// Partial properties, not fields: MVVMTK0045 requires this form in WinUI 3 so the CsWinRT
    /// generators can emit the WinRT marshalling code.
    /// </remarks>
    [ObservableProperty]
    public partial bool HasProfile { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Email { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int Avatar { get; set; } = NoAvatarChosen;

    [ObservableProperty]
    public partial string Colour { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Initial { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MemberSince { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RecordedChanges { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    public string Heading => _text.Get("Profile_Heading");

    public string NameLabel => _text.Get("Profile_NameLabel");

    public string EmailLabel => _text.Get("Profile_EmailLabel");

    /// <summary>
    /// The line under the email field.
    /// </summary>
    /// <remarks>
    /// Not optional and not small print. A form shaped like a registration that sends nothing has
    /// to say so — otherwise it is the one place in Winora where the app promises something it
    /// does not do.
    /// </remarks>
    public string EmailPrivacyNote => _text.Get("Profile_EmailPrivacy");

    public string AvatarLabel => _text.Get("Profile_AvatarLabel");

    public string SaveLabel => _text.Get("Profile_Save");

    /// <summary>The palette, so the picker does not have to know how colours are chosen.</summary>
    public IReadOnlyList<string> Palette => _profile.Palette;

    public bool CanSave =>
        Winora.Core.Profile.ProfileRules.IsNameValid(Name) &&
        Winora.Core.Profile.ProfileRules.IsEmailValid(Email);

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(CanSave));

    partial void OnEmailChanged(string value) => OnPropertyChanged(nameof(CanSave));

    public void Load()
    {
        var current = _profile.Current;
        HasProfile = current is not null;

        Name = current?.Name ?? _profile.SuggestedName;
        Email = current?.Email ?? string.Empty;
        Avatar = current?.Avatar ?? NoAvatarChosen;
        Colour = current?.Colour ?? string.Empty;
        Initial = current?.Initial ?? string.Empty;

        MemberSince = current is null
            ? string.Empty
            : string.Format(
                CultureInfo.CurrentCulture,
                _text.Get("Profile_MemberSince"),
                current.CreatedUtc.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture));
    }

    public async Task LoadStatisticsAsync()
    {
        var recorded = await _profile.RecordedChangesAsync().ConfigureAwait(true);

        RecordedChanges = string.Format(
            CultureInfo.CurrentCulture,
            _text.Get("Profile_RecordedChanges"),
            recorded);
    }

    [RelayCommand]
    private void Save()
    {
        if (!CanSave)
        {
            return;
        }

        if (!_profile.Save(Name, Email, Avatar))
        {
            StatusMessage = _text.Get("Profile_SaveFailed");
            return;
        }

        StatusMessage = _text.Get("Profile_Saved");
        Load();
    }
}
```

- [ ] **Step 6: Добавить палитру в прослойку**

`ProfileViewModel.Palette` читает `_profile.Palette`, которого в интерфейсе ещё нет. Добавить в `IProfileService`, рядом с `SuggestedName`:

```csharp
    /// <summary>The colours the picker offers. Resolved here so the view model need not know how.</summary>
    IReadOnlyList<string> Palette { get; }
```

и в `ProfileService`:

```csharp
    public IReadOnlyList<string> Palette => ProfileAvatar.Palette;
```

и в `FakeProfileService` в тесте, рядом с `SuggestedName`:

```csharp
        public IReadOnlyList<string> Palette { get; } = ["#7C6BF5", "#3FA9F5", "#2FBF9E"];
```

- [ ] **Step 7: Зарегистрировать**

В `src/Winora.App/Services/ServiceRegistration.cs`, рядом с остальными одиночками — например сразу после регистрации `IAppInstaller` — вставить:

```csharp
        // Singletons: the store is a file and the card appears in two places, which must not read
        // it into two different answers.
        services.AddSingleton<IUserProfileStore, UserProfileStore>();
        services.AddSingleton<IProfileService, ProfileService>();
        services.AddTransient<ProfileViewModel>();
```

и к `using` того же файла добавить:

```csharp
using Winora.Infrastructure.Profile;
```

- [ ] **Step 8: Убедиться, что всё проходит**

```bash
dotnet test --nologo
```

Ожидаемо: 1279 прежних плюс новые, ни одного упавшего.

- [ ] **Step 9: Коммит**

```bash
git add src/Winora.App/Services/ProfileService.cs src/Winora.App/ViewModels/ProfileViewModel.cs src/Winora.App/Services/ServiceRegistration.cs src/Winora.App/Strings/ru-RU/Resources.resw tests/Winora.App.Tests/ViewModels/ProfileViewModelTests.cs
git commit -m "feat(profile): the cabinet, and the honest line under the email field"
```

---

### Task 5: Карточка и раздел

**Files:**
- Create: `src/Winora.App/Controls/ProfileCard.xaml`
- Create: `src/Winora.App/Controls/ProfileCard.xaml.cs`
- Create: `src/Winora.App/Views/ProfilePage.xaml`
- Create: `src/Winora.App/Views/ProfilePage.xaml.cs`
- Modify: `src/Winora.App/Navigation/RouteKeys.cs`
- Modify: `src/Winora.App/Navigation/RouteRegistry.cs`
- Modify: `src/Winora.App/Navigation/PageCatalog.cs`
- Modify: `src/Winora.App/Controls/FluentIconCatalog.cs`
- Modify: `src/Winora.App/Views/DashboardPage.xaml`
- Modify: `src/Winora.App/Views/DashboardPage.xaml.cs`

**Interfaces:**
- Consumes: `ProfileViewModel` из задачи 4.
- Produces: `ProfileCard` — пользовательский элемент со свойством `ViewModel` типа `ProfileViewModel`; маршрут `RouteKeys.Profile = "profile"`.

- [ ] **Step 1: Завести маршрут**

В `src/Winora.App/Navigation/RouteKeys.cs`, к остальным константам:

```csharp
    public const string Profile = "profile";
```

и в список `All`, рядом с `Journal` и `Settings`:

```csharp
        Profile,
```

В `src/Winora.App/Navigation/RouteRegistry.cs`, в подвал панели, перед строкой с `RouteKeys.Journal`:

```csharp
        new(RouteKeys.Profile, "Nav_Profile", RoutePlacement.Footer, IconGlyphKey: "profile"),
```

В `src/Winora.App/Navigation/PageCatalog.cs`, к остальным ветвям:

```csharp
        RouteKeys.Profile => typeof(ProfilePage),
```

В `src/Winora.App/Controls/FluentIconCatalog.cs`, в словарь `Glyphs`:

```csharp
        ["profile"] = "\uE77B",
```

- [ ] **Step 2: Написать карточку**

Создать `src/Winora.App/Controls/ProfileCard.xaml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<UserControl
    x:Class="Winora.App.Controls.ProfileCard"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
    xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
    mc:Ignorable="d">

    <!--
      One control, two homes: the cabinet and the dashboard. Written once so the two cannot drift
      apart, which two copies of the same markup always eventually do.
    -->
    <Border Style="{StaticResource WinoraCard}">
        <Grid ColumnSpacing="18">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="Auto" />
                <ColumnDefinition Width="*" />
            </Grid.ColumnDefinitions>

            <!-- Drawn, not an image: nothing to ship and no blur at any size. -->
            <Grid Grid.Column="0" Width="64" Height="64">
                <Ellipse x:Name="AvatarCircle" />
                <TextBlock x:Name="AvatarInitial"
                           FontSize="28"
                           FontWeight="SemiBold"
                           Foreground="White"
                           HorizontalAlignment="Center"
                           VerticalAlignment="Center" />
            </Grid>

            <StackPanel Grid.Column="1" VerticalAlignment="Center" Spacing="4">
                <TextBlock x:Name="CardName" Style="{StaticResource WinoraRowTitle}" />
                <TextBlock x:Name="CardEmail"
                           Style="{StaticResource WinoraRowDetail}"
                           Margin="0"
                           TextWrapping="Wrap" />
                <TextBlock x:Name="CardSince" Style="{StaticResource WinoraRowDetail}" Margin="0" />
                <TextBlock x:Name="CardChanges" Style="{StaticResource WinoraRowDetail}" Margin="0" />
            </StackPanel>
        </Grid>
    </Border>
</UserControl>
```

Создать `src/Winora.App/Controls/ProfileCard.xaml.cs`:

```csharp
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Winora.App.ViewModels;

namespace Winora.App.Controls;

/// <summary>The person's card, shown in the cabinet and at the top of the dashboard.</summary>
public sealed partial class ProfileCard : UserControl
{
    public ProfileCard() => InitializeComponent();

    /// <summary>
    /// Filled by hand rather than by binding.
    /// </summary>
    /// <remarks>
    /// Six values into a control created once. A set of bindings, a colour converter and a
    /// visibility converter would be more machinery than the thing they drive.
    /// </remarks>
    public void Show(ProfileViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        CardName.Text = viewModel.Name;
        CardEmail.Text = viewModel.Email;
        CardEmail.Visibility = viewModel.Email.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        CardSince.Text = viewModel.MemberSince;
        CardChanges.Text = viewModel.RecordedChanges;
        AvatarInitial.Text = viewModel.Initial;
        AvatarCircle.Fill = Brush(viewModel.Colour);
    }

    /// <summary>
    /// A brush from "#RRGGBB".
    /// </summary>
    /// <remarks>
    /// Falls back to the accent brush rather than throwing: the colour arrives from a file a person
    /// could have edited, and a card is decoration — decoration must never be able to stop a screen
    /// from drawing.
    /// </remarks>
    private static SolidColorBrush Brush(string colour)
    {
        try
        {
            if (colour.Length == 7 && colour[0] == '#')
            {
                var red = Convert.ToByte(colour.Substring(1, 2), 16);
                var green = Convert.ToByte(colour.Substring(3, 2), 16);
                var blue = Convert.ToByte(colour.Substring(5, 2), 16);
                return new SolidColorBrush(ColorHelper.FromArgb(255, red, green, blue));
            }
        }
        catch (Exception)
        {
            // Not a colour. Fall through.
        }

        return new SolidColorBrush(Colors.SlateGray);
    }
}
```

- [ ] **Step 3: Написать страницу кабинета**

Создать `src/Winora.App/Views/ProfilePage.xaml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<Page
    x:Class="Winora.App.Views.ProfilePage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:ctl="using:Winora.App.Controls"
    xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
    xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
    mc:Ignorable="d">

    <ScrollViewer VerticalScrollBarVisibility="Auto">
        <StackPanel>

            <Grid Background="{ThemeResource WinoraHeaderSystem}" Padding="32,28,32,26">
                <StackPanel MaxWidth="1800" HorizontalAlignment="Left">
                    <TextBlock Style="{StaticResource WinoraPageTitle}"
                               Text="{x:Bind ViewModel.Heading, Mode=OneWay}" />
                    <Rectangle Style="{StaticResource WinoraRuleSystem}" />
                </StackPanel>
            </Grid>

            <StackPanel Padding="32,24,32,32" MaxWidth="1800" HorizontalAlignment="Left">

                <ctl:ProfileCard x:Name="Card" Margin="{StaticResource WinoraStackGapL}" />

                <TextBlock Style="{StaticResource WinoraRowDetail}"
                           Text="{x:Bind ViewModel.NameLabel, Mode=OneWay}" />
                <TextBox x:Name="NameBox"
                         MaxWidth="420"
                         HorizontalAlignment="Left"
                         Margin="0,4,0,14"
                         Text="{x:Bind ViewModel.Name, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}" />

                <TextBlock Style="{StaticResource WinoraRowDetail}"
                           Text="{x:Bind ViewModel.EmailLabel, Mode=OneWay}" />
                <TextBox x:Name="EmailBox"
                         MaxWidth="420"
                         HorizontalAlignment="Left"
                         Margin="0,4,0,4"
                         Text="{x:Bind ViewModel.Email, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}" />
                <!-- The honest part of the form. Not small print. -->
                <TextBlock Style="{StaticResource WinoraRowDetail}"
                           TextWrapping="Wrap"
                           MaxWidth="420"
                           HorizontalAlignment="Left"
                           Margin="0,0,0,14"
                           Text="{x:Bind ViewModel.EmailPrivacyNote, Mode=OneWay}" />

                <Button Style="{StaticResource WinoraActionButton}"
                        HorizontalAlignment="Left"
                        Content="{x:Bind ViewModel.SaveLabel, Mode=OneWay}"
                        Command="{x:Bind ViewModel.SaveCommand}" />

                <TextBlock Style="{StaticResource WinoraRowDetail}"
                           Margin="0,10,0,0"
                           TextWrapping="Wrap"
                           Text="{x:Bind ViewModel.StatusMessage, Mode=OneWay}" />

            </StackPanel>
        </StackPanel>
    </ScrollViewer>
</Page>
```

Создать `src/Winora.App/Views/ProfilePage.xaml.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Winora.App.ViewModels;

namespace Winora.App.Views;

public sealed partial class ProfilePage : Page
{
    public ProfilePage()
    {
        ViewModel = App.Services.GetRequiredService<ProfileViewModel>();
        InitializeComponent();
    }

    public ProfileViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        ViewModel.Load();
        await ViewModel.LoadStatisticsAsync().ConfigureAwait(true);
        Card.Show(ViewModel);

        // The card follows what the person types, so saving is not the first time they see it.
        ViewModel.PropertyChanged += (_, _) => Card.Show(ViewModel);
    }
}
```

- [ ] **Step 4: Поставить карточку на «Главную»**

В `src/Winora.App/Views/DashboardPage.xaml`, к пространствам имён:

```xml
    xmlns:ctl="using:Winora.App.Controls"
```

(если оно там уже есть — не дублировать), и первым элементом в основной колонке, до всего остального содержимого:

```xml
                    <ctl:ProfileCard x:Name="Card" Margin="{StaticResource WinoraStackGapL}" />
```

В `src/Winora.App/Views/DashboardPage.xaml.cs`, в `OnNavigatedTo`, после существующей загрузки:

```csharp
        // The same card as the cabinet. The dashboard was empty above the fold, and the name the
        // person just typed had nowhere to land.
        var profile = App.Services.GetRequiredService<ProfileViewModel>();
        profile.Load();
        await profile.LoadStatisticsAsync().ConfigureAwait(true);
        Card.Show(profile);
```

Если `OnNavigatedTo` там не `async` — сделать его `protected override async void OnNavigatedTo(NavigationEventArgs e)`, как на `ProfilePage`.

- [ ] **Step 5: Собрать**

```bash
dotnet build src/Winora.App/Winora.App.csproj -c Release -p:Platform=x64 --nologo -v q
```

Ожидаемо: молча. `TreatWarningsAsErrors=true`, поэтому отсутствие вывода и есть результат.

- [ ] **Step 6: Прогнать всё**

```bash
dotnet test --nologo
```

Ожидаемо: ни одного упавшего. В частности `RouteKeys.All` и каталог страниц проверяются тестами навигации — новый маршрут должен быть во всех трёх местах.

- [ ] **Step 7: Коммит**

```bash
git add src/Winora.App/Controls/ProfileCard.xaml src/Winora.App/Controls/ProfileCard.xaml.cs src/Winora.App/Views/ProfilePage.xaml src/Winora.App/Views/ProfilePage.xaml.cs src/Winora.App/Navigation src/Winora.App/Controls/FluentIconCatalog.cs src/Winora.App/Views/DashboardPage.xaml src/Winora.App/Views/DashboardPage.xaml.cs
git commit -m "feat(profile): one card, in the cabinet and at the top of the dashboard"
```

---

### Task 6: Окно приветствия

**Files:**
- Create: `src/Winora.App/Views/WelcomeDialog.xaml`
- Create: `src/Winora.App/Views/WelcomeDialog.xaml.cs`
- Modify: `src/Winora.App/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `ProfileViewModel`, `IProfileService` из задачи 4.
- Produces: `WelcomeDialog : ContentDialog` со статическим `static Task ShowIfNeededAsync(XamlRoot root)`.

- [ ] **Step 1: Написать окно**

Создать `src/Winora.App/Views/WelcomeDialog.xaml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<ContentDialog
    x:Class="Winora.App.Views.WelcomeDialog"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
    xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
    mc:Ignorable="d">

    <StackPanel Spacing="12" MinWidth="420">

        <!--
          What the program is, before what the person is. Somebody opening Winora for the first
          time has no idea what it does, and the safety promise is the single most important thing
          to say: nothing is changed without being shown first.
        -->
        <TextBlock x:Name="AboutText" TextWrapping="Wrap" />
        <TextBlock x:Name="SafetyText" TextWrapping="Wrap" Opacity="0.8" />

        <TextBlock x:Name="NameLabel" Margin="0,8,0,0" />
        <TextBox x:Name="NameBox" />

        <TextBlock x:Name="EmailLabel" />
        <TextBox x:Name="EmailBox" />
        <!-- The honest part of the form. -->
        <TextBlock x:Name="PrivacyText" TextWrapping="Wrap" Opacity="0.8" />

    </StackPanel>
</ContentDialog>
```

Создать `src/Winora.App/Views/WelcomeDialog.xaml.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Winora.App.Services;
using Winora.App.ViewModels;

namespace Winora.App.Views;

/// <summary>The one window a person sees before anything else, once.</summary>
/// <remarks>
/// A greeting, not a gate. It can be skipped with one press, and skipping still leaves a profile —
/// otherwise the window would return on every launch, which is how a greeting becomes a nuisance.
/// </remarks>
public sealed partial class WelcomeDialog : ContentDialog
{
    private readonly ProfileViewModel _profile;

    private WelcomeDialog(ProfileViewModel profile, ILocalizationService text)
    {
        _profile = profile;
        InitializeComponent();

        Title = text.Get("Welcome_Title");
        PrimaryButtonText = text.Get("Welcome_Start");
        CloseButtonText = text.Get("Welcome_Skip");
        DefaultButton = ContentDialogButton.Primary;

        AboutText.Text = text.Get("Welcome_About");
        SafetyText.Text = text.Get("App_Safety_Statement");
        NameLabel.Text = text.Get("Profile_NameLabel");
        EmailLabel.Text = text.Get("Profile_EmailLabel");
        PrivacyText.Text = text.Get("Profile_EmailPrivacy");

        NameBox.PlaceholderText = text.Get("Welcome_NameHint");
        NameBox.Text = profile.Name;
    }

    /// <summary>Shows the greeting when nobody has introduced themselves yet.</summary>
    public static async Task ShowIfNeededAsync(XamlRoot root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var profile = App.Services.GetRequiredService<ProfileViewModel>();
        profile.Load();

        if (profile.HasProfile)
        {
            return;
        }

        var dialog = new WelcomeDialog(
            profile,
            App.Services.GetRequiredService<ILocalizationService>())
        {
            XamlRoot = root,
        };

        var pressed = await dialog.ShowAsync();

        profile.Name = pressed == ContentDialogResult.Primary
            ? dialog.NameBox.Text
            : App.Services.GetRequiredService<IProfileService>().SuggestedName;

        profile.Email = pressed == ContentDialogResult.Primary ? dialog.EmailBox.Text : string.Empty;

        // Skipping still writes a profile, so the greeting does not come back every launch. If the
        // Windows account name will not pass the rules — which would take an empty one — nothing is
        // written and the person is asked again next time. That is the honest outcome either way.
        if (profile.CanSave)
        {
            profile.SaveCommand.Execute(null);
        }
    }
}
```

- [ ] **Step 2: Показать его при первом запуске**

В `src/Winora.App/MainWindow.xaml.cs`, в конце метода `OnRootLoaded`, после существующего вызова `OfferInstall()`:

```csharp
        // After the install offer, not before: two dialogs at once is one too many, and the install
        // question decides where the program will live before the person settles into it.
        _ = ShowWelcomeAsync();
```

и в конец класса:

```csharp
    /// <summary>Greets a new person once the window exists to hang a dialog on.</summary>
    private async Task ShowWelcomeAsync()
    {
        try
        {
            await Views.WelcomeDialog.ShowIfNeededAsync(Content.XamlRoot).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // A greeting that will not open is not a reason to fail a launch.
            Diagnostics.DiagnosticSink.Write("Welcome", ex);
        }
    }
```

- [ ] **Step 3: Собрать**

```bash
dotnet build src/Winora.App/Winora.App.csproj -c Release -p:Platform=x64 --nologo -v q
```

Ожидаемо: молча.

- [ ] **Step 4: Прогнать всё**

```bash
dotnet test --nologo
```

Ожидаемо: ни одного упавшего.

- [ ] **Step 5: Проверить живьём**

```bash
dotnet publish src/Winora.App/Winora.App.csproj -c Release -p:WinoraPortable=true -p:Platform=x64 -p:Version=0.5.0 -o publish/run --nologo
```

Затем удалить профиль, чтобы приветствие сработало как в первый раз, и запустить:

```bash
powershell -NoProfile -Command "Remove-Item \"$env:USERPROFILE\Winora\State\profile.json\" -ErrorAction SilentlyContinue; Start-Process 'M:\WinoraWork\Winora\publish\run\Winora.exe'"
```

Проверить глазами:

1. После вопроса об установке появляется окно приветствия с рассказом о программе и обещанием безопасности.
2. Имя подставлено из Windows; «Начать» неактивна при пустом имени.
3. Под полем почты стоит строка о том, что почта остаётся на этом компьютере.
4. После «Начать» карточка с именем видна вверху «Главной» и в разделе «Профиль».
5. Повторный запуск приветствия не показывает.
6. «Пропустить» тоже не приводит к повторному показу.

- [ ] **Step 6: Коммит**

```bash
git add src/Winora.App/Views/WelcomeDialog.xaml src/Winora.App/Views/WelcomeDialog.xaml.cs src/Winora.App/MainWindow.xaml.cs
git commit -m "feat(profile): say what Winora is before asking who you are"
```

---

## Порядок и зависимости

```
1 (правила) → 2 (аватар) → 3 (хранилище) → 4 (прослойка и модель) → 5 (карточка и раздел) → 6 (приветствие)
```

Строго последовательно: каждая задача опирается на предыдущую.

## Чего в плане нет, и почему

**Третьего числа на карточке.** Спецификация называет три: изменения, резервные копии и текущая тема. В плане два — изменения из журнала и дата знакомства. Счётчик резервных копий и текущая тема требуют API, которых я не сверял, а выдумывать имена в плане, который кто-то будет исполнять дословно, хуже, чем назвать пробел вслух. Добавляется одной строкой в `ProfileService`, когда будет чем.

**Выбора цвета в интерфейсе.** `Palette` и поле `Avatar` готовы и хранятся, но пикера в разметке нет: пока цвет считается из имени, и этого достаточно. Добавить — полтора десятка строк в `ProfilePage.xaml`, когда станет нужно.
