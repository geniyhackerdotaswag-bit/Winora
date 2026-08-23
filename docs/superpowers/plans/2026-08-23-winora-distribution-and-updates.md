# Раздача Winora и обновление внутри приложения — план работ

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Посторонний человек скачивает один `.exe`, ставит его одним нажатием и дальше получает новые версии из самого приложения.

**Architecture:** Пять маленьких частей в `src/Winora.System/Updates/`, ни одна не знает про WinUI: разбор версии, лента релизов GitHub, знание о том где программа живёт, проверка и подмена файла, ярлык. Над ними один `UpdateViewModel` в слое приложения и `InfoBar` в оболочке. Релиз собирает GitHub Actions по тегу.

**Tech Stack:** .NET 10, WinUI 3 / Windows App SDK 2.0.4, xunit, CommunityToolkit.Mvvm, `System.Net.Http.Json`, COM `IShellLink`, GitHub Actions.

**Спецификация:** `docs/superpowers/specs/2026-08-23-winora-distribution-and-updates-design.md`

## Global Constraints

- Целевая платформа `net10.0-windows10.0.26100.0`, платформа `x64`, `LangVersion 14.0`, `Nullable enable`, `TreatWarningsAsErrors=true` — предупреждение ломает сборку.
- Репозиторий: `geniyhackerdotaswag-bit/Winora`. Лента: `https://api.github.com/repos/geniyhackerdotaswag-bit/Winora/releases/latest`.
- Имена файлов в релизе — ровно `Winora.exe` и `Winora.exe.sha256`.
- Установленное место — ровно `%LOCALAPPDATA%\Programs\Winora\Winora.exe`.
- **«Не знаю» никогда не превращается в «доступно обновление».** Нет сети, лимит GitHub, изменившийся ответ, отсутствующий файл в релизе — всё это отсутствие обновления, а не его наличие.
- **Ничего не обновляется молча.** Каждая подмена — после нажатия человека.
- В `Winora.System` не должно появиться ни одной ссылки на `Microsoft.UI.*` или `Microsoft.Extensions.*`.
- Комментарии в коде — по-английски, как во всём проекте. Строки на экране — через `Resources.resw`, никогда не в коде.
- Тесты запускаются так: `dotnet test tests/Winora.System.Tests/Winora.System.Tests.csproj --nologo`. Сейчас их 409 и все проходят — это отправная точка, ни один не должен сломаться.

## Файловая карта

| Файл | За что отвечает |
|---|---|
| `src/Winora.System/Updates/AppVersion.cs` | Разбор строки версии в `Version`, приведение к трём числам |
| `src/Winora.System/Updates/AppRelease.cs` | Запись о релизе и `AppUpdateCheck` со свойством «есть ли обновление» |
| `src/Winora.System/Updates/AppReleaseFeed.cs` | Один запрос к GitHub, поиск двух нужных файлов |
| `src/Winora.System/Updates/AppInstallLocation.cs` | Где процесс, где положено, одно ли это |
| `src/Winora.System/Updates/AppDownloadCheck.cs` | Три проверки скачанного файла |
| `src/Winora.System/Updates/AppFileSwap.cs` | Переименование запущенного файла и возврат при сбое |
| `src/Winora.System/Updates/AppUpdater.cs` | Скачать → проверить → подменить → перезапустить |
| `src/Winora.System/Updates/StartMenuShortcut.cs` | `IShellLink` за интерфейсом `IShortcutWriter` |
| `src/Winora.System/Updates/AppInstaller.cs` | Копия в папку программ, ярлык, запуск копии |
| `src/Winora.App/ViewModels/UpdateViewModel.cs` | Состояние полоски, команды «Обновить» и «Проверить» |
| `src/Winora.App/MainWindow.xaml` | `InfoBar` в строке 0 |
| `.github/workflows/release.yml` | Сборка и публикация по тегу |

---

### Task 1: Версия — один источник и осторожный разбор

Сейчас переносимая сборка не знает своей версии: `Directory.Build.props` не задаёт `Version`, поэтому получается `1.0.0`. Тег должен стать единственным источником, а разбор — терпеть всё, что может прийти из чужого поля `tag_name`.

**Files:**
- Modify: `Directory.Build.props`
- Create: `src/Winora.System/Updates/AppVersion.cs`
- Test: `tests/Winora.System.Tests/Updates/AppVersionTests.cs`

**Interfaces:**
- Consumes: ничего.
- Produces: `Winora.System.Updates.AppVersion.Parse(string? text) → Version?` — возвращает версию ровно из трёх чисел (`Major.Minor.Build`) либо `null`.

- [ ] **Step 1: Написать падающий тест**

Создать `tests/Winora.System.Tests/Updates/AppVersionTests.cs`:

```csharp
using Winora.System.Updates;
using Xunit;

namespace Winora.System.Tests.Updates;

/// <summary>
/// Reading a version out of text that came from somewhere else.
/// </summary>
/// <remarks>
/// Two sources feed this and neither is under our control in the same way. The running build
/// supplies AssemblyInformationalVersion, which the SDK may decorate with a commit hash. The feed
/// supplies whatever was typed into a git tag. Both arrive as strings, and the comparison that
/// decides whether to offer an update is only as good as this.
/// </remarks>
public sealed class AppVersionTests
{
    [Theory]
    [InlineData("0.4.0", 0, 4, 0)]
    [InlineData("v0.4.0", 0, 4, 0)]
    [InlineData("V0.4.0", 0, 4, 0)]
    [InlineData("  0.4.0  ", 0, 4, 0)]
    [InlineData("0.4.0+a1b2c3d", 0, 4, 0)]
    [InlineData("0.4.0-beta.1", 0, 4, 0)]
    [InlineData("1.2.3.4", 1, 2, 3)]
    public void A_version_is_read_from_the_text(string text, int major, int minor, int build)
    {
        Assert.Equal(new Version(major, minor, build), AppVersion.Parse(text));
    }

    /// <summary>
    /// Two components mean the third is zero, not "unspecified".
    /// </summary>
    /// <remarks>
    /// This is the trap. Version treats an absent component as -1, so Version.Parse("0.4") compares
    /// as *less* than Version.Parse("0.4.0"). Someone tagging v0.4 while running a build called
    /// 0.4.0 would be told forever that an update is available, and installing it would change
    /// nothing. Everything is normalised to three numbers so that cannot happen.
    /// </remarks>
    [Fact]
    public void A_missing_third_number_is_zero_and_not_less_than_zero()
    {
        Assert.Equal(new Version(0, 4, 0), AppVersion.Parse("0.4"));
        Assert.Equal(AppVersion.Parse("0.4.0"), AppVersion.Parse("0.4"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("v")]
    [InlineData("latest")]
    [InlineData("0")]
    [InlineData("release-2026-08")]
    [InlineData("..")]
    public void Text_that_is_not_a_version_reads_as_nothing(string? text)
    {
        Assert.Null(AppVersion.Parse(text));
    }
}
```

- [ ] **Step 2: Убедиться, что тест падает**

```bash
dotnet test tests/Winora.System.Tests/Winora.System.Tests.csproj --nologo --filter "FullyQualifiedName~AppVersionTests"
```

Ожидаемо: сборка не проходит, `CS0234` — пространства имён `Winora.System.Updates` не существует.

- [ ] **Step 3: Написать разбор**

Создать `src/Winora.System/Updates/AppVersion.cs`:

```csharp
namespace Winora.System.Updates;

/// <summary>Reads a version out of text that came from a build or from a git tag.</summary>
/// <remarks>
/// <para>
/// Everything is normalised to three numbers. <see cref="Version" /> stores an absent component as
/// -1, which makes <c>0.4</c> compare as less than <c>0.4.0</c>; a tag of <c>v0.4</c> against a
/// build called <c>0.4.0</c> would then look like an update forever, and installing it would change
/// nothing. Three numbers always, so the comparison means what it reads as.
/// </para>
/// <para>
/// A fourth number is dropped rather than kept. Releases are named with three, and a build that
/// carries a revision — which MSBuild adds on its own — must not read as newer than the release it
/// was built from.
/// </para>
/// </remarks>
public static class AppVersion
{
    /// <summary>The version in this text, or null when there is not one.</summary>
    public static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Trim();

        // Tags are written v0.4.0; the version inside a build is not.
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        // AssemblyInformationalVersion carries "+<commit>" when SourceLink is on, and a pre-release
        // label after "-". Neither takes part in ordering here: releases are numbered, and a label
        // that changed the comparison would make the answer depend on how the build was tagged.
        var cut = value.IndexOfAny(['+', '-']);
        if (cut >= 0)
        {
            value = value[..cut];
        }

        if (!Version.TryParse(value, out var version))
        {
            return null;
        }

        return new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
    }
}
```

- [ ] **Step 4: Убедиться, что тест проходит**

```bash
dotnet test tests/Winora.System.Tests/Winora.System.Tests.csproj --nologo --filter "FullyQualifiedName~AppVersionTests"
```

Ожидаемо: все зелёные.

- [ ] **Step 5: Задать версию по умолчанию**

В `Directory.Build.props`, внутрь существующего `<PropertyGroup>`, после строки `<RuntimeIdentifiers>win-x64</RuntimeIdentifiers>`:

```xml
    <!--
      The version a local build carries. Releases override it: the workflow passes -p:Version from
      the git tag, and that tag is the only source that matters. This default exists so that a build
      from somebody's working copy has an honest low number instead of the SDK's 1.0.0, which would
      read as newer than every release ever published and hide the update banner forever.
    -->
    <Version>0.0.0</Version>
```

- [ ] **Step 6: Проверить, что версия доходит до сборки**

```bash
dotnet build src/Winora.App/Winora.App.csproj -c Release -p:Platform=x64 -p:Version=9.9.9 --nologo -v q
```

Ожидаемо: сборка проходит. Затем убедиться, что число попало внутрь:

```bash
powershell -NoProfile -Command "(Get-Item 'src/Winora.App/bin/x64/Release/net10.0-windows10.0.26100.0/Winora.App.dll').VersionInfo.ProductVersion"
```

Ожидаемо: `9.9.9`.

- [ ] **Step 7: Коммит**

```bash
git add Directory.Build.props src/Winora.System/Updates/AppVersion.cs tests/Winora.System.Tests/Updates/AppVersionTests.cs
git commit -m "feat(updates): read a version out of a tag without the two-component trap"
```

---

### Task 2: Лента релизов

**Files:**
- Create: `src/Winora.System/Updates/AppRelease.cs`
- Create: `src/Winora.System/Updates/AppReleaseFeed.cs`
- Test: `tests/Winora.System.Tests/Updates/AppReleaseFeedTests.cs`

**Interfaces:**
- Consumes: `AppVersion.Parse` из задачи 1.
- Produces:
  - `sealed record AppRelease(Version Version, string Tag, string Notes, string DownloadUrl, string ChecksumUrl, long SizeBytes, DateTimeOffset PublishedAtUtc)`
  - `sealed record AppUpdateCheck(Version Current, AppRelease? Latest)` со свойством `bool UpdateAvailable`
  - `interface IAppReleaseFeed { Task<AppRelease?> LatestAsync(CancellationToken cancellationToken = default); }`
  - `sealed class AppReleaseFeed : IAppReleaseFeed` с конструкторами `()` и `(HttpClient http, string releasesUrl)`

- [ ] **Step 1: Написать падающий тест**

Создать `tests/Winora.System.Tests/Updates/AppReleaseFeedTests.cs`:

```csharp
using System.Net;
using System.Text;
using Winora.System.Updates;
using Xunit;

namespace Winora.System.Tests.Updates;

/// <summary>
/// Asking GitHub what the newest release is.
/// </summary>
/// <remarks>
/// Nothing here touches the network. The answers are the shapes GitHub actually returns, including
/// the ones that are not answers at all — a rate limit, a release published without its files, a
/// body that is not the JSON we expect. The rule those cases all check is the same one: not knowing
/// is not the same as knowing there is nothing, and neither is an update.
/// </remarks>
public sealed class AppReleaseFeedTests
{
    private const string Url = "https://example.invalid/releases/latest";

    /// <summary>An answer of the shape GitHub gives, with both files attached.</summary>
    private static string Body(string tag) => $$"""
        {
          "tag_name": "{{tag}}",
          "body": "Что нового: полоска обновления.",
          "published_at": "2026-08-23T12:00:00Z",
          "assets": [
            {
              "name": "Winora.exe",
              "size": 92274688,
              "browser_download_url": "https://example.invalid/Winora.exe"
            },
            {
              "name": "Winora.exe.sha256",
              "size": 64,
              "browser_download_url": "https://example.invalid/Winora.exe.sha256"
            }
          ]
        }
        """;

    private static AppReleaseFeed Feed(HttpStatusCode status, string body) =>
        new(new HttpClient(new CannedHandler(status, body)), Url);

    [Fact]
    public async Task The_newest_release_is_read()
    {
        var release = await Feed(HttpStatusCode.OK, Body("v0.4.0")).LatestAsync();

        Assert.NotNull(release);
        Assert.Equal(new Version(0, 4, 0), release.Version);
        Assert.Equal("v0.4.0", release.Tag);
        Assert.Equal("https://example.invalid/Winora.exe", release.DownloadUrl);
        Assert.Equal("https://example.invalid/Winora.exe.sha256", release.ChecksumUrl);
        Assert.Equal(92274688, release.SizeBytes);
        Assert.Contains("полоска", release.Notes);
    }

    /// <summary>
    /// Half a release is worse than none: without the checksum the download cannot be verified, and
    /// offering an update we would then refuse to install wastes the person's time and trust.
    /// </summary>
    [Fact]
    public async Task A_release_missing_its_checksum_is_not_a_release()
    {
        const string body = """
            {
              "tag_name": "v0.4.0",
              "body": "",
              "published_at": "2026-08-23T12:00:00Z",
              "assets": [
                { "name": "Winora.exe", "size": 10, "browser_download_url": "https://example.invalid/Winora.exe" }
              ]
            }
            """;

        Assert.Null(await Feed(HttpStatusCode.OK, body).LatestAsync());
    }

    [Fact]
    public async Task A_release_missing_the_program_is_not_a_release()
    {
        const string body = """
            {
              "tag_name": "v0.4.0",
              "body": "",
              "published_at": "2026-08-23T12:00:00Z",
              "assets": [
                { "name": "Winora.exe.sha256", "size": 64, "browser_download_url": "https://example.invalid/s" }
              ]
            }
            """;

        Assert.Null(await Feed(HttpStatusCode.OK, body).LatestAsync());
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "{\"message\":\"API rate limit exceeded\"}")]
    [InlineData(HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}")]
    [InlineData(HttpStatusCode.OK, "not json at all")]
    [InlineData(HttpStatusCode.OK, "{}")]
    public async Task An_answer_we_cannot_read_is_nothing(HttpStatusCode status, string body)
    {
        Assert.Null(await Feed(status, body).LatestAsync());
    }

    /// <summary>A tag nobody can parse is not a version, and so not an update.</summary>
    [Fact]
    public async Task A_tag_that_is_not_a_version_is_nothing()
    {
        Assert.Null(await Feed(HttpStatusCode.OK, Body("latest")).LatestAsync());
    }

    /// <summary>
    /// The comparison the whole feature turns on. Equal is not an update; older is not an update;
    /// and not knowing is not an update.
    /// </summary>
    [Theory]
    [InlineData("0.4.0", "0.3.0", false)]
    [InlineData("0.4.0", "0.4.0", false)]
    [InlineData("0.3.0", "0.4.0", true)]
    [InlineData("0.4.1", "0.4.0", false)]
    [InlineData("0.4.0", "0.4.1", true)]
    public void An_update_is_offered_only_when_the_release_is_newer(
        string current, string latest, bool expected)
    {
        var release = new AppRelease(
            AppVersion.Parse(latest)!, "v" + latest, string.Empty,
            "https://example.invalid/a", "https://example.invalid/b", 1,
            DateTimeOffset.UnixEpoch);

        var check = new AppUpdateCheck(AppVersion.Parse(current)!, release);

        Assert.Equal(expected, check.UpdateAvailable);
    }

    [Fact]
    public void Not_knowing_is_not_an_update()
    {
        Assert.False(new AppUpdateCheck(new Version(0, 1, 0), null).UpdateAvailable);
    }

    /// <summary>Answers a canned reply without going anywhere.</summary>
    private sealed class CannedHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
```

- [ ] **Step 2: Убедиться, что тест падает**

```bash
dotnet test tests/Winora.System.Tests/Winora.System.Tests.csproj --nologo --filter "FullyQualifiedName~AppReleaseFeedTests"
```

Ожидаемо: `CS0246` — `AppRelease`, `AppUpdateCheck`, `AppReleaseFeed` не найдены.

- [ ] **Step 3: Написать записи**

Создать `src/Winora.System/Updates/AppRelease.cs`:

```csharp
namespace Winora.System.Updates;

/// <param name="Version">The release version, already parsed and normalised to three numbers.</param>
/// <param name="Tag">The tag as written, for showing and for linking to the page.</param>
/// <param name="Notes">What the release says about itself. May be empty.</param>
/// <param name="DownloadUrl">Where <c>Winora.exe</c> is.</param>
/// <param name="ChecksumUrl">Where <c>Winora.exe.sha256</c> is.</param>
/// <param name="SizeBytes">How large the program is, so the screen can say before downloading.</param>
/// <param name="PublishedAtUtc">When it was published.</param>
public sealed record AppRelease(
    Version Version,
    string Tag,
    string Notes,
    string DownloadUrl,
    string ChecksumUrl,
    long SizeBytes,
    DateTimeOffset PublishedAtUtc);

/// <param name="Current">The version running now.</param>
/// <param name="Latest">The newest published release, or null when it could not be read.</param>
public sealed record AppUpdateCheck(Version Current, AppRelease? Latest)
{
    /// <summary>
    /// True only when a release was read and it is genuinely newer.
    /// </summary>
    /// <remarks>
    /// Compared as versions, not as text. The bypass feed compares its tags as strings, and for
    /// somebody else's tags that is right — their format is not ours to assume. These tags are ours,
    /// and string comparison would call every locally built version an update, because a working
    /// copy is almost always ahead of what has been published.
    /// </remarks>
    public bool UpdateAvailable => Latest is not null && Latest.Version > Current;
}
```

- [ ] **Step 4: Написать ленту**

Создать `src/Winora.System/Updates/AppReleaseFeed.cs`:

```csharp
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Winora.System.Updates;

/// <summary>Looks up the newest published Winora release.</summary>
public interface IAppReleaseFeed
{
    /// <summary>The newest release, or null when there is not one we can act on.</summary>
    Task<AppRelease?> LatestAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads the newest release out of the project's own GitHub releases.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately shaped like <see cref="Windows.BypassReleaseInstaller" />, which holds the same
/// conversation with the same service: the user agent GitHub insists on, and the rule that an
/// unreadable answer is never an update. What differs is the comparison, which is by version rather
/// than by text — see <see cref="AppUpdateCheck.UpdateAvailable" />.
/// </para>
/// <para>
/// Returns null for everything that is not a complete, usable release. There is no error to report
/// and nothing for the person to do about it: a check that failed is indistinguishable, from where
/// they sit, from there being no new version, and inventing a difference would only add noise.
/// </para>
/// </remarks>
public sealed class AppReleaseFeed : IAppReleaseFeed
{
    private const string DefaultUrl =
        "https://api.github.com/repos/geniyhackerdotaswag-bit/Winora/releases/latest";

    /// <summary>The program itself, as named in the release.</summary>
    private const string ProgramAsset = "Winora.exe";

    /// <summary>Its checksum, published by the same workflow run.</summary>
    private const string ChecksumAsset = "Winora.exe.sha256";

    private readonly HttpClient _http;
    private readonly string _url;

    public AppReleaseFeed()
        : this(CreateClient(), DefaultUrl)
    {
    }

    public AppReleaseFeed(HttpClient http, string releasesUrl)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        ArgumentException.ThrowIfNullOrWhiteSpace(releasesUrl);
        _url = releasesUrl;
    }

    /// <remarks>GitHub refuses requests without a user agent, with a 403 that looks like a ban.</remarks>
    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Winora");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    public async Task<AppRelease?> LatestAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var release = await _http
                .GetFromJsonAsync<GithubRelease>(_url, cancellationToken)
                .ConfigureAwait(false);

            if (release is null || AppVersion.Parse(release.TagName) is not { } version)
            {
                return null;
            }

            var program = Asset(release, ProgramAsset);
            var checksum = Asset(release, ChecksumAsset);

            // Both or neither. Without the checksum the download cannot be verified, and offering
            // an update that would then be refused wastes the person's time.
            if (program?.DownloadUrl is not { Length: > 0 } programUrl ||
                checksum?.DownloadUrl is not { Length: > 0 } checksumUrl)
            {
                return null;
            }

            return new AppRelease(
                version,
                release.TagName ?? string.Empty,
                release.Body ?? string.Empty,
                programUrl,
                checksumUrl,
                program.Size,
                release.PublishedAt);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // No network, a rate limit, or a changed feed shape. Null means "not known", which
            // AppUpdateCheck deliberately does not turn into "an update is available".
            return null;
        }
    }

    private static GithubAsset? Asset(GithubRelease release, string name) =>
        release.Assets?.FirstOrDefault(asset =>
            string.Equals(asset.Name, name, StringComparison.OrdinalIgnoreCase));

    private sealed record GithubRelease(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("published_at")] DateTimeOffset PublishedAt,
        [property: JsonPropertyName("assets")] IReadOnlyList<GithubAsset>? Assets);

    private sealed record GithubAsset(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("browser_download_url")] string? DownloadUrl);
}
```

- [ ] **Step 5: Убедиться, что тесты проходят**

```bash
dotnet test tests/Winora.System.Tests/Winora.System.Tests.csproj --nologo --filter "FullyQualifiedName~AppReleaseFeedTests"
```

Ожидаемо: все зелёные.

- [ ] **Step 6: Коммит**

```bash
git add src/Winora.System/Updates/AppRelease.cs src/Winora.System/Updates/AppReleaseFeed.cs tests/Winora.System.Tests/Updates/AppReleaseFeedTests.cs
git commit -m "feat(updates): read the newest release, and refuse half of one"
```

---

### Task 3: Где программа живёт

**Files:**
- Create: `src/Winora.System/Updates/AppInstallLocation.cs`
- Test: `tests/Winora.System.Tests/Updates/AppInstallLocationTests.cs`

**Interfaces:**
- Consumes: ничего.
- Produces: `interface IAppInstallLocation { string CurrentExecutablePath { get; } string InstalledDirectory { get; } string InstalledExecutablePath { get; } bool IsInstalled { get; } }` и `sealed class AppInstallLocation : IAppInstallLocation` с конструкторами `()` и `(string currentExecutablePath, string programsRoot)`.

- [ ] **Step 1: Написать падающий тест**

Создать `tests/Winora.System.Tests/Updates/AppInstallLocationTests.cs`:

```csharp
using Winora.System.Updates;
using Xunit;

namespace Winora.System.Tests.Updates;

/// <summary>
/// Whether this copy of the program is the installed one.
/// </summary>
/// <remarks>
/// Everything downstream turns on this answer. Said yes wrongly, the updater would try to replace a
/// file in the Downloads folder that the person never agreed to have replaced. Said no wrongly, an
/// installed copy would keep offering to install itself, over and over, on every launch.
/// </remarks>
public sealed class AppInstallLocationTests
{
    private const string Programs = @"C:\Users\someone\AppData\Local\Programs";

    private static AppInstallLocation At(string current) => new(current, Programs);

    [Fact]
    public void The_installed_place_is_Winora_under_the_programs_folder()
    {
        var location = At(@"C:\Users\someone\Downloads\Winora.exe");

        Assert.Equal(Path.Combine(Programs, "Winora"), location.InstalledDirectory);
        Assert.Equal(Path.Combine(Programs, "Winora", "Winora.exe"), location.InstalledExecutablePath);
    }

    [Fact]
    public void A_copy_in_the_programs_folder_is_installed()
    {
        Assert.True(At(Path.Combine(Programs, "Winora", "Winora.exe")).IsInstalled);
    }

    /// <summary>Case and separators are Windows' business, not a reason to answer differently.</summary>
    [Theory]
    [InlineData(@"C:\Users\someone\AppData\Local\Programs\Winora\WINORA.EXE")]
    [InlineData(@"C:\Users\someone\AppData\Local\Programs\winora\winora.exe")]
    [InlineData(@"C:/Users/someone/AppData/Local/Programs/Winora/Winora.exe")]
    [InlineData(@"C:\Users\someone\AppData\Local\Programs\Winora\..\Winora\Winora.exe")]
    public void The_same_file_written_differently_is_still_installed(string current)
    {
        Assert.True(At(current).IsInstalled);
    }

    [Theory]
    [InlineData(@"C:\Users\someone\Downloads\Winora.exe")]
    [InlineData(@"C:\Users\someone\Desktop\Winora (1).exe")]
    [InlineData(@"C:\Users\someone\AppData\Local\Programs\Winora\Winora.App.exe")]
    [InlineData(@"C:\Users\someone\AppData\Local\Programs\WinoraOld\Winora.exe")]
    public void Anything_else_is_not_installed(string current)
    {
        Assert.False(At(current).IsInstalled);
    }

    /// <summary>
    /// The build output is called Winora.App.exe and the release is called Winora.exe. Keeping the
    /// name part of the answer means a build run from the debugger never counts as installed, and
    /// development never tries to update itself.
    /// </summary>
    [Fact]
    public void The_build_output_name_is_not_the_release_name()
    {
        Assert.False(At(Path.Combine(Programs, "Winora", "Winora.App.exe")).IsInstalled);
    }

    /// <summary>
    /// The parameterless constructor resolves real folders rather than throwing or returning empty.
    /// </summary>
    /// <remarks>
    /// Comparing CurrentExecutablePath against Environment.ProcessPath would restate the line that
    /// produces it and could never fail. What is worth asserting is what the rest of the code
    /// assumes: that the paths come out rooted, under the real local app data folder, and named the
    /// way an installed copy is named.
    /// </remarks>
    [Fact]
    public void The_real_one_resolves_to_real_folders()
    {
        var location = new AppInstallLocation();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.True(Path.IsPathRooted(location.InstalledExecutablePath));
        Assert.StartsWith(localAppData, location.InstalledDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Winora.exe", Path.GetFileName(location.InstalledExecutablePath));
        Assert.Equal(location.InstalledDirectory, Path.GetDirectoryName(location.InstalledExecutablePath));
    }
}
```

- [ ] **Step 2: Убедиться, что тест падает**

```bash
dotnet test tests/Winora.System.Tests/Winora.System.Tests.csproj --nologo --filter "FullyQualifiedName~AppInstallLocationTests"
```

Ожидаемо: `CS0246` — `AppInstallLocation` не найден.

- [ ] **Step 3: Написать**

Создать `src/Winora.System/Updates/AppInstallLocation.cs`:

```csharp
namespace Winora.System.Updates;

/// <summary>Where this copy of the program is, and where an installed one belongs.</summary>
public interface IAppInstallLocation
{
    /// <summary>The file this process was started from.</summary>
    string CurrentExecutablePath { get; }

    /// <summary>The folder an installed copy lives in.</summary>
    string InstalledDirectory { get; }

    /// <summary>The file an installed copy is.</summary>
    string InstalledExecutablePath { get; }

    /// <summary>True when this process is running from the installed place, under that name.</summary>
    bool IsInstalled { get; }
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// The name is part of the answer, not only the folder. The build produces
/// <c>Winora.App.exe</c> and the release is published as <c>Winora.exe</c>; requiring both to match
/// means a build run out of the debugger is never mistaken for an installed copy, and development
/// never tries to update itself out from under the debugger.
/// </para>
/// <para>
/// <c>%LOCALAPPDATA%\Programs</c> and not <c>Program Files</c>: that folder belongs to the user, so
/// installing and later replacing a file there needs no administrator rights. Winora asks for
/// elevation for the operations that genuinely require it and for nothing else, and putting itself
/// somewhere that made every update an elevation prompt would break that.
/// </para>
/// </remarks>
public sealed class AppInstallLocation : IAppInstallLocation
{
    /// <summary>The folder name, and the file name, an installed copy uses.</summary>
    private const string ProductName = "Winora";

    private const string ExecutableName = "Winora.exe";

    public AppInstallLocation()
        : this(
            Environment.ProcessPath ?? string.Empty,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs"))
    {
    }

    public AppInstallLocation(string currentExecutablePath, string programsRoot)
    {
        ArgumentNullException.ThrowIfNull(currentExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(programsRoot);

        CurrentExecutablePath = currentExecutablePath;
        InstalledDirectory = Path.Combine(programsRoot, ProductName);
        InstalledExecutablePath = Path.Combine(InstalledDirectory, ExecutableName);
    }

    public string CurrentExecutablePath { get; }

    public string InstalledDirectory { get; }

    public string InstalledExecutablePath { get; }

    public bool IsInstalled => Same(CurrentExecutablePath, InstalledExecutablePath);

    /// <summary>
    /// Whether two paths name the same file, as Windows would judge it.
    /// </summary>
    /// <remarks>
    /// Compared after <see cref="Path.GetFullPath(string)" />, which settles forward slashes and
    /// <c>..</c> segments, and ignoring case, which is what the file system does. Comparing the
    /// strings as typed would answer "not installed" for a path that differs only in how somebody
    /// wrote it, and the program would offer to install itself on top of itself.
    /// </remarks>
    private static bool Same(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0)
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // A path the file system will not even parse is not the installed one.
            return false;
        }
    }
}
```

- [ ] **Step 4: Убедиться, что тесты проходят**

```bash
dotnet test tests/Winora.System.Tests/Winora.System.Tests.csproj --nologo --filter "FullyQualifiedName~AppInstallLocationTests"
```

Ожидаемо: все зелёные.

- [ ] **Step 5: Коммит**

```bash
git add src/Winora.System/Updates/AppInstallLocation.cs tests/Winora.System.Tests/Updates/AppInstallLocationTests.cs
git commit -m "feat(updates): know whether this copy is the installed one"
```

---

### Task 4: Проверка скачанного и подмена файла

Две вещи в одной задаче намеренно: проверка существует только чтобы решить, можно ли подменять, и рецензировать их порознь бессмысленно.

**Files:**
- Create: `src/Winora.System/Updates/AppDownloadCheck.cs`
- Create: `src/Winora.System/Updates/AppFileSwap.cs`
- Test: `tests/Winora.System.Tests/Updates/AppDownloadCheckTests.cs`
- Test: `tests/Winora.System.Tests/Updates/AppFileSwapTests.cs`

**Interfaces:**
- Consumes: ничего.
- Produces:
  - `enum DownloadVerdict { Ok, WrongSize, WrongHash, NotAnExecutable, Unreadable }`
  - `static class AppDownloadCheck { static DownloadVerdict Verify(string path, long expectedSize, string? expectedSha256); }`
  - `static class AppFileSwap { static bool Replace(string target, string fresh); static void RemoveLeftovers(string directory); const string OldSuffix = ".old"; }`

- [ ] **Step 1: Написать падающий тест на проверку**

Создать `tests/Winora.System.Tests/Updates/AppDownloadCheckTests.cs`:

```csharp
using System.Security.Cryptography;
using Winora.System.Updates;
using Xunit;

namespace Winora.System.Tests.Updates;

/// <summary>
/// Deciding whether what arrived is safe to put in place of the running program.
/// </summary>
/// <remarks>
/// Three checks, and each one exists because of a different way this goes wrong. The size catches a
/// connection that dropped and a disk that filled. The hash catches bytes that changed on the way.
/// The signature catches the case where the download succeeded perfectly and delivered a web page —
/// a proxy sign-in, a rate-limit notice, an error page — which is the most common of the three and
/// the only one the other two can miss.
/// </remarks>
public sealed class AppDownloadCheckTests : IDisposable
{
    private readonly string _folder;

    public AppDownloadCheckTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "winora-check-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>Bytes that start like a Windows executable, because that is what is being checked.</summary>
    private static byte[] Executable(int length)
    {
        var bytes = new byte[length];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        for (var index = 2; index < length; index++)
        {
            bytes[index] = (byte)(index % 251);
        }

        return bytes;
    }

    private string Write(byte[] content)
    {
        var path = Path.Combine(_folder, "Winora.exe.new");
        File.WriteAllBytes(path, content);
        return path;
    }

    private static string Hash(byte[] content) => Convert.ToHexString(SHA256.HashData(content));

    [Fact]
    public void A_good_download_passes()
    {
        var content = Executable(4096);
        var path = Write(content);

        Assert.Equal(DownloadVerdict.Ok, AppDownloadCheck.Verify(path, content.Length, Hash(content)));
    }

    /// <summary>The hash may be written the way sha256sum writes it: lower case, then the name.</summary>
    [Fact]
    public void The_hash_is_read_however_the_tool_wrote_it()
    {
        var content = Executable(4096);
        var path = Write(content);
        var written = Hash(content).ToLowerInvariant() + "  Winora.exe\n";

        Assert.Equal(DownloadVerdict.Ok, AppDownloadCheck.Verify(path, content.Length, written));
    }

    [Fact]
    public void A_short_file_is_caught_by_its_size()
    {
        var content = Executable(4096);
        var path = Write(content);

        Assert.Equal(DownloadVerdict.WrongSize, AppDownloadCheck.Verify(path, 8192, Hash(content)));
    }

    [Fact]
    public void Changed_bytes_are_caught_by_the_hash()
    {
        var content = Executable(4096);
        var path = Write(content);
        var other = Executable(4096);
        other[100] ^= 0xFF;

        Assert.Equal(DownloadVerdict.WrongHash, AppDownloadCheck.Verify(path, content.Length, Hash(other)));
    }

    /// <summary>
    /// The case the other two checks cannot see: a complete, uncorrupted file that is not a program.
    /// </summary>
    [Fact]
    public void A_web_page_instead_of_a_program_is_caught_by_its_first_two_bytes()
    {
        var page = "<!doctype html><title>Sign in</title>"u8.ToArray();
        var path = Write(page);

        Assert.Equal(
            DownloadVerdict.NotAnExecutable,
            AppDownloadCheck.Verify(path, page.Length, Hash(page)));
    }

    [Fact]
    public void An_empty_file_is_not_an_executable()
    {
        var path = Write([]);

        Assert.Equal(DownloadVerdict.NotAnExecutable, AppDownloadCheck.Verify(path, 0, Hash([])));
    }

    [Fact]
    public void A_file_that_is_not_there_is_unreadable()
    {
        Assert.Equal(
            DownloadVerdict.Unreadable,
            AppDownloadCheck.Verify(Path.Combine(_folder, "absent.exe"), 1, new string('0', 64)));
    }

    /// <summary>A checksum file with nothing usable in it fails rather than passing by accident.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-hash")]
    public void An_unusable_checksum_never_passes(string checksum)
    {
        var content = Executable(4096);
        var path = Write(content);

        Assert.Equal(DownloadVerdict.WrongHash, AppDownloadCheck.Verify(path, content.Length, checksum));
    }
}
```

- [ ] **Step 2: Убедиться, что тест падает**

```bash
dotnet test tests/Winora.System.Tests/Winora.System.Tests.csproj --nologo --filter "FullyQualifiedName~AppDownloadCheckTests"
```

Ожидаемо: `CS0103` — `AppDownloadCheck` не найден.

- [ ] **Step 3: Написать проверку**

Создать `src/Winora.System/Updates/AppDownloadCheck.cs`:

