# Окно регистрации при первом запуске — план работ

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** При первом запуске видно только окно регистрации; после «Готово» открывается приложение.

**Architecture:** Отпечаток пароля и оценка сложности — чистые функции в `Winora.Core`. Хранилище получает новый формат с версией схемы; старый профиль читается как «регистрации не было». Мастер — отдельное тёмное окно WinUI с шаговым индикатором и анимациями переходов. `App.xaml.cs` решает при старте, какое окно создать.

**Tech Stack:** .NET 10, WinUI 3, `Rfc2898DeriveBytes`, CommunityToolkit.Mvvm, xunit.

**Спецификация:** `docs/superpowers/specs/2026-08-24-winora-registration-design.md`
**Разбор образца:** `.superpowers/sdd/2026-08-24-winora-profile/reference-registration-card.md`

## Global Constraints

- `net10.0-windows10.0.26100.0`, `x64`, `LangVersion 14.0`, `Nullable enable`, `TreatWarningsAsErrors=true` — **предупреждение ломает сборку**.
- Комментарии в коде по-английски. Всё видимое на экране — только в `src/Winora.App/Strings/ru-RU/Resources.resw`, записи **в одну строку**, без `xml:space`.
- **Модели представления не обращаются к `Winora.System` и `Winora.Infrastructure`** — проверяет `tests/Winora.Architecture.Tests/SolutionStructureTests.cs`.
- Явные квалификаторы `System.` не компилируются: `Winora.System` затеняет глобальный `System`. Писать `global::System.` или `using`.
- Наблюдаемые свойства только `[ObservableProperty] public partial ... { get; set; }` — иначе MVVMTK0045.
- Своей криптографии не писать. Только `System.Security.Cryptography`.
- **Две фразы образца не переносить**: «Мы отправим подтверждение…» и «Защитите свой аккаунт…». Winora ничего не отправляет и локальным паролем ничего не защищает.
- Сейчас проходит 1352 теста в пяти проектах: `dotnet test --nologo`. Ни один не должен сломаться.

## Файловая карта

| Файл | За что отвечает |
|---|---|
| `src/Winora.Core/Profile/PasswordHash.cs` | посчитать и проверить отпечаток |
| `src/Winora.Core/Profile/PasswordStrength.cs` | четыре требования и оценка словом |
| `src/Winora.Core/Profile/UserProfile.cs` | три новых поля и версия схемы |
| `src/Winora.Infrastructure/Profile/UserProfileStore.cs` | новый формат; старый = «нет профиля» |
| `src/Winora.App/ViewModels/RegistrationViewModel.cs` | шаги, поля, проверки, сохранение |
| `src/Winora.App/Views/RegistrationWindow.xaml` | тёмное окно, карточка, индикатор, анимации |
| `src/Winora.App/Controls/StepProgress.xaml` | шаговый индикатор |
| `src/Winora.App/App.xaml.cs` | какое окно создать при старте |

---

### Task 1: Отпечаток пароля

**Files:**
- Create: `src/Winora.Core/Profile/PasswordHash.cs`
- Test: `tests/Winora.Core.Tests/Profile/PasswordHashTests.cs`

**Interfaces:**
- Consumes: ничего.
- Produces: `sealed record PasswordDigest(string Hash, string Salt, int Iterations)` и `static class PasswordHash` с `const int DefaultIterations = 210_000;`, `static PasswordDigest Create(string password)`, `static bool Verify(string password, PasswordDigest digest)`

- [ ] **Step 1: Написать падающий тест**

Создать `tests/Winora.Core.Tests/Profile/PasswordHashTests.cs`:

```csharp
using Winora.Core.Profile;
using Xunit;

namespace Winora.Core.Tests.Profile;

/// <summary>
/// Storing a password without storing the password.
/// </summary>
/// <remarks>
/// Nothing here invents cryptography. The point of these tests is the handling around it: that a
/// fresh salt is drawn every time, that the digest is checkable, and that a wrong password is
/// refused — including the shapes of wrong that come from an edited file rather than from a person.
/// </remarks>
public sealed class PasswordHashTests
{
    [Fact]
    public void The_right_password_is_accepted()
    {
        var digest = PasswordHash.Create("Password1!");

        Assert.True(PasswordHash.Verify("Password1!", digest));
    }

    [Theory]
    [InlineData("password1!")]
    [InlineData("Password1")]
    [InlineData("Password1! ")]
    [InlineData("")]
    public void A_wrong_password_is_refused(string attempt)
    {
        var digest = PasswordHash.Create("Password1!");

        Assert.False(PasswordHash.Verify(attempt, digest));
    }

    /// <summary>
    /// Two people who choose the same password must not end up with the same stored digest.
    /// </summary>
    /// <remarks>
    /// That is what the salt is for, and it is the one property that a hand-rolled implementation
    /// most often gets wrong by reusing a constant.
    /// </remarks>
    [Fact]
    public void The_same_password_twice_gives_different_digests()
    {
        var first = PasswordHash.Create("Password1!");
        var second = PasswordHash.Create("Password1!");

        Assert.NotEqual(first.Salt, second.Salt);
        Assert.NotEqual(first.Hash, second.Hash);
        Assert.True(PasswordHash.Verify("Password1!", first));
        Assert.True(PasswordHash.Verify("Password1!", second));
    }

    [Fact]
    public void The_digest_records_how_it_was_made()
    {
        var digest = PasswordHash.Create("Password1!");

        Assert.Equal(PasswordHash.DefaultIterations, digest.Iterations);
        Assert.NotEmpty(digest.Salt);
        Assert.NotEmpty(digest.Hash);
    }

    /// <summary>
    /// A digest that has been edited by hand refuses everything rather than throwing.
    /// </summary>
    /// <remarks>
    /// profile.json is a plain file a person can open. Whatever they do to it, Verify has to answer
    /// "no" — a thrown exception here would come out of a startup path and take the window with it.
    /// </remarks>
    [Theory]
    [InlineData("", "c2FsdA==", 210_000)]
    [InlineData("aGFzaA==", "", 210_000)]
    [InlineData("not base64!", "c2FsdA==", 210_000)]
    [InlineData("aGFzaA==", "not base64!", 210_000)]
    [InlineData("aGFzaA==", "c2FsdA==", 0)]
    [InlineData("aGFzaA==", "c2FsdA==", -1)]
    public void A_broken_digest_refuses_everything(string hash, string salt, int iterations)
    {
        var digest = new PasswordDigest(hash, salt, iterations);

        Assert.False(PasswordHash.Verify("Password1!", digest));
    }

    [Fact]
    public void A_null_digest_refuses_everything()
    {
        Assert.False(PasswordHash.Verify("Password1!", null));
    }
}
```