```csharp
using System.Security.Cryptography;

namespace Winora.System.Updates;

/// <summary>What was decided about a downloaded file.</summary>
public enum DownloadVerdict
{
    /// <summary>Safe to put in place.</summary>
    Ok,

    /// <summary>Not the length the release said. A dropped connection, or a full disk.</summary>
    WrongSize,

    /// <summary>The right length, the wrong bytes.</summary>
    WrongHash,

    /// <summary>Not a Windows program at all — most often a web page.</summary>
    NotAnExecutable,

    /// <summary>Could not be read to judge.</summary>
    Unreadable,
}

/// <summary>
/// Decides whether a downloaded file may replace the running program.
/// </summary>
/// <remarks>
/// <para>
/// What this protects against is a broken download, not a forged release. The checksum is published
/// by the same workflow run, in the same release, and served from the same host as the program: it
/// cannot vouch for the program's origin, only for its arrival. The origin is vouched for by HTTPS
/// to GitHub, and calling the checksum a defence against tampering would be a claim this code does
/// not support.
/// </para>
/// <para>
/// The order is deliberate: size first because it costs nothing, then the two bytes at the front,
/// then the hash, which is the only one that reads the whole file.
/// </para>
/// </remarks>
public static class AppDownloadCheck
{
    /// <summary>The first two bytes of every Windows executable.</summary>
    private static ReadOnlySpan<byte> ExecutableSignature => "MZ"u8;

    /// <param name="path">The downloaded file.</param>
    /// <param name="expectedSize">The length the release said it would be.</param>
    /// <param name="expectedSha256">
    /// The contents of the checksum file. May be bare hex, or hex followed by a file name the way
    /// <c>sha256sum</c> writes it.
    /// </param>
    public static DownloadVerdict Verify(string path, long expectedSize, string? expectedSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                return DownloadVerdict.Unreadable;
            }

            if (file.Length != expectedSize)
            {
                return DownloadVerdict.WrongSize;
            }

            if (!StartsLikeAProgram(path))
            {
                return DownloadVerdict.NotAnExecutable;
            }

            return HashMatches(path, expectedSha256)
                ? DownloadVerdict.Ok
                : DownloadVerdict.WrongHash;
        }
        catch (Exception)
        {
            // Locked, gone, or on a disk that stopped answering. Not a reason to throw out of a
            // download, and certainly not a reason to install it.
            return DownloadVerdict.Unreadable;
        }
    }

    private static bool StartsLikeAProgram(string path)
    {
        using var stream = File.OpenRead(path);

        Span<byte> head = stackalloc byte[2];
        return stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length &&
               head.SequenceEqual(ExecutableSignature);
    }

    private static bool HashMatches(string path, string? expected)
    {
        // sha256sum writes "<hex>  <name>". Only the first word is the hash.
        var wanted = expected?.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

        if (wanted is not { Length: 64 })
        {
            return false;
        }

        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream));

        return string.Equals(actual, wanted, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 4: Убедиться, что тесты проходят**

```bash
dotnet test tests/Winora.System.Tests/Winora.System.Tests.csproj --nologo --filter "FullyQualifiedName~AppDownloadCheckTests"
```

Ожидаемо: все зелёные.

- [ ] **Step 5: Написать падающий тест на подмену**

Создать `tests/Winora.System.Tests/Updates/AppFileSwapTests.cs`:

```csharp
using Winora.System.Updates;
using Xunit;

namespace Winora.System.Tests.Updates;

/// <summary>
/// Putting a new file where the running one is.
/// </summary>
/// <remarks>
/// Windows will not let a running program be deleted or overwritten, but it will let it be renamed.
/// That single permission is what makes this possible without a second program to do the work, and
/// the order below is arranged around it: nothing is destroyed until the rename has already
/// succeeded, and the step after it is reversible because the renamed file is still a working
/// program.
/// </remarks>
public sealed class AppFileSwapTests : IDisposable
{
    private readonly string _folder;
    private readonly string _target;
    private readonly string _fresh;

    public AppFileSwapTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "winora-swap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        _target = Path.Combine(_folder, "Winora.exe");
        _fresh = Path.Combine(_folder, "Winora.exe.new");

        File.WriteAllText(_target, "old program");
        File.WriteAllText(_fresh, "new program");
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

    [Fact]
    public void The_new_file_takes_the_place_of_the_old_one()
    {
        Assert.True(AppFileSwap.Replace(_target, _fresh));

        Assert.Equal("new program", File.ReadAllText(_target));
        Assert.False(File.Exists(_fresh));
    }

    /// <summary>The displaced program is kept, not deleted: until the new one runs it is the fallback.</summary>
    [Fact]
    public void The_old_program_is_set_aside_rather_than_destroyed()
    {
        AppFileSwap.Replace(_target, _fresh);

        Assert.Equal("old program", File.ReadAllText(_target + AppFileSwap.OldSuffix));
    }

    /// <summary>A leftover from a previous update must not stop the next one.</summary>
    [Fact]
    public void A_leftover_from_last_time_does_not_block_the_swap()
    {
        File.WriteAllText(_target + AppFileSwap.OldSuffix, "from last time");

        Assert.True(AppFileSwap.Replace(_target, _fresh));
        Assert.Equal("new program", File.ReadAllText(_target));
    }

    /// <summary>
    /// Nothing is touched when there is nothing to put in place. Said the other way: a failure
    /// before the rename must leave a working program where it was.
    /// </summary>
    [Fact]
    public void Without_a_new_file_the_old_one_stays_exactly_where_it_was()
    {
        File.Delete(_fresh);

        Assert.False(AppFileSwap.Replace(_target, _fresh));
        Assert.Equal("old program", File.ReadAllText(_target));
        Assert.False(File.Exists(_target + AppFileSwap.OldSuffix));
    }

    /// <summary>
    /// The step the whole order exists for: if putting the new file in place fails, the working
    /// program comes back.
    /// </summary>
    /// <remarks>
    /// Forced by holding the downloaded file open with no sharing, which is what an antivirus
    /// scanning it at that exact moment looks like from here. The rename of the target has already
    /// happened by then, so this is the one window where the program is not where it belongs, and
    /// it must not be left that way.
    /// </remarks>
    [Fact]
    public void A_swap_that_fails_puts_the_working_program_back()
    {
        using (var held = new FileStream(_fresh, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(AppFileSwap.Replace(_target, _fresh));
        }

        Assert.True(File.Exists(_target));
        Assert.Equal("old program", File.ReadAllText(_target));
        Assert.False(File.Exists(_target + AppFileSwap.OldSuffix));
    }

    [Fact]
    public void Leftovers_are_cleared_away()
    {
        File.WriteAllText(Path.Combine(_folder, "Winora.exe.old"), "gone");
        File.WriteAllText(Path.Combine(_folder, "Winora.exe.new"), "gone too");

        AppFileSwap.RemoveLeftovers(_folder);

        Assert.False(File.Exists(Path.Combine(_folder, "Winora.exe.old")));
        Assert.False(File.Exists(Path.Combine(_folder, "Winora.exe.new")));
        Assert.True(File.Exists(_target));
    }

    /// <summary>
    /// A leftover that cannot be removed is not a reason to fail. It is removed next time, and a
    /// program that refused to start because of a stale file would be worse than the stale file.
    /// </summary>
    [Fact]
    public void A_leftover_that_will_not_go_is_not_an_error()
    {
        var stuck = Path.Combine(_folder, "Winora.exe.old");
        File.WriteAllText(stuck, "held open");

        using var hold = new FileStream(stuck, FileMode.Open, FileAccess.Read, FileShare.None);

        AppFileSwap.RemoveLeftovers(_folder);

        Assert.True(File.Exists(stuck));
    }

    [Fact]
    public void Clearing_a_folder_that_is_not_there_is_not_an_error()
    {
        AppFileSwap.RemoveLeftovers(Path.Combine(_folder, "absent"));
    }
}
```

- [ ] **Step 6: Убедиться, что тест падает**

```bash
dotnet test tests/Winora.System.Tests/Winora.System.Tests.csproj --nologo --filter "FullyQualifiedName~AppFileSwapTests"
```

Ожидаемо: `CS0103` — `AppFileSwap` не найден.

- [ ] **Step 7: Написать подмену**

Создать `src/Winora.System/Updates/AppFileSwap.cs`:

```csharp
namespace Winora.System.Updates;

/// <summary>
/// Puts a downloaded program in the place of the running one.
/// </summary>
/// <remarks>
/// <para>
/// Windows refuses to delete or overwrite a running executable, but allows it to be renamed: the
/// loader opens the image with FILE_SHARE_DELETE, and a rename needs only that. This is why no
/// second program is needed to perform the update — a helper that waits for the first to exit is the
/// usual arrangement, and it is one more executable to ship, sign, and explain to an antivirus.
/// </para>
/// <para>
/// The order matters more than the mechanism. Up to the rename nothing has been destroyed, so any
/// failure leaves the program exactly as it was. The rename itself is reversible, because what it
/// produced is still the working program. Only after both have succeeded is there a moment where
/// the new file is in place, and by then there is nothing left to undo.
/// </para>
/// </remarks>
public static class AppFileSwap
{
    /// <summary>What the displaced program is renamed to.</summary>
    public const string OldSuffix = ".old";

    /// <summary>What a download in progress is called.</summary>
    public const string FreshSuffix = ".new";

    /// <summary>Replaces <paramref name="target" /> with <paramref name="fresh" />.</summary>
    /// <returns>True when the new file is now in place.</returns>
    public static bool Replace(string target, string fresh)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(fresh);

        if (!File.Exists(fresh) || !File.Exists(target))
        {
            return false;
        }

        var displaced = target + OldSuffix;

        try
        {
            // A leftover from a previous update would make the rename below fail. It is not needed:
            // whatever it holds has already been superseded once.
            TryDelete(displaced);

            File.Move(target, displaced);
        }
        catch (Exception)
        {
            // Nothing has moved. The program is where it was and still runs.
            return false;
        }

        try
        {
            File.Move(fresh, target);
            return true;
        }
        catch (Exception)
        {
            // Put the working program back. If even this fails there is nothing further to try, and
            // the caller is told the update did not happen either way.
            try
            {
                File.Move(displaced, target);
            }
            catch (Exception)
            {
                // Reported as a failed update; the displaced file is still beside it by name.
            }

            return false;
        }
    }

    /// <summary>
    /// Clears away what previous updates left behind.
    /// </summary>
    /// <remarks>
    /// Called at startup, when the displaced program is no longer running and can finally be
    /// deleted. Failure is silent on purpose: a file still held open is removed the next time, and a
    /// program that refused to start over a stale file would be worse than the stale file.
    /// </remarks>
    public static void RemoveLeftovers(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*" + OldSuffix)
                         .Concat(Directory.EnumerateFiles(directory, "*" + FreshSuffix)))
            {
                TryDelete(file);
            }
        }
        catch (Exception)
        {
            // Same reasoning: never a reason to fail a startup.
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
            // Held open by something. Next time.
        }
    }
}
```

- [ ] **Step 8: Убедиться, что все тесты проходят**

```bash
dotnet test tests/Winora.System.Tests/Winora.System.Tests.csproj --nologo
```

Ожидаемо: 409 прежних плюс новые, ни одного упавшего.

- [ ] **Step 9: Коммит**

```bash
git add src/Winora.System/Updates/AppDownloadCheck.cs src/Winora.System/Updates/AppFileSwap.cs tests/Winora.System.Tests/Updates/AppDownloadCheckTests.cs tests/Winora.System.Tests/Updates/AppFileSwapTests.cs
git commit -m "feat(updates): refuse a bad download, and swap a running file safely"
```

---

### Task 5: Скачивание и весь ход обновления

**Files:**
- Create: `src/Winora.System/Updates/AppUpdater.cs`
- Test: `tests/Winora.System.Tests/Updates/AppUpdaterTests.cs`

**Interfaces:**
- Consumes: `AppRelease`, `AppDownloadCheck.Verify`, `AppFileSwap.Replace`, `IAppInstallLocation`.
- Produces:
  - `enum UpdateOutcome { Installed, DownloadFailed, Verification, SwapFailed, NotInstalled }`
  - `interface IAppUpdater { Task<UpdateOutcome> UpdateAsync(AppRelease release, IProgress<double>? progress, CancellationToken cancellationToken = default); void RemoveLeftovers(); bool Restart(); }`
  - `sealed class AppUpdater : IAppUpdater` с конструкторами `(IAppInstallLocation location)` и `(IAppInstallLocation location, HttpClient http)`

- [ ] **Step 1: Написать падающий тест**

Создать `tests/Winora.System.Tests/Updates/AppUpdaterTests.cs`:

```csharp
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Winora.System.Updates;
using Xunit;

namespace Winora.System.Tests.Updates;

/// <summary>
/// The whole run: download, judge, put in place.
/// </summary>
/// <remarks>
/// Nothing here restarts anything. Starting the new program is the last step and the one step that
/// cannot be observed from inside the test that asked for it; what is checked is everything up to
/// it, and above all what is left on disk when a step goes wrong. The rule every case below shares:
/// a failed update leaves a program that still runs.
/// </remarks>
public sealed class AppUpdaterTests : IDisposable
{
    private readonly string _folder;
    private readonly string _target;

    public AppUpdaterTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "winora-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        _target = Path.Combine(_folder, "Winora.exe");
        File.WriteAllText(_target, "the program that is running");
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

    private static byte[] Program(int length)
    {
        var bytes = new byte[length];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        for (var index = 2; index < length; index++)
        {
            bytes[index] = (byte)(index % 251);
        }

        return bytes;
    }

    /// <summary>The place the updater believes it is installed: our temp folder, not the real one.</summary>
    private sealed class HereLocation(string current) : IAppInstallLocation
    {
        public string CurrentExecutablePath => current;

        public string InstalledDirectory => Path.GetDirectoryName(current)!;

        public string InstalledExecutablePath => current;

        public bool IsInstalled { get; init; } = true;
    }

    private AppUpdater Updater(byte[] program, string checksum, bool installed = true) =>
        new(
            new HereLocation(_target) { IsInstalled = installed },
            new HttpClient(new TwoFileHandler(program, checksum)));

    private static AppRelease Release(long size) => new(
        new Version(0, 4, 0), "v0.4.0", "notes",
        "https://example.invalid/Winora.exe",
        "https://example.invalid/Winora.exe.sha256",
        size,
        DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task A_good_update_lands()
    {
        var program = Program(2048);
        var updater = Updater(program, Convert.ToHexString(SHA256.HashData(program)));

        Assert.Equal(UpdateOutcome.Installed, await updater.UpdateAsync(Release(program.Length), null));
        Assert.Equal(program, File.ReadAllBytes(_target));
    }

    /// <summary>
    /// Progress is reported while the download runs, and the last report is the end of it.
    /// </summary>
    /// <remarks>
    /// Recorded through a synchronous IProgress rather than Progress&lt;T&gt;, which hands its
    /// callbacks to the synchronization context and would leave the assertions racing the reports.
    /// A test that needed a sleep to pass would be a test that fails on a loaded machine.
    /// </remarks>
    [Fact]
    public async Task Progress_is_reported_and_ends_at_the_end()
    {
        var program = Program(200_000);
        var updater = Updater(program, Convert.ToHexString(SHA256.HashData(program)));
        var seen = new SynchronousProgress();

        await updater.UpdateAsync(Release(program.Length), seen);

        Assert.NotEmpty(seen.Values);
        Assert.All(seen.Values, value => Assert.InRange(value, 0d, 1d));
        Assert.Equal(1d, seen.Values[^1], 3);
    }

    /// <summary>The three refusals of AppDownloadCheck, each leaving the program untouched.</summary>
    [Fact]
    public async Task A_download_that_does_not_verify_changes_nothing()
    {
        var program = Program(2048);
        var updater = Updater(program, new string('a', 64));

        Assert.Equal(
            UpdateOutcome.Verification,
            await updater.UpdateAsync(Release(program.Length), null));

        Assert.Equal("the program that is running", File.ReadAllText(_target));
    }

    [Fact]
    public async Task A_web_page_instead_of_a_program_changes_nothing()
    {
        var page = Encoding.UTF8.GetBytes("<!doctype html><title>Sign in</title>");
        var updater = Updater(page, Convert.ToHexString(SHA256.HashData(page)));

        Assert.Equal(UpdateOutcome.Verification, await updater.UpdateAsync(Release(page.Length), null));
        Assert.Equal("the program that is running", File.ReadAllText(_target));
    }

    [Fact]
    public async Task A_download_that_fails_changes_nothing()
    {
        var updater = new AppUpdater(
            new HereLocation(_target),
            new HttpClient(new FailingHandler()));

        Assert.Equal(UpdateOutcome.DownloadFailed, await updater.UpdateAsync(Release(2048), null));
        Assert.Equal("the program that is running", File.ReadAllText(_target));
    }

    /// <summary>
    /// A copy running from wherever it was downloaded is never replaced. Nobody agreed to that file
    /// being changed, and the folder it sits in is not ours to write into.
    /// </summary>
    [Fact]
    public async Task A_copy_that_is_not_installed_is_never_replaced()
    {
        var program = Program(2048);
        var updater = Updater(program, Convert.ToHexString(SHA256.HashData(program)), installed: false);

        Assert.Equal(
            UpdateOutcome.NotInstalled,
            await updater.UpdateAsync(Release(program.Length), null));

        Assert.Equal("the program that is running", File.ReadAllText(_target));
    }

    /// <summary>Nothing half-written is left behind when the update is refused.</summary>
    [Fact]
    public async Task A_refused_update_leaves_no_debris()
    {
        var program = Program(2048);
        var updater = Updater(program, new string('a', 64));

        await updater.UpdateAsync(Release(program.Length), null);

        Assert.False(File.Exists(_target + AppFileSwap.FreshSuffix));
    }

    /// <summary>Records every report on the thread that made it, so nothing has to be waited for.</summary>
    private sealed class SynchronousProgress : IProgress<double>
    {
        private readonly List<double> _values = [];

        public IReadOnlyList<double> Values => _values;

        public void Report(double value) => _values.Add(value);
    }

    /// <summary>Serves the program on one address and its checksum on the other.</summary>
    private sealed class TwoFileHandler(byte[] program, string checksum) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();

            HttpContent content = url.EndsWith(".sha256", StringComparison.Ordinal)
                ? new StringContent(checksum, Encoding.ASCII)
                : new ByteArrayContent(program);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("no network");
    }
}
```

- [ ] **Step 2: Убедиться, что тест падает**

```bash
dotnet test tests/Winora.System.Tests/Winora.System.Tests.csproj --nologo --filter "FullyQualifiedName~AppUpdaterTests"
```

Ожидаемо: `CS0246` — `AppUpdater` и `UpdateOutcome` не найдены.

- [ ] **Step 3: Написать**

Создать `src/Winora.System/Updates/AppUpdater.cs`:

```csharp
using System.Diagnostics;

namespace Winora.System.Updates;

/// <summary>How an update attempt ended.</summary>
public enum UpdateOutcome
{
    /// <summary>The new program is in place and the process may now restart into it.</summary>
    Installed,

    /// <summary>Nothing arrived. The program is unchanged.</summary>
    DownloadFailed,

    /// <summary>What arrived was not what was promised. The program is unchanged.</summary>
    Verification,

    /// <summary>The file could not be put in place. The program is unchanged.</summary>
    SwapFailed,

    /// <summary>This copy is not the installed one, so it is not ours to replace.</summary>
    NotInstalled,
}

/// <summary>Downloads a release and puts it in the place of the running program.</summary>
public interface IAppUpdater
{
    Task<UpdateOutcome> UpdateAsync(
        AppRelease release,
        IProgress<double>? progress,
        CancellationToken cancellationToken = default);

    /// <summary>Clears away what a previous update left behind. Called once at startup.</summary>
    void RemoveLeftovers();

    /// <summary>Starts the program that is now in place and asks this process to end.</summary>
    bool Restart();
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// Nothing happens here without somebody having pressed a button. Winora changes the machine it runs
/// on and asks for administrator rights to do it; a program of that kind replacing its own
/// executable in the background is not something a person can agree to after the fact. The same
/// reasoning is already written down for the bypass download, and it applies with more force to
/// Winora's own file.
/// </para>
/// <para>
/// A copy that is not the installed one is refused outright. It is sitting wherever its owner put
/// it — usually the Downloads folder — and rewriting a file there would be changing something nobody
/// offered up.
/// </para>
/// </remarks>
public sealed class AppUpdater : IAppUpdater
{
    private readonly IAppInstallLocation _location;
    private readonly HttpClient _http;

    public AppUpdater(IAppInstallLocation location)
        : this(location, CreateClient())
    {
    }

    public AppUpdater(IAppInstallLocation location, HttpClient http)
    {
        _location = location ?? throw new ArgumentNullException(nameof(location));
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Winora");
        return http;
    }

    public async Task<UpdateOutcome> UpdateAsync(
        AppRelease release,
        IProgress<double>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);

        if (!_location.IsInstalled)
        {
            return UpdateOutcome.NotInstalled;
        }

        var target = _location.InstalledExecutablePath;
        var fresh = target + AppFileSwap.FreshSuffix;

        try
        {
            string checksum;
            try
            {
                await DownloadAsync(release.DownloadUrl, fresh, release.SizeBytes, progress, cancellationToken)
                    .ConfigureAwait(false);

                checksum = await _http.GetStringAsync(release.ChecksumUrl, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return UpdateOutcome.DownloadFailed;
            }

            if (AppDownloadCheck.Verify(fresh, release.SizeBytes, checksum) != DownloadVerdict.Ok)
            {
                return UpdateOutcome.Verification;
            }

            return AppFileSwap.Replace(target, fresh)
                ? UpdateOutcome.Installed
                : UpdateOutcome.SwapFailed;
        }
        finally
        {
            // Whatever happened, a half-written download is not left lying beside the program. On
            // the successful path the file has already been moved and there is nothing to remove.
            TryDelete(fresh);
        }
    }

    public void RemoveLeftovers() => AppFileSwap.RemoveLeftovers(_location.InstalledDirectory);

    /// <remarks>
    /// The caller ends this process afterwards. Started without a shell so the new program inherits
    /// nothing from the old one's console, and with the install folder as its working directory so
    /// it behaves exactly as it would from its shortcut.
    /// </remarks>
    public bool Restart()
    {
        try
        {
            var started = Process.Start(new ProcessStartInfo(_location.InstalledExecutablePath)
            {
                WorkingDirectory = _location.InstalledDirectory,
                UseShellExecute = false,
            });

            return started is not null;
        }
        catch (Exception)
        {
            // The file is in place and will run the next time it is opened by hand. Reported so the
            // screen can say to reopen it rather than sitting on a button that did nothing.
            return false;
        }
    }

    private async Task DownloadAsync(
        string url,
        string destination,
        long expectedSize,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? expectedSize;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var target = File.Create(destination);

        var buffer = new byte[81920];
        long written = 0;
        int read;

        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            written += read;

            if (total > 0)
            {
                progress?.Report(Math.Clamp((double)written / total, 0, 1));
            }
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
            // Cleared by RemoveLeftovers at the next startup.
        }
    }
}
```

- [ ] **Step 4: Убедиться, что тесты проходят**

```bash
dotnet test tests/Winora.System.Tests/Winora.System.Tests.csproj --nologo
```

Ожидаемо: все зелёные, включая 409 прежних.

- [ ] **Step 5: Коммит**

```bash
git add src/Winora.System/Updates/AppUpdater.cs tests/Winora.System.Tests/Updates/AppUpdaterTests.cs
git commit -m "feat(updates): download, judge and install a release"
```

---

### Task 6: Установка при первом запуске и ярлык

**Files:**
- Create: `src/Winora.System/Updates/StartMenuShortcut.cs`
- Create: `src/Winora.System/Updates/AppInstaller.cs`
- Test: `tests/Winora.System.Tests/Updates/AppInstallerTests.cs`

**Interfaces:**
- Consumes: `IAppInstallLocation`, `AppUpdater.Restart` (не используется — установщик запускает копию сам).
- Produces:
  - `interface IShortcutWriter { bool Write(string shortcutPath, string targetPath, string description); }`
  - `sealed class StartMenuShortcut : IShortcutWriter`
  - `enum InstallOutcome { Installed, AlreadyInstalled, CopyFailed }`
  - `interface IAppInstaller { bool NeedsInstalling { get; } string DestinationPath { get; } InstallOutcome Install(); bool StartInstalledCopy(); }`
  - `sealed class AppInstaller : IAppInstaller`

- [ ] **Step 1: Написать падающий тест**

Создать `tests/Winora.System.Tests/Updates/AppInstallerTests.cs`:

```csharp
using Winora.System.Updates;
using Xunit;