- [ ] **Step 2: Убедиться, что тест падает**

```bash
dotnet test tests/Winora.Core.Tests/Winora.Core.Tests.csproj --nologo --filter "FullyQualifiedName~PasswordHashTests"
```

Ожидаемо: `CS0246` — `PasswordHash` и `PasswordDigest` не найдены.

- [ ] **Step 3: Написать**

Создать `src/Winora.Core/Profile/PasswordHash.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace Winora.Core.Profile;

/// <param name="Hash">The derived key, base64.</param>
/// <param name="Salt">The salt it was derived with, base64. Fresh for every profile.</param>
/// <param name="Iterations">How many rounds produced it, so a stored digest stays checkable.</param>
public sealed record PasswordDigest(string Hash, string Salt, int Iterations);

/// <summary>
/// Turns a password into something that can be checked but not read back.
/// </summary>
/// <remarks>
/// <para>
/// PBKDF2 out of the framework, deliberately not anything hand-written. The iteration count is
/// stored beside the digest rather than assumed, so it can be raised later without making every
/// existing profile unreadable — the old ones keep verifying at the count they were made with.
/// </para>
/// <para>
/// Worth being plain about what this does not do. Winora has no server: the digest sits in a file
/// beside the program, on the same machine as the person typing the password. It stops a password
/// being read out of that file; it does not stop anyone who has the file from using the program.
/// The registration screen says so in as many words.
/// </para>
/// </remarks>
public static class PasswordHash
{
    /// <summary>Rounds for a new password. Stored per digest, so this may rise over time.</summary>
    public const int DefaultIterations = 210_000;

    private const int SaltBytes = 16;

    private const int KeyBytes = 32;

    public static PasswordDigest Create(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var key = Derive(password, salt, DefaultIterations);

        return new PasswordDigest(
            Convert.ToBase64String(key),
            Convert.ToBase64String(salt),
            DefaultIterations);
    }

    /// <summary>Whether this password produces the stored digest.</summary>
    /// <remarks>
    /// Every unusable digest answers false rather than throwing. The file it came from is one a
    /// person can open and edit, and this runs on the path that decides whether to show a window —
    /// an exception here would take the launch down over a bad character in a text file.
    /// </remarks>
    public static bool Verify(string password, PasswordDigest? digest)
    {
        if (password is null || digest is null || digest.Iterations <= 0)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(digest.Salt);
            var expected = Convert.FromBase64String(digest.Hash);

            if (salt.Length == 0 || expected.Length == 0)
            {
                return false;
            }

            var actual = Derive(password, salt, digest.Iterations);

            // Fixed-time comparison: the framework's own, so a difference in where two digests
            // diverge cannot be measured from outside.
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (Exception)
        {
            // Not base64, or a length the framework refuses. Either way: not this password.
            return false;
        }
    }

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            KeyBytes);
}
```

- [ ] **Step 4: Убедиться, что тесты проходят**

```bash
dotnet test tests/Winora.Core.Tests/Winora.Core.Tests.csproj --nologo --filter "FullyQualifiedName~PasswordHashTests"
```

Ожидаемо: все зелёные.

- [ ] **Step 5: Коммит**

```bash
git add src/Winora.Core/Profile/PasswordHash.cs tests/Winora.Core.Tests/Profile/PasswordHashTests.cs
git commit -m "feat(registration): store a password without storing the password"
```

---

### Task 2: Оценка сложности пароля

**Files:**
- Create: `src/Winora.Core/Profile/PasswordStrength.cs`
- Test: `tests/Winora.Core.Tests/Profile/PasswordStrengthTests.cs`

**Interfaces:**
- Consumes: ничего.
- Produces: `sealed record PasswordStrength(int Score, bool HasMinLength, bool HasNumber, bool HasUppercase, bool HasSpecial)` со свойством `bool IsAcceptable => Score >= 2 && HasMinLength;` и `static class PasswordStrengthRules` с `const int MinLength = 8;`, `static PasswordStrength Evaluate(string? password)`

- [ ] **Step 1: Написать падающий тест**

Создать `tests/Winora.Core.Tests/Profile/PasswordStrengthTests.cs`:

```csharp
using Winora.Core.Profile;
using Xunit;

namespace Winora.Core.Tests.Profile;

/// <summary>
/// The four requirements the registration screen ticks off as somebody types.
/// </summary>
/// <remarks>
/// Copied in substance from the reference the owner supplied: at least eight characters, a digit,
/// a capital, and something that is neither. The capital and the "something else" both have to
/// understand Cyrillic, because the people typing here are typing Russian.
/// </remarks>
public sealed class PasswordStrengthTests
{
    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("abc", 0)]
    [InlineData("abcdefgh", 1)]
    [InlineData("abcdefg1", 2)]
    [InlineData("Abcdefg1", 3)]
    [InlineData("Abcdefg1!", 4)]
    public void The_score_counts_the_requirements_met(string? password, int expected)
    {
        Assert.Equal(expected, PasswordStrengthRules.Evaluate(password).Score);
    }

    [Fact]
    public void Each_requirement_is_reported_on_its_own()
    {
        var strength = PasswordStrengthRules.Evaluate("Abcdefg1!");

        Assert.True(strength.HasMinLength);
        Assert.True(strength.HasNumber);
        Assert.True(strength.HasUppercase);
        Assert.True(strength.HasSpecial);
    }

    [Fact]
    public void A_short_password_fails_only_the_length()
    {
        var strength = PasswordStrengthRules.Evaluate("Ab1!");

        Assert.False(strength.HasMinLength);
        Assert.True(strength.HasNumber);
        Assert.True(strength.HasUppercase);
        Assert.True(strength.HasSpecial);
    }

    /// <summary>The people typing here type Russian, so Cyrillic has to count.</summary>
    [Fact]
    public void A_cyrillic_capital_counts_as_a_capital()
    {
        Assert.True(PasswordStrengthRules.Evaluate("Пароль12").HasUppercase);
    }

    [Fact]
    public void A_cyrillic_letter_is_not_a_special_character()
    {
        Assert.False(PasswordStrengthRules.Evaluate("Пароль12").HasSpecial);
    }

    [Theory]
    [InlineData("Пароль1!")]
    [InlineData("пароль 1")]
    public void A_space_or_a_symbol_counts_as_special(string password)
    {
        Assert.True(PasswordStrengthRules.Evaluate(password).HasSpecial);
    }

    /// <summary>
    /// What the "Готово" button actually waits for: eight characters and at least two requirements.
    /// </summary>
    [Theory]
    [InlineData("abcdefgh", false)]
    [InlineData("abcdefg1", true)]
    [InlineData("Ab1!", false)]
    [InlineData("Abcdefg1!", true)]
    public void Acceptable_means_long_enough_and_not_trivial(string password, bool expected)
    {
        Assert.Equal(expected, PasswordStrengthRules.Evaluate(password).IsAcceptable);
    }
}
```