namespace Winora.System.Tests.Updates;

/// <summary>
/// Moving a downloaded copy into the place it belongs.
/// </summary>
/// <remarks>
/// The shortcut is written through an interface so this can be checked without leaving anything in
/// the real Start menu. What COM does with the file it is handed is COM's business; what matters
/// here is that a shortcut is asked for, that it points at the copy rather than at the download, and
/// that failing to write one does not fail the installation — a program in the right place without a
/// shortcut is still usable, and refusing to install over a missing menu entry would not be.
/// </remarks>
public sealed class AppInstallerTests : IDisposable
{
    private readonly string _folder;
    private readonly string _downloaded;
    private readonly string _programs;

    public AppInstallerTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "winora-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_folder, "Downloads"));
        _programs = Path.Combine(_folder, "Programs");
        _downloaded = Path.Combine(_folder, "Downloads", "Winora.exe");
        File.WriteAllText(_downloaded, "the downloaded program");
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

    private sealed class RecordingShortcuts : IShortcutWriter
    {
        public string? ShortcutPath { get; private set; }

        public string? TargetPath { get; private set; }

        public bool Succeed { get; init; } = true;

        public bool Write(string shortcutPath, string targetPath, string description)
        {
            ShortcutPath = shortcutPath;
            TargetPath = targetPath;
            return Succeed;
        }
    }

    private AppInstaller Installer(RecordingShortcuts shortcuts, string? current = null) =>
        new(
            new AppInstallLocation(current ?? _downloaded, _programs),
            shortcuts,
            Path.Combine(_folder, "StartMenu"));

    [Fact]
    public void A_downloaded_copy_needs_installing()
    {
        Assert.True(Installer(new RecordingShortcuts()).NeedsInstalling);
    }

    [Fact]
    public void The_installed_copy_does_not_need_installing_again()
    {
        var installed = Path.Combine(_programs, "Winora", "Winora.exe");

        Assert.False(Installer(new RecordingShortcuts(), installed).NeedsInstalling);
    }

    [Fact]
    public void Installing_puts_the_program_where_it_belongs()
    {
        var installer = Installer(new RecordingShortcuts());

        Assert.Equal(InstallOutcome.Installed, installer.Install());

        var landed = Path.Combine(_programs, "Winora", "Winora.exe");
        Assert.True(File.Exists(landed));
        Assert.Equal("the downloaded program", File.ReadAllText(landed));
    }

    /// <summary>The download stays. It is the person's file, and deleting it was not asked for.</summary>
    [Fact]
    public void The_downloaded_file_is_left_alone()
    {
        Installer(new RecordingShortcuts()).Install();

        Assert.True(File.Exists(_downloaded));
    }

    [Fact]
    public void A_shortcut_is_asked_for_and_points_at_the_installed_copy()
    {
        var shortcuts = new RecordingShortcuts();

        Installer(shortcuts).Install();

        Assert.Equal(Path.Combine(_programs, "Winora", "Winora.exe"), shortcuts.TargetPath);
        Assert.Equal(Path.Combine(_folder, "StartMenu", "Winora.lnk"), shortcuts.ShortcutPath);
    }

    /// <summary>
    /// A program in the right place with no menu entry is still a working program. Refusing to
    /// install over a shortcut that would not write would be trading something for nothing.
    /// </summary>
    [Fact]
    public void A_shortcut_that_will_not_write_does_not_fail_the_installation()
    {
        var installer = Installer(new RecordingShortcuts { Succeed = false });

        Assert.Equal(InstallOutcome.Installed, installer.Install());
        Assert.True(File.Exists(Path.Combine(_programs, "Winora", "Winora.exe")));
    }

    /// <summary>Installing over an existing copy replaces it rather than refusing.</summary>
    [Fact]
    public void An_existing_copy_is_overwritten()
    {
        var landed = Path.Combine(_programs, "Winora", "Winora.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(landed)!);
        File.WriteAllText(landed, "an older copy");

        Installer(new RecordingShortcuts()).Install();

        Assert.Equal("the downloaded program", File.ReadAllText(landed));
    }

    [Fact]
    public void The_destination_is_reported_before_anything_happens()
    {
        Assert.Equal(
            Path.Combine(_programs, "Winora", "Winora.exe"),
            Installer(new RecordingShortcuts()).DestinationPath);
    }
}
```

- [ ] **Step 2: Убедиться, что тест падает**

```bash
dotnet test tests/Winora.System.Tests/Winora.System.Tests.csproj --nologo --filter "FullyQualifiedName~AppInstallerTests"
```

Ожидаемо: `CS0246` — `AppInstaller`, `IShortcutWriter`, `InstallOutcome` не найдены.

- [ ] **Step 3: Написать ярлык**

Создать `src/Winora.System/Updates/StartMenuShortcut.cs`:

```csharp
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Winora.System.Updates;

/// <summary>Writes a Windows shortcut file.</summary>
public interface IShortcutWriter
{
    /// <summary>Writes <paramref name="shortcutPath" /> pointing at <paramref name="targetPath" />.</summary>
    /// <returns>True when the shortcut was written.</returns>
    bool Write(string shortcutPath, string targetPath, string description);
}

/// <summary>
/// Writes a <c>.lnk</c> through the shell's own interface.
/// </summary>
/// <remarks>
/// <para>
/// A shortcut is a structured binary file and there is no supported way to produce one except by
/// asking the shell. This is the standard interop for it, unchanged since it was documented.
/// </para>
/// <para>
/// Behind an interface because everything above it must be testable without leaving files in a real
/// Start menu, and because a failure here has to be survivable: a program in the right place with no
/// menu entry still works, and the installer treats a refusal as a shrug rather than an error.
/// </para>
/// Microsoft Learn: https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-ishelllinkw
/// </remarks>
public sealed class StartMenuShortcut : IShortcutWriter
{
    public bool Write(string shortcutPath, string targetPath, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shortcutPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        object? instance = null;

        try
        {
            var directory = Path.GetDirectoryName(shortcutPath);
            if (directory is { Length: > 0 })
            {
                Directory.CreateDirectory(directory);
            }

            // Activated by CLSID rather than through a declared coclass. A [ComImport] class cannot
            // be sealed, and an unsealed private type trips CA1852 — which, with warnings as errors,
            // means the canonical declaration will not build here. This asks the runtime for the
            // same object without declaring a type at all.
            var type = Type.GetTypeFromCLSID(ShellLinkClsid);
            instance = type is null ? null : Activator.CreateInstance(type);

            if (instance is not IShellLinkW link)
            {
                return false;
            }

            link.SetPath(targetPath);
            link.SetWorkingDirectory(Path.GetDirectoryName(targetPath) ?? string.Empty);
            link.SetDescription(description ?? string.Empty);

            ((IPersistFile)instance).Save(shortcutPath, fRemember: true);
            return true;
        }
        catch (Exception)
        {
            // COM unavailable, the folder not writable, or the shell refusing. The caller carries on
            // without a menu entry rather than failing an installation over one.
            return false;
        }
        finally
        {
            if (instance is not null && Marshal.IsComObject(instance))
            {
                Marshal.ReleaseComObject(instance);
            }
        }
    }