- [ ] **Step 2: Убедиться, что тест падает**

```bash
dotnet test tests/Winora.Core.Tests/Winora.Core.Tests.csproj --nologo --filter "FullyQualifiedName~PasswordStrengthTests"
```

Ожидаемо: `CS0246` — `PasswordStrengthRules` не найден.

- [ ] **Step 3: Написать**

Создать `src/Winora.Core/Profile/PasswordStrength.cs`:

```csharp
namespace Winora.Core.Profile;

/// <param name="Score">How many of the four requirements are met, 0 to 4.</param>
public sealed record PasswordStrength(
    int Score,
    bool HasMinLength,
    bool HasNumber,
    bool HasUppercase,
    bool HasSpecial)
{
    /// <summary>
    /// Whether the registration screen will accept it.
    /// </summary>
    /// <remarks>
    /// Length is required outright; beyond that, two of the four is the bar. Demanding all four
    /// pushes people towards writing the password down, which is worse than a merely decent one.
    /// </remarks>
    public bool IsAcceptable => Score >= 2 && HasMinLength;
}

/// <summary>The four requirements shown as a checklist while somebody types.</summary>
public static class PasswordStrengthRules
{
    public const int MinLength = 8;

    public static PasswordStrength Evaluate(string? password)
    {
        var value = password ?? string.Empty;

        var hasMinLength = value.Length >= MinLength;
        var hasNumber = value.Any(char.IsDigit);
        var hasUppercase = value.Any(char.IsUpper);

        // Anything that is neither a letter nor a digit — punctuation, a symbol, a space. Written
        // as "not letter, not digit" rather than as a list of allowed symbols so that it holds for
        // every alphabet, including the Cyrillic these passwords are mostly typed in.
        var hasSpecial = value.Any(static character =>
            !char.IsLetterOrDigit(character));

        var score = 0;
        if (hasMinLength) { score++; }
        if (hasNumber) { score++; }
        if (hasUppercase) { score++; }
        if (hasSpecial) { score++; }

        return new PasswordStrength(score, hasMinLength, hasNumber, hasUppercase, hasSpecial);
    }
}
```

- [ ] **Step 4: Убедиться, что тесты проходят**

```bash
dotnet test tests/Winora.Core.Tests/Winora.Core.Tests.csproj --nologo --filter "FullyQualifiedName~PasswordStrengthTests"
```

Ожидаемо: все зелёные.

- [ ] **Step 5: Коммит**

```bash
git add src/Winora.Core/Profile/PasswordStrength.cs tests/Winora.Core.Tests/Profile/PasswordStrengthTests.cs
git commit -m "feat(registration): tick off the four password requirements as they are typed"
```

---

### Task 3: Профиль с паролем и новый формат файла

**Files:**
- Modify: `src/Winora.Core/Profile/UserProfile.cs`
- Modify: `src/Winora.Infrastructure/Profile/UserProfileStore.cs`
- Modify: `tests/Winora.Infrastructure.Tests/Profile/UserProfileStoreTests.cs`
- Modify: `src/Winora.App/Services/ProfileService.cs`

**Interfaces:**
- Consumes: `PasswordDigest` из задачи 1.
- Produces: `UserProfile` получает `PasswordDigest? Password`; `UserProfileStore` пишет и читает `schemaVersion: 2`; профиль без пароля читается как `null`.

- [ ] **Step 1: Написать падающий тест**

В `tests/Winora.Infrastructure.Tests/Profile/UserProfileStoreTests.cs` добавить, а `Sample()` изменить так, чтобы профиль нёс пароль:

```csharp
    private static UserProfile Sample() =>
        new(
            "Аня",
            "anya@example.com",
            2,
            new DateTimeOffset(2026, 8, 24, 3, 0, 0, TimeSpan.Zero),
            PasswordHash.Create("Password1!"));

    /// <summary>The digest survives the round trip whole, or the password stops working.</summary>
    [Fact]
    public void The_password_digest_comes_back_whole()
    {
        var written = Sample();
        Store().Write(written);

        var read = Store().Read();

        Assert.NotNull(read?.Password);
        Assert.Equal(written.Password!.Hash, read.Password!.Hash);
        Assert.Equal(written.Password.Salt, read.Password.Salt);
        Assert.Equal(written.Password.Iterations, read.Password.Iterations);
        Assert.True(PasswordHash.Verify("Password1!", read.Password));
    }

    /// <summary>
    /// A profile from the previous version has no password, so registration was never completed.
    /// </summary>
    /// <remarks>
    /// It reads as absent rather than as a half-profile: the registration window is the only way in
    /// now, and letting an old file skip it would leave somebody with an account that has no way to
    /// be checked. Nothing of value is lost — a name and an email are half a minute to retype.
    /// </remarks>
    [Fact]
    public void A_profile_from_the_old_format_reads_as_no_profile()
    {
        File.WriteAllText(
            Path.Combine(_folder, "profile.json"),
            """
            {"name":"Аня","email":"anya@example.com","avatar":2,
             "createdUtc":"2026-08-24T00:00:00+00:00"}
            """);

        Assert.Null(Store().Read());
    }

    [Fact]
    public void A_profile_whose_digest_is_empty_reads_as_no_profile()
    {
        File.WriteAllText(
            Path.Combine(_folder, "profile.json"),
            """
            {"schemaVersion":2,"name":"Аня","email":"","avatar":0,
             "createdUtc":"2026-08-24T00:00:00+00:00",
             "passwordHash":"","passwordSalt":"","passwordIterations":0}
            """);

        Assert.Null(Store().Read());
    }
```

- [ ] **Step 2: Убедиться, что тест падает**

```bash
dotnet test tests/Winora.Infrastructure.Tests/Winora.Infrastructure.Tests.csproj --nologo --filter "FullyQualifiedName~UserProfileStoreTests"
```

Ожидаемо: не компилируется — у `UserProfile` пока четыре поля.

- [ ] **Step 3: Добавить поле в профиль**

В `src/Winora.Core/Profile/UserProfile.cs` заменить объявление записи:

```csharp
/// <param name="Name">What to call this person. Never empty once stored.</param>
/// <param name="Email">Optional, and it stays on this machine. May be empty.</param>
/// <param name="Avatar">Index into the palette, or -1 for "work it out from the name".</param>
/// <param name="CreatedUtc">When the introduction happened.</param>
/// <param name="Password">
/// The password digest. Null only in memory, while a registration is being filled in — a stored
/// profile always has one, because registration is the only way a profile comes to exist.
/// </param>
public sealed record UserProfile(
    string Name,
    string Email,
    int Avatar,
    DateTimeOffset CreatedUtc,
    PasswordDigest? Password = null);
```