    /// <summary>The shell's link object.</summary>
    private static readonly Guid ShellLinkClsid = new("00021401-0000-0000-C000-000000000046");

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath(
            [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file,
            int maxPath,
            nint find,
            int flags);

        void GetIDList(out nint list);

        void SetIDList(nint list);

        void GetDescription(
            [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name,
            int maxName);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);

        void GetWorkingDirectory(
            [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder directory,
            int maxPath);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);

        void GetArguments(
            [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder arguments,
            int maxArguments);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);

        void GetHotkey(out short hotkey);

        void SetHotkey(short hotkey);

        void GetShowCmd(out int show);

        void SetShowCmd(int show);

        void GetIconLocation(
            [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder iconPath,
            int iconPathLength,
            out int iconIndex);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relative, int reserved);

        void Resolve(nint window, int flags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
```

- [ ] **Step 4: Написать установщик**

Создать `src/Winora.System/Updates/AppInstaller.cs`:

```csharp
using System.Diagnostics;

namespace Winora.System.Updates;

/// <summary>How an installation attempt ended.</summary>
public enum InstallOutcome
{
    /// <summary>The program is now in the place it belongs.</summary>
    Installed,

    /// <summary>It was already there and nothing needed doing.</summary>
    AlreadyInstalled,

    /// <summary>The copy could not be made. Nothing has changed.</summary>
    CopyFailed,
}

/// <summary>Puts a downloaded copy of Winora into the place an installed one belongs.</summary>
public interface IAppInstaller
{
    /// <summary>True when this copy is running from somewhere other than the installed place.</summary>
    bool NeedsInstalling { get; }

    /// <summary>Where the copy would go. Shown to the person before they agree.</summary>
    string DestinationPath { get; }

    InstallOutcome Install();

    /// <summary>Starts the installed copy. The caller ends this process afterwards.</summary>
    bool StartInstalledCopy();
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// Asked, never assumed. A program that copied itself somewhere on first launch without saying so
/// has done something the person did not ask for, and the exact destination goes on the screen
/// before they answer.
/// </para>
/// <para>
/// The downloaded file is left where it is. It belongs to whoever downloaded it, tidying it away was
/// not part of the bargain, and a program that deleted files out of somebody's Downloads folder
/// would be doing something worse than leaving one behind.
/// </para>
/// </remarks>
public sealed class AppInstaller : IAppInstaller
{
    /// <summary>What the shortcut is called in the Start menu.</summary>
    private const string ShortcutName = "Winora.lnk";

    private const string ShortcutDescription = "Winora";

    private readonly IAppInstallLocation _location;
    private readonly IShortcutWriter _shortcuts;
    private readonly string _startMenuDirectory;

    public AppInstaller(IAppInstallLocation location, IShortcutWriter shortcuts)
        : this(
            location,
            shortcuts,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs"))
    {
    }

    public AppInstaller(
        IAppInstallLocation location,
        IShortcutWriter shortcuts,
        string startMenuDirectory)
    {
        _location = location ?? throw new ArgumentNullException(nameof(location));
        _shortcuts = shortcuts ?? throw new ArgumentNullException(nameof(shortcuts));
        ArgumentException.ThrowIfNullOrWhiteSpace(startMenuDirectory);
        _startMenuDirectory = startMenuDirectory;
    }

    public bool NeedsInstalling => !_location.IsInstalled;

    public string DestinationPath => _location.InstalledExecutablePath;

    public InstallOutcome Install()
    {
        if (_location.IsInstalled)
        {
            return InstallOutcome.AlreadyInstalled;
        }

        try
        {
            Directory.CreateDirectory(_location.InstalledDirectory);
            File.Copy(_location.CurrentExecutablePath, DestinationPath, overwrite: true);
        }
        catch (Exception)
        {
            // Out of disk, or a folder policy forbids it. Nothing has moved; the program keeps
            // running from where it is and says so.
            return InstallOutcome.CopyFailed;
        }

        // Deliberately not part of the outcome. A program in the right place without a menu entry is
        // still a working program, and refusing the installation over a shortcut would trade
        // something for nothing.
        _shortcuts.Write(
            Path.Combine(_startMenuDirectory, ShortcutName),
            DestinationPath,
            ShortcutDescription);

        return InstallOutcome.Installed;
    }

    public bool StartInstalledCopy()
    {
        try
        {
            var started = Process.Start(new ProcessStartInfo(DestinationPath)
            {
                WorkingDirectory = _location.InstalledDirectory,
                UseShellExecute = false,
            });

            return started is not null;
        }
        catch (Exception)
        {
            // The copy is in place and opens from the Start menu; only this hand-off failed.
            return false;
        }
    }
}
```

- [ ] **Step 5: Убедиться, что тесты проходят**

```bash
dotnet test tests/Winora.System.Tests/Winora.System.Tests.csproj --nologo
```

Ожидаемо: все зелёные.

- [ ] **Step 6: Собрать проект**

```bash
dotnet build src/Winora.System/Winora.System.csproj -c Release -p:Platform=x64 --nologo -v q
```

Ожидаемо: молча. `TreatWarningsAsErrors=true`, так что любое предупреждение — ошибка, и отсутствие вывода здесь и есть результат.

COM-часть намеренно не покрыта тестами: проверять чужую реализацию `IShellLink` подделкой бессмысленно, а писать в настоящее меню Пуск из теста нельзя. Она проверяется один раз живьём на шаге 4 задачи 8, где ярлык ищется на диске.

- [ ] **Step 7: Коммит**

```bash
git add src/Winora.System/Updates/StartMenuShortcut.cs src/Winora.System/Updates/AppInstaller.cs tests/Winora.System.Tests/Updates/AppInstallerTests.cs
git commit -m "feat(updates): offer to install into the programs folder, with a shortcut"
```

---

### Task 7: Экран — полоска обновления и вопрос при первом запуске

**Files:**
- Create: `src/Winora.App/ViewModels/UpdateViewModel.cs`
- Modify: `src/Winora.App/Services/ServiceRegistration.cs` (рядом со строкой 127, блок обхода)
- Modify: `src/Winora.App/MainWindow.xaml` (строка 17–22, добавить третью строку сетки)
- Modify: `src/Winora.App/MainWindow.xaml.cs` (конструктор, после `_shell.Load();`)
- Modify: `src/Winora.App/Strings/ru-RU/Resources.resw`

**Interfaces:**
- Consumes: `IAppReleaseFeed`, `IAppUpdater`, `IAppInstaller`, `IAppInstallLocation` (задачи 2–6), `IAppEnvironment.Version`, `IDeploymentState.IsPackaged`, `ILocalizationService.Get`.
- Produces: `UpdateViewModel` со свойствами `bool IsBannerVisible`, `string Message`, `string ActionLabel`, `bool IsBusy`, `double Progress` и командами `CheckCommand`, `ActCommand`; методом `Task StartupAsync()`.

- [ ] **Step 1: Добавить строки на экран**

В `src/Winora.App/Strings/ru-RU/Resources.resw`, перед закрывающим `</root>`, вставить:

```xml
  <data name="Update_Checking" xml:space="preserve">
    <value>Проверяем обновления…</value>
  </data>
  <data name="Update_Available" xml:space="preserve">
    <value>Доступна версия {0}</value>
  </data>
  <data name="Update_UpToDate" xml:space="preserve">
    <value>У вас последняя версия</value>
  </data>
  <data name="Update_Action_Install" xml:space="preserve">
    <value>Обновить</value>
  </data>
  <data name="Update_Action_Open" xml:space="preserve">
    <value>Открыть страницу</value>
  </data>
  <data name="Update_Downloading" xml:space="preserve">
    <value>Скачиваем…</value>
  </data>
  <data name="Update_Restarting" xml:space="preserve">
    <value>Обновлено. Перезапускаем…</value>
  </data>
  <data name="Update_Failed_Download" xml:space="preserve">
    <value>Не удалось скачать. Программа не изменена.</value>
  </data>
  <data name="Update_Failed_Verification" xml:space="preserve">
    <value>Файл скачался повреждённым. Программа не изменена.</value>
  </data>
  <data name="Update_Failed_Swap" xml:space="preserve">
    <value>Не удалось обновить, всё осталось как было.</value>
  </data>
  <data name="Update_Failed_Restart" xml:space="preserve">
    <value>Обновлено. Закройте окно и откройте Winora заново.</value>
  </data>
  <data name="Update_NotInstalled" xml:space="preserve">
    <value>Скачайте новую версию — эта копия запущена не из папки программ.</value>
  </data>
  <data name="Install_Title" xml:space="preserve">
    <value>Установить Winora?</value>
  </data>
  <data name="Install_Body" xml:space="preserve">
    <value>Программа скопирует себя в {0} и добавит ярлык в меню Пуск. Права администратора не нужны.</value>
  </data>
  <data name="Install_Yes" xml:space="preserve">
    <value>Установить</value>
  </data>
  <data name="Install_No" xml:space="preserve">
    <value>Не сейчас</value>
  </data>
  <data name="Install_Failed" xml:space="preserve">
    <value>Не удалось скопировать. Программа продолжит работать отсюда.</value>
  </data>
```

- [ ] **Step 2: Написать `UpdateViewModel`**

Создать `src/Winora.App/ViewModels/UpdateViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Winora.App.Services;
using Winora.System.Updates;

namespace Winora.App.ViewModels;

/// <summary>
/// The one strip at the top of the window that says a new version exists.
/// </summary>
/// <remarks>
/// <para>
/// Silent when there is nothing to say. A check that failed and a check that found nothing look the
/// same from where the person sits, and inventing a difference would fill the top of the window with
/// notices about the health of somebody else's API.
/// </para>
/// <para>
/// Switched off entirely in the packaged build. An MSIX app lives under
/// <c>C:\Program Files\WindowsApps</c>, which is protected by the operating system and cannot be
/// written to; a strip promising an update that could never be installed is worse than no strip.
/// </para>
/// </remarks>
public sealed partial class UpdateViewModel : ObservableObject
{
    private readonly IAppReleaseFeed _feed;
    private readonly IAppUpdater _updater;
    private readonly IAppInstallLocation _location;
    private readonly IAppEnvironment _environment;
    private readonly IDeploymentState _deployment;
    private readonly ILocalizationService _text;

    private AppRelease? _found;

    public UpdateViewModel(
        IAppReleaseFeed feed,
        IAppUpdater updater,
        IAppInstallLocation location,
        IAppEnvironment environment,
        IDeploymentState deployment,
        ILocalizationService text)
    {
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _updater = updater ?? throw new ArgumentNullException(nameof(updater));
        _location = location ?? throw new ArgumentNullException(nameof(location));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _deployment = deployment ?? throw new ArgumentNullException(nameof(deployment));
        _text = text ?? throw new ArgumentNullException(nameof(text));
    }

    /// <remarks>
    /// Partial properties, not fields: MVVMTK0045 requires this form in WinUI 3 so the CsWinRT
    /// generators can emit the WinRT marshalling code.
    /// </remarks>
    [ObservableProperty]
    public partial bool IsBannerVisible { get; set; }

    [ObservableProperty]
    public partial string Message { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ActionLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsActionVisible { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    /// <summary>Where to send somebody whose copy cannot update itself.</summary>
    public string ReleasePageUrl =>
        _found is null
            ? "https://github.com/geniyhackerdotaswag-bit/Winora/releases/latest"
            : $"https://github.com/geniyhackerdotaswag-bit/Winora/releases/tag/{_found.Tag}";

    /// <summary>Raised when the process should end because a newer one has been started.</summary>
    public event EventHandler? RestartRequested;

    /// <summary>Raised when the person should be sent to the release page in a browser.</summary>
    public event EventHandler? OpenPageRequested;

    /// <summary>
    /// The one check made without being asked, at startup.
    /// </summary>
    /// <remarks>
    /// Also the moment the debris of a previous update is cleared: the displaced file is no longer
    /// running by now, so this is the first point at which it can actually be deleted.
    /// </remarks>
    public async Task StartupAsync()
    {
        if (_deployment.IsPackaged)
        {
            return;
        }

        _updater.RemoveLeftovers();
        await CheckAsync(announceNothing: false).ConfigureAwait(true);
    }

    [RelayCommand]
    private Task Check() => CheckAsync(announceNothing: true);

    private async Task CheckAsync(bool announceNothing)
    {
        if (_deployment.IsPackaged || IsBusy)
        {
            return;
        }

        var current = AppVersion.Parse(_environment.Version);
        if (current is null)
        {
            return;
        }

        var check = new AppUpdateCheck(current, await _feed.LatestAsync().ConfigureAwait(true));

        if (!check.UpdateAvailable)
        {
            _found = null;
            IsActionVisible = false;

            // Only when they asked. An unprompted "you are up to date" is a notice about nothing.
            Message = announceNothing ? _text.Get("Update_UpToDate") : string.Empty;
            IsBannerVisible = announceNothing;
            return;
        }

        _found = check.Latest;
        Message = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            _text.Get("Update_Available"),
            check.Latest!.Version);

        // A copy running from wherever it was downloaded cannot replace itself: that file was never
        // offered up. It is sent to the page instead.
        ActionLabel = _location.IsInstalled
            ? _text.Get("Update_Action_Install")
            : _text.Get("Update_Action_Open");

        IsActionVisible = true;
        IsBannerVisible = true;
    }

    [RelayCommand]
    private async Task Act()
    {
        if (_found is null || IsBusy)
        {
            return;
        }

        if (!_location.IsInstalled)
        {
            Message = _text.Get("Update_NotInstalled");
            OpenPageRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        IsBusy = true;
        IsActionVisible = false;
        Progress = 0;
        Message = _text.Get("Update_Downloading");

        try
        {
            var outcome = await _updater
                .UpdateAsync(_found, new System.Progress<double>(value => Progress = value))
                .ConfigureAwait(true);

            switch (outcome)
            {
                case UpdateOutcome.Installed:
                    Message = _text.Get("Update_Restarting");
                    if (_updater.Restart())
                    {
                        RestartRequested?.Invoke(this, EventArgs.Empty);
                    }
                    else
                    {
                        Message = _text.Get("Update_Failed_Restart");
                    }

                    break;

                case UpdateOutcome.DownloadFailed:
                    Fail("Update_Failed_Download");
                    break;

                case UpdateOutcome.Verification:
                    Fail("Update_Failed_Verification");
                    break;

                case UpdateOutcome.SwapFailed:
                case UpdateOutcome.NotInstalled:
                default:
                    Fail("Update_Failed_Swap");
                    break;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Fail(string resourceKey)
    {
        Message = _text.Get(resourceKey);

        // The button comes back saying "open the page": whatever went wrong here, the release is
        // still downloadable by hand, and a dead end would be the wrong place to leave somebody.
        ActionLabel = _text.Get("Update_Action_Open");
        IsActionVisible = true;
    }
}
```

- [ ] **Step 3: Зарегистрировать в контейнере**

В `src/Winora.App/Services/ServiceRegistration.cs`, сразу после строки `services.AddTransient<BypassViewModel>();`, вставить:

```csharp
        // Winora's own release feed and updater. Singletons for the same reason as the bypass
        // installer above: the release the person was shown must be the release that gets
        // installed, so the object holding it has to outlive the screen that showed it.
        services.AddSingleton<IAppInstallLocation, AppInstallLocation>();
        services.AddSingleton<IShortcutWriter, StartMenuShortcut>();
        services.AddSingleton<IAppReleaseFeed, AppReleaseFeed>();
        services.AddSingleton<IAppUpdater>(provider =>
            new AppUpdater(provider.GetRequiredService<IAppInstallLocation>()));
        services.AddSingleton<IAppInstaller>(provider =>
            new AppInstaller(
                provider.GetRequiredService<IAppInstallLocation>(),
                provider.GetRequiredService<IShortcutWriter>()));
        services.AddSingleton<UpdateViewModel>();
```

В начало того же файла, к остальным `using`, добавить:

```csharp
using Winora.System.Updates;
```

- [ ] **Step 4: Добавить полоску в окно**

В `src/Winora.App/MainWindow.xaml` заменить блок описания строк:

```xml
        <Grid.RowDefinitions>
            <RowDefinition Height="48" />
            <RowDefinition Height="*" />
        </Grid.RowDefinitions>
```

на:

```xml
        <Grid.RowDefinitions>
            <RowDefinition Height="48" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
        </Grid.RowDefinitions>
```

Затем найти элемент `<NavigationView x:Name="Navigation"` и заменить в нём `Grid.Row="1"` на `Grid.Row="2"`. Если атрибута `Grid.Row` там нет — добавить `Grid.Row="2"`.

Перед `<NavigationView` вставить полоску:

```xml
        <!--
          The one place the app tells you about itself. Collapsed unless there is something to say:
          a strip that is always present, saying everything is fine, is a strip people stop reading.
        -->
        <InfoBar x:Name="UpdateBar"
                 Grid.Row="1"
                 Margin="16,0,16,12"
                 IsClosable="True"
                 IsOpen="False"
                 Severity="Informational">
            <InfoBar.ActionButton>
                <Button x:Name="UpdateAction" />
            </InfoBar.ActionButton>
            <InfoBar.Content>
                <ProgressBar x:Name="UpdateProgress"
                             Margin="0,8,0,8"
                             Maximum="1"
                             Visibility="Collapsed" />
            </InfoBar.Content>
        </InfoBar>
```

- [ ] **Step 5: Связать полоску с моделью**

В `src/Winora.App/MainWindow.xaml.cs`:

к полям класса добавить

```csharp
    private readonly UpdateViewModel _update;
```

в конструкторе, рядом с остальными `GetRequiredService`, добавить

```csharp
        _update = App.Services.GetRequiredService<UpdateViewModel>();
```

и в конце конструктора, после `_shell.Load();` и построения панели, добавить

```csharp
        BindUpdateBar();
```

а в конец класса — сам метод:

```csharp
    /// <summary>
    /// Wires the update strip by hand rather than by binding.
    /// </summary>
    /// <remarks>
    /// Four properties and two events, against a control that is created once and never recycled.
    /// A set of bindings and a converter for each visibility would be more machinery than the thing
    /// it drives, and the strip is the one piece of UI whose behaviour must be obvious when
    /// something goes wrong with it.
    /// </remarks>
    private void BindUpdateBar()
    {
        UpdateAction.Command = _update.ActCommand;

        _update.PropertyChanged += (_, _) =>
        {
            UpdateBar.IsOpen = _update.IsBannerVisible;
            UpdateBar.Message = _update.Message;
            UpdateAction.Content = _update.ActionLabel;
            UpdateAction.Visibility = _update.IsActionVisible
                ? Visibility.Visible
                : Visibility.Collapsed;
            UpdateProgress.Visibility = _update.IsBusy ? Visibility.Visible : Visibility.Collapsed;
            UpdateProgress.Value = _update.Progress;
        };

        _update.RestartRequested += (_, _) => Close();
        _update.OpenPageRequested += (_, _) =>
            _ = Windows.System.Launcher.LaunchUriAsync(new Uri(_update.ReleasePageUrl));

        // Deliberately not awaited: the window must finish opening whatever the network is doing.
        _ = _update.StartupAsync();
    }
```

- [ ] **Step 6: Спросить об установке при первом запуске**

В `src/Winora.App/MainWindow.xaml.cs`, в конец `BindUpdateBar` **не** добавлять — это отдельная забота. Добавить отдельный метод и вызвать его из конструктора **перед** `BindUpdateBar();`:

```csharp
    /// <summary>
    /// Offers to move a downloaded copy into the programs folder.
    /// </summary>
    /// <remarks>
    /// Asked every launch until it is answered yes, and never remembered as a no. Somebody who
    /// downloaded the program to look at it opens it the second time on purpose, and that is the
    /// better moment to ask than the first. If this turns out to be a nuisance the answer is a
    /// "don't ask again", but not before it has actually been a nuisance.
    /// </remarks>
    private async void OfferInstall()
    {
        var installer = App.Services.GetRequiredService<IAppInstaller>();

        if (App.Services.GetRequiredService<IDeploymentState>().IsPackaged ||
            !installer.NeedsInstalling)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = _text.Get("Install_Title"),
            Content = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                _text.Get("Install_Body"),
                installer.DestinationPath),
            PrimaryButtonText = _text.Get("Install_Yes"),
            CloseButtonText = _text.Get("Install_No"),
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (installer.Install() == InstallOutcome.Installed && installer.StartInstalledCopy())
        {
            Close();
            return;
        }

        UpdateBar.Message = _text.Get("Install_Failed");
        UpdateBar.IsOpen = true;
    }
```

Вызов в конструкторе, после `BindUpdateBar();`:

```csharp
        // After the window exists: a dialog needs a XamlRoot, and there is not one before this.
        DispatcherQueue.TryEnqueue(OfferInstall);
```

К `using` файла добавить `using Winora.System.Updates;`.

- [ ] **Step 7: Собрать и убедиться, что предупреждений нет**

```bash
dotnet build src/Winora.App/Winora.App.csproj -c Release -p:Platform=x64 --nologo -v q
```

Ожидаемо: сборка проходит. `TreatWarningsAsErrors=true`, поэтому любое предупреждение — это ошибка, и молчание здесь и есть результат.

- [ ] **Step 8: Прогнать все тесты**

```bash
dotnet test --nologo
```

Ожидаемо: ни одного упавшего во всех проектах.

- [ ] **Step 9: Коммит**

```bash
git add src/Winora.App/ViewModels/UpdateViewModel.cs src/Winora.App/Services/ServiceRegistration.cs src/Winora.App/MainWindow.xaml src/Winora.App/MainWindow.xaml.cs src/Winora.App/Strings/ru-RU/Resources.resw
git commit -m "feat(app): say when a new version exists, and offer to install this one"
```

---

### Task 8: Выпуск версии и живая проверка

**Files:**
- Create: `.github/workflows/release.yml`
- Create: `README.md` (если его нет — проверить `ls README.md`; если есть, дописать раздел)
- Modify: `docs/superpowers/plans/2026-08-08-winora-backlog.md`

**Interfaces:**
- Consumes: всё предыдущее.
- Produces: релиз с файлами `Winora.exe` и `Winora.exe.sha256`.

- [ ] **Step 1: Написать workflow**

Создать `.github/workflows/release.yml`:

```yaml
# Builds the portable Winora and publishes it, on a tag and only on a tag.
#
# The tag is the only source of the version: it reaches the build as -p:Version and ends up inside
# the executable, where the update check reads it back. Nothing here uses a token belonging to a
# person — GITHUB_TOKEN is issued for the length of this run and expires with it.
name: release

on:
  push:
    tags:
      - "v*"

permissions:
  contents: write

jobs:
  build:
    runs-on: windows-latest

    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "10.0.x"

      # The tag is written v0.4.0; the version inside the build is not.
      - name: Read the version out of the tag
        id: version
        shell: pwsh
        run: |
          $version = "${{ github.ref_name }}".TrimStart('v')
          if ($version -notmatch '^\d+\.\d+\.\d+$') {
            throw "Tag '${{ github.ref_name }}' is not vMAJOR.MINOR.PATCH"
          }
          "value=$version" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8

      - name: Test
        run: dotnet test --nologo

      - name: Publish
        run: >
          dotnet publish src/Winora.App/Winora.App.csproj
          -c Release
          -p:WinoraPortable=true
          -p:Platform=x64
          -p:Version=${{ steps.version.outputs.value }}
          -o publish

      # Published as Winora.exe: that is the name an installed copy has, and AppInstallLocation
      # checks the name as well as the folder.
      - name: Name it as the release
        shell: pwsh
        run: |
          Move-Item publish/Winora.App.exe publish/Winora.exe
          $hash = (Get-FileHash publish/Winora.exe -Algorithm SHA256).Hash
          "$hash  Winora.exe" | Out-File publish/Winora.exe.sha256 -Encoding ascii -NoNewline

      - uses: softprops/action-gh-release@v2
        with:
          files: |
            publish/Winora.exe
            publish/Winora.exe.sha256
          generate_release_notes: true
```

- [ ] **Step 2: Проверить, что workflow разбирается**

```bash
python -c "import yaml,io; yaml.safe_load(io.open('.github/workflows/release.yml',encoding='utf-8')); print('ok')"
```

Ожидаемо: `ok`.

- [ ] **Step 3: Написать README**

Создать или дописать `README.md`:

```markdown
# Winora

Приложение для Windows 11: оформление, курсоры, звуки и обход блокировок.

## Установка

1. Скачайте `Winora.exe` со [страницы релизов](https://github.com/geniyhackerdotaswag-bit/Winora/releases/latest).
2. Запустите его.
3. Windows покажет «Windows защитила ваш компьютер» — нажмите **Подробнее**, затем **Выполнить в любом случае**.
4. Приложение предложит установиться и добавит ярлык в меню Пуск.

Предупреждение на третьем шаге появляется потому, что файл не подписан
сертификатом: он стоит денег. Это не значит, что с файлом что-то не так, но
означает, что верить нужно тому, откуда вы его взяли, — то есть этой странице.

Первый запуск заметно дольше остальных: приложение самодостаточно и распаковывает
около 220 МБ во временную папку. Ни .NET, ни Windows App SDK ставить не нужно.

## Обновления

Приложение само проверяет новые версии при запуске и показывает полоску наверху.
Одно нажатие — скачает, проверит и перезапустится. Ничего не обновляется без
вашего согласия.

## Сборка из исходников

```
dotnet publish src/Winora.App/Winora.App.csproj -c Release -p:WinoraPortable=true -p:Platform=x64 -o publish
```
```

- [ ] **Step 4: Собрать переносимую сборку и проверить живьём**

```bash
dotnet publish src/Winora.App/Winora.App.csproj -c Release -p:WinoraPortable=true -p:Platform=x64 -p:Version=0.4.0 -o publish/portable --nologo -v q
```

Затем переименовать и запустить из папки, изображающей «Загрузки»:

```bash
mkdir -p /tmp/winora-downloads && cp publish/portable/Winora.App.exe /tmp/winora-downloads/Winora.exe && ls -la /tmp/winora-downloads/
```

Запустить `Winora.exe` из этой папки и проверить глазами три вещи:

1. Появилось окно с вопросом «Установить Winora?» и **точным** путём в тексте.
2. После согласия файл лежит в `%LOCALAPPDATA%\Programs\Winora\Winora.exe`, а в меню Пуск есть «Winora».
3. Новое окно открылось из установленной копии, старое закрылось.

Проверить путь и ярлык:

```bash
powershell -NoProfile -Command "Test-Path \"$env:LOCALAPPDATA\Programs\Winora\Winora.exe\"; Test-Path \"$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Winora.lnk\""
```

Ожидаемо: `True` дважды. Это единственная проверка COM-части — тестами она не покрыта.

- [ ] **Step 5: Записать в план оставшееся**

В `docs/superpowers/plans/2026-08-08-winora-backlog.md`, в конец файла:

```markdown

## Раздача и обновление: что осталось после 2026-08-23

- **Репозиторий всё ещё закрытый.** Пока он такой, лента релизов отвечает 404, и
  полоска обновления не покажется никогда. Открыть перед первым тегом.
- **Первый тег не поставлен.** `git tag v0.4.0 && git push --tags` — и только
  после этого можно проверить обновление целиком, а не по частям.
- **Обновление проверено по частям, но не целиком.** Отдельно проверены разбор
  версии, отказ от битой загрузки, порядок подмены и установка с ярлыком. Путь
  «увидел полоску → нажал → перезапустился на новой версии» требует двух
  настоящих релизов подряд и проверяется, когда они появятся.
- **`src/Winora.App/AppPackages` занимает 5,5 ГБ** старыми MSIX-сборками. Из
  сборки исключены, с диска не удалены — решение владельца.
- **MSIX-путь больше не используется**, но проект его собирает. Удалять пока
  нечего: оснастка MSIX нужна самой переносимой сборке, она генерирует
  `resources.pri`.
```

- [ ] **Step 6: Коммит**

```bash
git add .github/workflows/release.yml README.md docs/superpowers/plans/2026-08-08-winora-backlog.md
git commit -m "feat(release): publish a portable build from a tag, and say how to run it"
```

---

## Порядок и зависимости

```
1 (версия) → 2 (лента)
1 → 3 (место) → 4 (проверка и подмена) → 5 (обновление)
3 → 6 (установка)
2, 5, 6 → 7 (экран) → 8 (выпуск)
```

Задачи 2 и 3 независимы друг от друга и могут идти в любом порядке после первой.