- [ ] **Step 4: Научить хранилище новому формату**

В `src/Winora.Infrastructure/Profile/UserProfileStore.cs` заменить запись `StoredProfile` и оба преобразования:

```csharp
    /// <summary>
    /// The current shape of profile.json.
    /// </summary>
    /// <remarks>
    /// Version 2 added the password. A file without one was written before registration existed,
    /// and is treated as no profile at all rather than as a profile to be trusted — see Read.
    /// </remarks>
    private const int CurrentSchemaVersion = 2;
```

в `Read`, после проверки имени и до возврата, добавить:

```csharp
            // A profile with no usable digest never went through registration. That is the only
            // way in now, so such a file reads as absent and the person is asked to register.
            var digest = new PasswordDigest(
                stored.PasswordHash ?? string.Empty,
                stored.PasswordSalt ?? string.Empty,
                stored.PasswordIterations);

            if (stored.SchemaVersion < CurrentSchemaVersion ||
                digest.Hash.Length == 0 ||
                digest.Salt.Length == 0 ||
                digest.Iterations <= 0)
            {
                return null;
            }
```

и вернуть профиль с паролем:

```csharp
            return new UserProfile(
                finalName,
                stored.Email?.Trim() ?? string.Empty,
                stored.Avatar,
                stored.CreatedUtc,
                digest);
```

В `Write` собирать запись так:

```csharp
                JsonSerializer.Serialize(
                    new StoredProfile(
                        CurrentSchemaVersion,
                        profile.Name,
                        profile.Email,
                        profile.Avatar,
                        profile.CreatedUtc,
                        profile.Password?.Hash ?? string.Empty,
                        profile.Password?.Salt ?? string.Empty,
                        profile.Password?.Iterations ?? 0),
                    Options)
```

и заменить саму запись:

```csharp
    private sealed record StoredProfile(
        int SchemaVersion,
        string? Name,
        string? Email,
        int Avatar,
        DateTimeOffset CreatedUtc,
        string? PasswordHash,
        string? PasswordSalt,
        int PasswordIterations);
```

К `using` файла добавить `using Winora.Core.Profile;` — если его там ещё нет.

- [ ] **Step 5: Починить вызов в прослойке**

`ProfileService.Save` создаёт `UserProfile` из четырёх полей и потеряет пароль при сохранении из кабинета. В `src/Winora.App/Services/ProfileService.cs` заменить создание профиля так, чтобы существующий пароль переносился:

```csharp
        // The digest is carried over, not re-made: editing a name in the cabinet must not silently
        // change what the password checks against.
        var existing = _store.Read();
        var created = existing?.CreatedUtc ?? DateTimeOffset.UtcNow;

        return _store.Write(
            new UserProfile(
                trimmed,
                email?.Trim() ?? string.Empty,
                avatar,
                created,
                existing?.Password));
```

- [ ] **Step 6: Закрепить, что кабинет не стирает пароль**

Регистрация и кабинет пишут в один файл разными путями: `Register` создаёт профиль, `Save`
правит имя и почту. Если `Save` когда-нибудь забудет перенести отпечаток, человек молча
потеряет пароль — и узнает об этом только тогда, когда войти станет некуда. Тест на это, в
`tests/Winora.App.Tests/` рядом с остальными тестами прослойки:

```csharp
    /// <summary>
    /// Editing a name in the cabinet must not disturb the password.
    /// </summary>
    /// <remarks>
    /// Register and Save write the same file by different routes. If Save ever stops carrying the
    /// digest across, the password is silently lost, and nobody finds out until the day there is
    /// something to log into.
    /// </remarks>
    [Fact]
    public void Saving_from_the_cabinet_keeps_the_password()
    {
        var store = new UserProfileStore(_folder);
        var service = new ProfileService(store, new SilentJournal());

        Assert.True(service.Register("Аня", "a@b.ru", "Password1!"));
        Assert.True(service.Save("Пётр", "a@b.ru", 3));

        var stored = store.Read();

        Assert.NotNull(stored?.Password);
        Assert.Equal("Пётр", stored.Name);
        Assert.True(PasswordHash.Verify("Password1!", stored.Password));
    }
```

`SilentJournal` — заглушка `IActionJournalReader`, возвращающая пустой список. Если такой в
проекте ещё нет, объявить её рядом с тестом.

- [ ] **Step 7: Убедиться, что всё проходит**

```bash
dotnet test --nologo
```

Ожидаемо: ни одного упавшего. Если какие-то тесты профиля из прошлого плана ждали профиль без пароля — их надо обновить, а не ослабить: профиль теперь всегда с паролем.

- [ ] **Step 8: Коммит**

```bash
git add src/Winora.Core/Profile/UserProfile.cs src/Winora.Infrastructure/Profile/UserProfileStore.cs src/Winora.App/Services/ProfileService.cs tests/Winora.Infrastructure.Tests/Profile/UserProfileStoreTests.cs
git commit -m "feat(registration): profile carries a password, and the old format reads as none"
```

---

### Task 4: Модель мастера

**Files:**
- Create: `src/Winora.App/ViewModels/RegistrationViewModel.cs`
- Modify: `src/Winora.App/Services/ProfileService.cs`
- Modify: `src/Winora.App/Services/ServiceRegistration.cs`
- Modify: `src/Winora.App/Strings/ru-RU/Resources.resw`
- Test: `tests/Winora.App.Tests/ViewModels/RegistrationViewModelTests.cs`

**Interfaces:**
- Consumes: `PasswordStrengthRules.Evaluate`, `PasswordHash.Create`, `ProfileRules`, `IProfileService`.
- Produces: `enum RegistrationStep { Name, Email, Password, Done }`; `RegistrationViewModel` со свойствами `Step, Name, Email, Password, Confirm, NameError, EmailError, CanGoNext, CanFinish, Strength, StatusMessage` и командами `NextCommand, BackCommand, FinishCommand`; `IProfileService.Register(string name, string email, string password)`.

- [ ] **Step 1: Добавить строки**

В `src/Winora.App/Strings/ru-RU/Resources.resw`, перед `</root>`, в одну строку каждая:

```xml
  <data name="Reg_WindowTitle"><value>Регистрация нового пользователя</value></data>
  <data name="Reg_Step1Badge"><value>Шаг 1 из 3: Знакомство</value></data>
  <data name="Reg_Step2Badge"><value>Шаг 2 из 3: Контакты</value></data>
  <data name="Reg_Step3Badge"><value>Шаг 3 из 3: Безопасность</value></data>
  <data name="Reg_Step1Title"><value>Как вас зовут?</value></data>
  <data name="Reg_Step1Sub"><value>Укажите имя — так Winora будет к вам обращаться.</value></data>
  <data name="Reg_Step2Title"><value>Укажите вашу почту</value></data>
  <data name="Reg_Step2Sub"><value>Почта остаётся на этом компьютере. Winora ничего никуда не отправляет.</value></data>
  <data name="Reg_Step3Title"><value>Создайте пароль</value></data>
  <data name="Reg_Step3Sub"><value>Пароль хранится на этом компьютере и не отправляется никуда. Восстановить его нельзя: если забудете, профиль придётся создать заново.</value></data>
  <data name="Reg_NameLabel"><value>Ваше имя</value></data>
  <data name="Reg_NamePlaceholder"><value>Например: Александр</value></data>
  <data name="Reg_NameTooShort"><value>Имя должно содержать не менее 2 символов</value></data>
  <data name="Reg_EmailLabel"><value>Электронная почта</value></data>
  <data name="Reg_EmailPlaceholder"><value>name@example.com</value></data>
  <data name="Reg_EmailInvalid"><value>Проверьте адрес: нужен вид name@example.com</value></data>
  <data name="Reg_QuickDomains"><value>Быстрый выбор домена:</value></data>
  <data name="Reg_PasswordLabel"><value>Пароль</value></data>
  <data name="Reg_ConfirmLabel"><value>Повторите пароль</value></data>
  <data name="Reg_ConfirmMismatch"><value>Пароли не совпадают</value></data>
  <data name="Reg_ReqMinLength"><value>Не короче 8 знаков</value></data>
  <data name="Reg_ReqNumber"><value>Есть цифра</value></data>
  <data name="Reg_ReqUppercase"><value>Есть заглавная буква</value></data>
  <data name="Reg_ReqSpecial"><value>Есть знак или пробел</value></data>
  <data name="Reg_Strength_0"><value>Введите пароль</value></data>
  <data name="Reg_Strength_1"><value>Слабый</value></data>
  <data name="Reg_Strength_2"><value>Средний</value></data>
  <data name="Reg_Strength_3"><value>Хороший</value></data>
  <data name="Reg_Strength_4"><value>Надёжный</value></data>
  <data name="Reg_Next"><value>Продолжить</value></data>
  <data name="Reg_Back"><value>Назад</value></data>
  <data name="Reg_Finish"><value>Готово</value></data>
  <data name="Reg_DoneTitle"><value>Регистрация завершена</value></data>
  <data name="Reg_DoneSub"><value>Профиль создан на этом компьютере.</value></data>
  <data name="Reg_Open"><value>Открыть Winora</value></data>
  <data name="Reg_SaveFailed"><value>Не удалось сохранить профиль. Попробуйте ещё раз.</value></data>
  <data name="Reg_StepName"><value>Имя</value></data>
  <data name="Reg_StepEmail"><value>Почта</value></data>
  <data name="Reg_StepPassword"><value>Пароль</value></data>
```

- [ ] **Step 2: Написать падающий тест**

Создать `tests/Winora.App.Tests/ViewModels/RegistrationViewModelTests.cs`:

```csharp
using Winora.App.Services;
using Winora.App.ViewModels;
using Xunit;

namespace Winora.App.Tests.ViewModels;

/// <summary>
/// The wizard: three steps, and what each one refuses to let past.
/// </summary>
public sealed class RegistrationViewModelTests
{
    private sealed class FakeProfileService : IProfileService
    {
        public ProfileView? Current { get; set; }

        public string SuggestedName { get; init; } = "brawl";

        public IReadOnlyList<string> Palette { get; } = ["#7C6BF5"];

        public bool RegisterSucceeds { get; init; } = true;

        public (string Name, string Email, string Password)? Registered { get; private set; }

        public bool Save(string name, string email, int avatar) => true;

        public bool Register(string name, string email, string password)
        {
            Registered = (name, email, password);
            return RegisterSucceeds;
        }

        public Task<int> RecordedChangesAsync() => Task.FromResult(0);
    }

    private sealed class EchoLocalization : ILocalizationService
    {
        public bool IsAvailable => true;

        public string Get(string resourceKey) => resourceKey;
    }

    private static RegistrationViewModel Build(IProfileService service) =>
        new(service, new EchoLocalization());

    [Fact]
    public void It_opens_on_the_name_step_with_the_windows_name_offered()
    {
        var vm = Build(new FakeProfileService());

        Assert.Equal(RegistrationStep.Name, vm.Step);
        Assert.Equal("brawl", vm.Name);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("A", false)]
    [InlineData("Ан", true)]
    [InlineData("  Аня  ", true)]
    public void The_name_step_needs_two_characters(string name, bool expected)
    {
        var vm = Build(new FakeProfileService());
        vm.Name = name;

        Assert.Equal(expected, vm.CanGoNext);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("a@", false)]
    [InlineData("ab.ru", false)]
    [InlineData("a@b.ru", true)]
    public void The_email_step_needs_an_address(string email, bool expected)
    {
        var vm = Build(new FakeProfileService());
        vm.Name = "Аня";
        vm.NextCommand.Execute(null);
        vm.Email = email;

        Assert.Equal(RegistrationStep.Email, vm.Step);
        Assert.Equal(expected, vm.CanGoNext);
    }

    /// <summary>
    /// The email is required here, unlike in the cabinet where it is optional.
    /// </summary>
    /// <remarks>
    /// The reference the owner supplied makes it a required step of its own, and a step somebody
    /// can walk past without typing anything is not a step.
    /// </remarks>
    [Fact]
    public void An_empty_email_does_not_pass_the_email_step()
    {
        var vm = Build(new FakeProfileService());
        vm.Name = "Аня";
        vm.NextCommand.Execute(null);

        Assert.False(vm.CanGoNext);
    }

    [Fact]
    public void Going_back_keeps_what_was_typed()
    {
        var vm = Build(new FakeProfileService());
        vm.Name = "Аня";
        vm.NextCommand.Execute(null);
        vm.Email = "a@b.ru";
        vm.BackCommand.Execute(null);

        Assert.Equal(RegistrationStep.Name, vm.Step);
        Assert.Equal("Аня", vm.Name);

        vm.NextCommand.Execute(null);
        Assert.Equal("a@b.ru", vm.Email);
    }

    private static RegistrationViewModel AtPasswordStep(IProfileService service)
    {
        var vm = new RegistrationViewModel(service, new EchoLocalization())
        {
            Name = "Аня",
        };

        vm.NextCommand.Execute(null);
        vm.Email = "a@b.ru";
        vm.NextCommand.Execute(null);
        return vm;
    }

    [Theory]
    [InlineData("Password1!", "Password1!", true)]
    [InlineData("Password1!", "Password1", false)]
    [InlineData("Password1!", "", false)]
    [InlineData("short1!", "short1!", false)]
    [InlineData("abcdefgh", "abcdefgh", false)]
    public void Finishing_needs_a_matching_acceptable_password(
        string password, string confirm, bool expected)
    {
        var vm = AtPasswordStep(new FakeProfileService());
        vm.Password = password;
        vm.Confirm = confirm;

        Assert.Equal(RegistrationStep.Password, vm.Step);
        Assert.Equal(expected, vm.CanFinish);
    }

    [Fact]
    public void Finishing_registers_the_trimmed_values_and_moves_to_done()
    {
        var service = new FakeProfileService();
        var vm = AtPasswordStep(service);
        vm.Name = "  Аня  ";
        vm.Email = "  a@b.ru  ";
        vm.Password = "Password1!";
        vm.Confirm = "Password1!";

        vm.FinishCommand.Execute(null);

        Assert.Equal(("Аня", "a@b.ru", "Password1!"), service.Registered);
        Assert.Equal(RegistrationStep.Done, vm.Step);
    }

    [Fact]
    public void A_failed_save_says_so_and_stays_on_the_password_step()
    {
        var vm = AtPasswordStep(new FakeProfileService { RegisterSucceeds = false });
        vm.Password = "Password1!";
        vm.Confirm = "Password1!";

        vm.FinishCommand.Execute(null);

        Assert.Equal(RegistrationStep.Password, vm.Step);
        Assert.Equal("Reg_SaveFailed", vm.StatusMessage);
    }

    [Fact]
    public void The_strength_follows_the_password()
    {
        var vm = AtPasswordStep(new FakeProfileService());
        vm.Password = "Abcdefg1!";

        Assert.Equal(4, vm.Strength.Score);
        Assert.True(vm.Strength.IsAcceptable);
    }
}
```

- [ ] **Step 3: Убедиться, что тест падает**

```bash
dotnet test tests/Winora.App.Tests/Winora.App.Tests.csproj --nologo --filter "FullyQualifiedName~RegistrationViewModelTests"
```

Ожидаемо: `CS0246` — `RegistrationViewModel` и `RegistrationStep` не найдены.

- [ ] **Step 4: Открыть регистрацию через интерфейс**

**Тело `Register` уже написано в задаче 3** — там оно понадобилось её же тесту. Добавить его
второй раз значит получить `CS0111`. Здесь добавляется **только член интерфейса**; сверьтесь с
`src/Winora.App/Services/ProfileService.cs` перед правкой.

В `src/Winora.App/Services/ProfileService.cs`, к интерфейсу `IProfileService`:

```csharp
    /// <summary>Creates the profile the registration window filled in.</summary>
    bool Register(string name, string email, string password);
```

Метод в классе уже есть и менять его не нужно. Если его там почему-то нет, он такой:

```csharp
    /// <remarks>
    /// The one place a profile comes into being. The password is hashed here rather than in the
    /// view model, so nothing above this layer ever holds the plain text longer than the keystroke
    /// that produced it.
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
```

Заодно `FakeProfileService` в существующем `ProfileViewModelTests.cs` перестанет
удовлетворять интерфейсу — добавить в него `Register`, возвращающий `true`.

- [ ] **Step 5: Написать модель**

Создать `src/Winora.App/ViewModels/RegistrationViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winora.App.Services;
using Winora.Core.Profile;

namespace Winora.App.ViewModels;

/// <summary>Which of the wizard's screens is showing.</summary>
public enum RegistrationStep
{
    Name = 0,
    Email = 1,
    Password = 2,
    Done = 3,
}

/// <summary>
/// The first-run wizard: name, email, password, done.
/// </summary>
/// <remarks>
/// Knows nothing about windows or animations — it holds what has been typed and what that permits.
/// The window watches Step and moves its own pages; the model never touches a control.
/// </remarks>
public sealed partial class RegistrationViewModel : ObservableObject
{
    private readonly IProfileService _profile;
    private readonly ILocalizationService _text;

    public RegistrationViewModel(IProfileService profile, ILocalizationService text)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _text = text ?? throw new ArgumentNullException(nameof(text));

        // Offered, not imposed: the field is editable and almost always right.
        Name = _profile.SuggestedName;
    }

    /// <remarks>
    /// Partial properties, not fields: MVVMTK0045 requires this form in WinUI 3 so the CsWinRT
    /// generators can emit the WinRT marshalling code.
    /// </remarks>
    [ObservableProperty]
    public partial RegistrationStep Step { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Email { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Confirm { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    public PasswordStrength Strength => PasswordStrengthRules.Evaluate(Password);

    /// <summary>Two characters, matching the reference the owner supplied.</summary>
    public bool IsNameAcceptable => ProfileRules.NormaliseName(Name).Length >= 2;

    /// <summary>
    /// Required here, unlike in the cabinet where it is optional: the reference makes it a step of
    /// its own, and a step somebody can walk past without typing is not a step.
    /// </summary>
    public bool IsEmailAcceptable =>
        Email.Trim().Length > 0 && ProfileRules.IsEmailValid(Email);

    public bool CanGoNext => Step switch
    {
        RegistrationStep.Name => IsNameAcceptable,
        RegistrationStep.Email => IsEmailAcceptable,
        _ => false,
    };

    public bool CanFinish =>
        Step == RegistrationStep.Password &&
        Strength.IsAcceptable &&
        Confirm.Length > 0 &&
        string.Equals(Password, Confirm, StringComparison.Ordinal);

    public bool CanGoBack => Step is RegistrationStep.Email or RegistrationStep.Password;

    partial void OnNameChanged(string value) => Recheck();

    partial void OnEmailChanged(string value) => Recheck();

    partial void OnPasswordChanged(string value)
    {
        OnPropertyChanged(nameof(Strength));
        Recheck();
    }

    partial void OnConfirmChanged(string value) => Recheck();

    partial void OnStepChanged(RegistrationStep value) => Recheck();

    private void Recheck()
    {
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanFinish));
        OnPropertyChanged(nameof(CanGoBack));
    }

    /// <summary>Raised once the profile exists and the app may open.</summary>
    public event EventHandler? Completed;

    [RelayCommand]
    private void Next()
    {
        if (!CanGoNext)
        {
            return;
        }

        StatusMessage = string.Empty;
        Step = Step == RegistrationStep.Name ? RegistrationStep.Email : RegistrationStep.Password;
    }

    [RelayCommand]
    private void Back()
    {
        if (!CanGoBack)
        {
            return;
        }

        StatusMessage = string.Empty;
        Step = Step == RegistrationStep.Password ? RegistrationStep.Email : RegistrationStep.Name;
    }

    [RelayCommand]
    private void Finish()
    {
        if (!CanFinish)
        {
            return;
        }

        if (!_profile.Register(Name, Email, Password))
        {
            // Stays on this step with everything typed still there: a failed save must not cost
            // somebody the three screens they just filled in.
            StatusMessage = _text.Get("Reg_SaveFailed");
            return;
        }

        // The plain text is not kept a moment longer than it takes to hash it.
        Password = string.Empty;
        Confirm = string.Empty;
        Step = RegistrationStep.Done;
    }

    [RelayCommand]
    private void Open() => Completed?.Invoke(this, EventArgs.Empty);
}
```

- [ ] **Step 6: Зарегистрировать**

В `src/Winora.App/Services/ServiceRegistration.cs`, рядом с `ProfileViewModel`:

```csharp
        services.AddTransient<RegistrationViewModel>();
```

- [ ] **Step 7: Убедиться, что всё проходит**

```bash
dotnet test --nologo
```

Ожидаемо: ни одного упавшего.

- [ ] **Step 8: Коммит**

```bash
git add src/Winora.App/ViewModels/RegistrationViewModel.cs src/Winora.App/Services/ProfileService.cs src/Winora.App/Services/ServiceRegistration.cs src/Winora.App/Strings/ru-RU/Resources.resw tests/Winora.App.Tests/ViewModels/RegistrationViewModelTests.cs
git commit -m "feat(registration): the wizard's three steps and what each refuses"
```

---

### Task 5: Окно мастера

**Files:**
- Create: `src/Winora.App/Views/RegistrationWindow.xaml`
- Create: `src/Winora.App/Views/RegistrationWindow.xaml.cs`
- Modify: `src/Winora.App/Resources/Styles/Controls.xaml`

**Interfaces:**
- Consumes: `RegistrationViewModel` из задачи 4.
- Produces: `RegistrationWindow : Window` с событием `event EventHandler? Completed`.

Оформление берётся из образца: тёмная подложка `#070709`, карточка почти чёрная с тонкой рамкой и крупным скруглением, полоса заголовка, шаговый индикатор из трёх кружков, поля со значками, белая кнопка действия. Переходы между шагами — сдвиг по горизонтали с затуханием, около 0,22 с.

- [ ] **Step 1: Написать окно**

Создать `src/Winora.App/Views/RegistrationWindow.xaml`. Разметка длинная; ключевые части:

```xml
<?xml version="1.0" encoding="utf-8"?>
<Window
    x:Class="Winora.App.Views.RegistrationWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:ctl="using:Winora.App.Controls"
    xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
    xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
    mc:Ignorable="d">

    <Grid Background="#070709">
        <!--
          The card, centred and bounded. The reference keeps it narrow on purpose: a registration
          form stretched across a wide monitor reads as a web page, not as a program.
        -->
        <Border Width="520"
                HorizontalAlignment="Center"
                VerticalAlignment="Center"
                Background="#0A0A0C"
                BorderBrush="#1F1F23"
                BorderThickness="1"
                CornerRadius="16">
            <StackPanel>

                <!-- Title bar of the card, matching the reference's window-inside-a-window look. -->
                <Border Background="#111114" BorderBrush="#1F1F23" BorderThickness="0,0,0,1"
                        CornerRadius="16,16,0,0" Padding="20,12">
                    <StackPanel Orientation="Horizontal" HorizontalAlignment="Center" Spacing="8">
                        <FontIcon Glyph="&#xE8FA;" FontFamily="Segoe Fluent Icons" FontSize="14"
                                  Foreground="#9A9AA2" />
                        <TextBlock x:Name="CardTitle" FontSize="12" FontWeight="SemiBold"
                                   Foreground="#C9C9D1" />
                    </StackPanel>
                </Border>

                <StackPanel Padding="32,28,32,32" Spacing="20">
                    <ctl:StepProgress x:Name="Steps" />

                    <!-- One panel per step; the code-behind shows one and animates the swap. -->
                    <Grid x:Name="StepHost" MinHeight="320">
                        <StackPanel x:Name="StepName" Spacing="14" />
                        <StackPanel x:Name="StepEmail" Spacing="14" Visibility="Collapsed" />
                        <StackPanel x:Name="StepPassword" Spacing="14" Visibility="Collapsed" />
                        <StackPanel x:Name="StepDone" Spacing="14" Visibility="Collapsed" />
                    </Grid>
                </StackPanel>
            </StackPanel>
        </Border>
    </Grid>
</Window>
```

Содержимое каждого шага — заголовок, подпись, поля и кнопки — собирается в разметке по образцу из `reference-registration-card.md`, раздел «FULL COMPONENT DETAIL». Тексты берутся из ресурсов ключами `Reg_*`, ни одной строки в коде.

- [ ] **Step 2: Связать окно с моделью**

Создать `src/Winora.App/Views/RegistrationWindow.xaml.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using Winora.App.ViewModels;

namespace Winora.App.Views;

/// <summary>
/// The only window a new person sees, until they have registered.
/// </summary>
/// <remarks>
/// A window rather than a dialog over the shell: the owner asked that the app itself not be visible
/// until registration is done, and a dialog needs a window under it to sit on.
/// </remarks>
public sealed partial class RegistrationWindow : Window
{
    private readonly RegistrationViewModel _model;

    public RegistrationWindow()
    {
        _model = App.Services.GetRequiredService<RegistrationViewModel>();
        InitializeComponent();

        var text = App.Services.GetRequiredService<Services.ILocalizationService>();
        Title = text.Get("Reg_WindowTitle");
        CardTitle.Text = Title;

        ExtendsContentIntoTitleBar = true;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(720, 760));

        _model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(RegistrationViewModel.Step))
            {
                ShowStep(_model.Step);
            }
        };

        _model.Completed += (_, _) => Completed?.Invoke(this, EventArgs.Empty);

        ShowStep(_model.Step);
    }

    /// <summary>Raised when the profile exists and the app may open.</summary>
    public event EventHandler? Completed;

    /// <summary>
    /// Swaps the visible step, sliding the new one in.
    /// </summary>
    /// <remarks>
    /// Done by hand rather than by a transition on a Frame: there are four fixed panels, and a
    /// navigation stack for four panels that never navigate anywhere is more machinery than the
    /// thing it drives.
    /// </remarks>
    private void ShowStep(RegistrationStep step)
    {
        var panels = new[] { StepName, StepEmail, StepPassword, StepDone };

        for (var index = 0; index < panels.Length; index++)
        {
            panels[index].Visibility = index == (int)step
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        Steps.Show(step);
        Slide(panels[(int)step]);
    }

    private static void Slide(UIElement target)
    {
        var transform = new Microsoft.UI.Xaml.Media.TranslateTransform { X = 24 };
        target.RenderTransform = transform;
        target.Opacity = 0;

        var storyboard = new Storyboard();

        var move = new DoubleAnimation
        {
            From = 24,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(220)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        Storyboard.SetTarget(move, transform);
        Storyboard.SetTargetProperty(move, "X");

        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(220)),
        };

        Storyboard.SetTarget(fade, target);
        Storyboard.SetTargetProperty(fade, "Opacity");

        storyboard.Children.Add(move);
        storyboard.Children.Add(fade);
        storyboard.Begin();
    }
}
```

- [ ] **Step 3: Собрать шаговый индикатор**

Создать `src/Winora.App/Controls/StepProgress.xaml` и `.xaml.cs` — три кружка с подписями «Имя», «Почта», «Пароль», соединённые линией, и метод `Show(RegistrationStep step)`, красящий их: пройденный — белый с галочкой, текущий — тёмный с белой обводкой, будущий — тусклый с номером. Заполнение линии меняется вместе с шагом.

- [ ] **Step 4: Собрать и посмотреть**

```bash
dotnet build src/Winora.App/Winora.App.csproj -c Release -p:Platform=x64 --nologo -v q
```

Ожидаемо: молча.

- [ ] **Step 5: Коммит**

```bash
git add src/Winora.App/Views/RegistrationWindow.xaml src/Winora.App/Views/RegistrationWindow.xaml.cs src/Winora.App/Controls/StepProgress.xaml src/Winora.App/Controls/StepProgress.xaml.cs
git commit -m "feat(registration): the dark card, its steps and the slide between them"
```

---

### Task 6: Выбор при старте и удаление старого приветствия

**Files:**
- Modify: `src/Winora.App/App.xaml.cs`
- Modify: `src/Winora.App/MainWindow.xaml.cs`
- Delete: `src/Winora.App/Views/WelcomeDialog.xaml`, `.xaml.cs`, `src/Winora.App/Views/WelcomeOutcome.cs`
- Delete: `tests/Winora.App.Tests/Views/WelcomeOutcomeTests.cs`

**Interfaces:**
- Consumes: `RegistrationWindow`, `IProfileService.Current`.

- [ ] **Step 1: Решать при старте, какое окно создать**

В `src/Winora.App/App.xaml.cs`, в `OnLaunched`, заменить безусловное создание главного окна:

```csharp
        // Registration comes first, and the shell is not created at all until it is done — not
        // created and hidden. A hidden window still shows in the taskbar and in Alt+Tab, which is
        // exactly the "app is visible before registration" the owner asked to remove.
        if (App.Services.GetRequiredService<Services.IProfileService>().Current is null)
        {
            var registration = new Views.RegistrationWindow();
            registration.Completed += (_, _) =>
            {
                new MainWindow().Activate();
                registration.Close();
            };

            registration.Activate();
            return;
        }

        new MainWindow().Activate();
```

- [ ] **Step 2: Убрать вызов приветствия из главного окна**

В `src/Winora.App/MainWindow.xaml.cs` удалить `await ShowWelcomeAsync();` из `OnRootLoaded` и сам метод `ShowWelcomeAsync`. Установочное предложение (`OfferInstallAsync`) остаётся: оно про другое — где лежит файл, а не кто им пользуется.

- [ ] **Step 3: Удалить старое приветствие**

```bash
git rm src/Winora.App/Views/WelcomeDialog.xaml src/Winora.App/Views/WelcomeDialog.xaml.cs src/Winora.App/Views/WelcomeOutcome.cs tests/Winora.App.Tests/Views/WelcomeOutcomeTests.cs
```

Затем убрать из `tests/Winora.App.Tests/Winora.App.Tests.csproj` строку `Compile Include`, ссылающуюся на `WelcomeOutcome.cs`, если она там есть.

- [ ] **Step 4: Прогнать всё**

```bash
dotnet test --nologo
```

Ожидаемо: ни одного упавшего. Тестов станет меньше на четыре — те, что проверяли удалённое.

- [ ] **Step 5: Проверить живьём**

```bash
dotnet publish src/Winora.App/Winora.App.csproj -c Release -p:WinoraPortable=true -p:Platform=x64 -o publish/reg --nologo
```

Затем отложить существующий профиль и запустить:

```bash
powershell -NoProfile -Command "Move-Item \"$env:USERPROFILE\Winora\State\profile.json\" \"$env:USERPROFILE\Winora\State\profile.json.bak\" -Force -ErrorAction SilentlyContinue; Start-Process 'M:\WinoraWork\Winora\publish\reg\Winora.exe'"
```

Проверить глазами:

1. Видно **только** окно регистрации; главного окна нет ни на экране, ни в панели задач.
2. Имя подставлено; «Продолжить» неактивна, пока имя короче двух знаков.
3. На шаге почты работают быстрые домены; негодный адрес краснеет.
4. На шаге пароля галочки требований загораются по мере ввода; «Готово» неактивна, пока пароли не совпали.
5. Под полем пароля стоит строка о том, что восстановить его нельзя.
6. После «Готово» и «Открыть Winora» появляется приложение, окно регистрации исчезает.
7. Повторный запуск открывает приложение сразу, без регистрации.

- [ ] **Step 6: Коммит**

```bash
git add -A
git commit -m "feat(registration): show only the wizard on first run, and delete the old greeting"
```

---

## Порядок и зависимости

```
1 (отпечаток) → 3 (профиль и формат)
2 (сложность) → 4 (модель) → 5 (окно) → 6 (старт и удаление старого)
                 3 ────────────┘
```

Задачи 1 и 2 независимы. Задача 4 требует обеих плюс третью.

## Чего в плане нет, и почему

**Точной разметки шагов в задаче 5.** Она объёмна и механична: заголовок, подпись, поля со значками, кнопки — всё описано в разборе образца, раздел «FULL COMPONENT DETAIL», с точными текстами и правилами подсветки. Переписывать сюда двести строк XAML значило бы завести вторую копию описания, которая разойдётся с первой.

**Проверки окна тестами.** `Window` и `UserControl` в этом проекте не покрыты ничем и не имеют испытательной оснастки. Логика, которую можно проверить, вынесена в модель и в ядро — они покрыты. Окно проверяется живьём, шагом 5 задачи 6.
