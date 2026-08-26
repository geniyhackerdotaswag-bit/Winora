# Winora: вид законченного продукта — план работ

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Убрать четыре места, где Winora сама себя выдаёт как незаконченную: пустая Главная, тупики в панели, отсутствие версии и наполовину пустое окно регистрации.

**Architecture:** Ничего нового не заводится. Плитки Главной берут название и значок из `RouteRegistry` — того же, из которого строится левая панель. Закрытые разделы уходят из панели сменой `RoutePlacement` на уже существующий `RouteOnly`. Версия читается из уже работающего `IAppEnvironment.Version`. Кнопка сообщества переезжает с Главной в подвал панели вместе со своим адресом и подсказкой.

**Tech Stack:** WinUI 3 / Windows App SDK 2.0.4, .NET 10, CommunityToolkit.Mvvm, xUnit.

**Спецификация:** `docs/superpowers/specs/2026-08-26-winora-finished-look-design.md`

## Global Constraints

- Модели представления не обращаются к `Winora.System` и `Winora.Infrastructure` — этого требует `ViewModelBoundaryTests`. `Winora.App.Navigation` разрешён: `ShellViewModel` уже берёт `RouteRegistry`.
- `[ObservableProperty]` объявляется только как `public partial string X { get; set; }` — MVVMTK0045 требует именно эту форму в WinUI 3.
- Каждый ключ ресурса, запрошенный из `.cs` или `.xaml`, обязан существовать в `src/Winora.App/Strings/ru-RU/Resources.resw` — это проверяет `ResourceKeyTests`, сканируя исходный текст обоих видов файлов. Пустое значение ключа он тоже отвергает.
- Каждый значок, который просит маршрут, обязан существовать в `FluentIconCatalog` — это проверяет `IconCatalogTests`. Ключ `"shield"`, которого в каталоге не было, однажды уехал в релиз и оставил в панели пустое место без единой ошибки.
- Язык интерфейса — только русский, папка ресурсов одна: `ru-RU`.
- Сборка: `dotnet build src/Winora.App/Winora.App.csproj -c Release -p:Platform=x64`. Тесты: `dotnet test -c Release`.
- Приложение работает с правами администратора: UI-автоматизация к нему не проходит. Проверка на экране — только снимком (`CopyFromScreen` после `SetWindowPos` в topmost; `PrintWindow` для этого окна возвращает чёрный кадр и однажды привёл к ложному выводу).

## Чего этот план сознательно не делает

**Ничего не добавляет на Главную сверх плиток.** Комментарий к `DashboardViewModel` ведёт счёт: экран очищали дважды, и «список последних изменений с отменой у каждой строки» был **сделан по просьбе владельца и убран на следующий день** — прочитался как мусор. Там же записана проверка, которую этот экран раз за разом заваливает: то, что кладут на Главную, должно быть тем, **за чем человек пришёл**, а не тем, **что приложение захотело сказать**.

Плитки проходят эту проверку: это навигация к тому, ради чего программу открыли. Счётчики, сводки и ленты — не проходят, и три попытки уже сняты. В комментарий к `QuickActions` это переносится дословно, чтобы четвёртой не было.

---

## Файлы

| Файл | Ответственность | Задача |
|---|---|---|
| `src/Winora.App/Controls/FluentIconCatalog.cs` | ключ `globe` | 1 |
| `src/Winora.App/Navigation/RouteRegistry.cs` | обход берёт глобус; звуки и производительность уходят из панели | 1, 2 |
| `src/Winora.App/ViewModels/DashboardViewModel.cs` | список плиток; минус адрес и подсказка сообщества | 3, 5 |
| `src/Winora.App/ViewModels/QuickAction.cs` | что знает одна плитка | 3 |
| `src/Winora.App/Views/DashboardPage.xaml(.cs)` | разметка плиток; минус кнопка в углу | 4 |
| `src/Winora.App/ViewModels/ShellViewModel.cs` | адрес сообщества, подсказка, строка версии | 5 |
| `src/Winora.App/MainWindow.xaml(.cs)` | подвал панели | 5 |
| `src/Winora.App/Views/RegistrationWindow.xaml(.cs)` | фокус и центрирование шага | 6 |
| `src/Winora.App/Resources/Styles/Controls.xaml` | стиль плитки | 4 |
| `src/Winora.App/Strings/ru-RU/Resources.resw` | новые строки | 1, 3, 5 |
| `tests/Winora.App.Tests/ViewModels/DashboardViewModelTests.cs` | новый файл: плитки | 3 |
| `tests/Winora.App.Tests/Navigation/RouteRegistryTests.cs` | панель без тупиков | 2 |
| `tests/Winora.App.Tests/ViewModels/ShellViewModelTests.cs` | новый файл: версия | 5 |

---

## Task 1: Глобус для обхода, Discord — сообществу

**Files:**
- Modify: `src/Winora.App/Controls/FluentIconCatalog.cs`
- Modify: `src/Winora.App/Navigation/RouteRegistry.cs:37`
- Test: `tests/Winora.App.Tests/Navigation/IconCatalogTests.cs` (существующий, ничего не дописываем)

**Interfaces:**
- Consumes: ничего.
- Produces: ключ каталога `"globe"`; маршрут `RouteKeys.Bypass` больше не носит `"discord"`.

**Зачем.** Как только кнопка сообщества переедет в подвал панели (задача 5), логотип Discord начнёт означать в одной панели две разные вещи: ссылку на сервер и функцию, которая разблокирует в том числе YouTube. Discord остаётся у сообщества, где он буквален.

- [ ] **Шаг 1: Убедиться, что существующий тест ловит выдуманный ключ**

Временно поставить в `RouteRegistry.cs:37` значок `"globe"`, которого ещё нет в каталоге:

```csharp
new(RouteKeys.Bypass, "Nav_Bypass", RoutePlacement.Pane, GroupSystem, "globe"),
```

Запустить:

```bash
dotnet test tests/Winora.App.Tests/Winora.App.Tests.csproj -c Release --filter "FullyQualifiedName~IconCatalogTests"
```

Ожидается: FAIL — `Route 'bypass' asks for icon 'globe', which the catalog does not have.`

Это и есть красный тест задачи: ключ не выдуман, а именно проверяем.

- [ ] **Шаг 2: Добавить глобус в каталог**

В `FluentIconCatalog.Glyphs`, следом за `["startup"]`:

```csharp
        ["globe"] = "",
```

`E774` — Globe из Segoe Fluent Icons.

- [ ] **Шаг 3: Тест зеленеет**

```bash
dotnet test tests/Winora.App.Tests/Winora.App.Tests.csproj -c Release --filter "FullyQualifiedName~IconCatalogTests"
```

Ожидается: PASS.

- [ ] **Шаг 4: Коммит**

```bash
git add src/Winora.App/Controls/FluentIconCatalog.cs src/Winora.App/Navigation/RouteRegistry.cs
git commit -m "Give the bypass route a globe and leave Discord to the community"
```

---

## Task 2: Звуки и Производительность уходят из панели

**Files:**
- Modify: `src/Winora.App/Navigation/RouteRegistry.cs:33,36`
- Test: `tests/Winora.App.Tests/Navigation/RouteRegistryTests.cs`

**Interfaces:**
- Consumes: `RoutePlacement.RouteOnly` — уже существует и уже так используется маршрутом `Appearance`.
- Produces: `RouteKeys.Sounds` и `RouteKeys.Performance` находятся по ключу, но не появляются ни в одной коллекции панели.

- [ ] **Шаг 1: Написать падающий тест**

В конец `RouteRegistryTests.cs`, внутрь класса:

```csharp
    /// <summary>
    /// Оба раздела закрыты на технические работы. Пункт панели, который открывается фразой
    /// "раздел закрыт", — это тупик, и он громче всего говорит "программа не доделана".
    /// Страницы остаются достижимыми по ключу: закрыты они временно.
    /// </summary>
    [Theory]
    [InlineData(RouteKeys.Sounds)]
    [InlineData(RouteKeys.Performance)]
    public void A_section_closed_for_maintenance_is_not_offered_in_the_pane(string key)
    {
        var registry = RouteRegistry.Create();

        Assert.True(registry.TryFind(key, out var route));
        Assert.Equal(RoutePlacement.RouteOnly, route!.Placement);
    }
```

- [ ] **Шаг 2: Запустить, убедиться, что падает**

```bash
dotnet test tests/Winora.App.Tests/Winora.App.Tests.csproj -c Release --filter "FullyQualifiedName~RouteRegistryTests"
```

Ожидается: FAIL — ожидалось `RouteOnly`, получено `Pane`, дважды.

- [ ] **Шаг 3: Убрать оба маршрута из панели**

В `RouteRegistry.Create()` заменить обе строки. Группа у `RouteOnly` не нужна и указывать её нельзя — она читается только для `Pane`:

```csharp
        // Закрыты на технические работы. Не удалены и не спрятаны за флагом: страница, её тексты
        // и её тесты целы, и вернуть раздел в панель — это одно слово здесь. Пункт, который
        // открывается фразой "раздел закрыт", хуже отсутствующего пункта: он обещает и не даёт.
        new(RouteKeys.Sounds, "Nav_Sounds", RoutePlacement.RouteOnly, IconGlyphKey: "sound"),
        new(RouteKeys.Performance, "Nav_Performance", RoutePlacement.RouteOnly, IconGlyphKey: "speed"),
```

Строку `Sounds` при этом надо перенести из группы «Персонализация», а `Performance` — из «Обслуживания», поставив обе рядом с `Appearance` в конце списка, к остальным `RouteOnly`.

- [ ] **Шаг 4: Прогнать весь набор тестов**

```bash
dotnet test -c Release
```

Ожидается: PASS целиком. Особое внимание — `IconCatalogTests.Every_pane_item_carries_an_icon`: маршруты ушли из панели, и этот тест их больше не увидит.

- [ ] **Шаг 5: Коммит**

```bash
git add src/Winora.App/Navigation/RouteRegistry.cs tests/Winora.App.Tests/Navigation/RouteRegistryTests.cs
git commit -m "Take the two maintenance sections out of the pane"
```

---

## Task 3: Плитки — модель представления

**Files:**
- Create: `src/Winora.App/ViewModels/QuickAction.cs`
- Modify: `src/Winora.App/ViewModels/DashboardViewModel.cs`
- Modify: `src/Winora.App/Strings/ru-RU/Resources.resw`
- Create: `tests/Winora.App.Tests/ViewModels/DashboardViewModelTests.cs`

**Interfaces:**
- Consumes: `RouteRegistry.Find(string)` → `RouteDescriptor` с полями `Key`, `TitleResourceKey`, `IconGlyphKey`.
- Produces: `record QuickAction(string RouteKey, string Title, string IconGlyphKey, string Description)`; `DashboardViewModel.QuickActions` типа `IReadOnlyList<QuickAction>`, заполняется в `LoadAsync`. Конструктор `DashboardViewModel` получает третий параметр `RouteRegistry routes`.

- [ ] **Шаг 1: Написать падающий тест**

Новый файл `tests/Winora.App.Tests/ViewModels/DashboardViewModelTests.cs`:

```csharp
using Winora.App.Navigation;
using Winora.App.Services;
using Winora.App.ViewModels;
using Xunit;

namespace Winora.App.Tests.ViewModels;

public sealed class DashboardViewModelTests
{
    private sealed class EchoLocalization : ILocalizationService
    {
        public bool IsAvailable => true;

        public string Get(string resourceKey) => resourceKey;
    }

    private sealed class QuietRecovery : IRecoveryState
    {
        public Task<int> PendingCountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<RecoveryOutcomeView> RecoverAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RecoveryOutcomeView(0, 0, string.Empty));
    }

    private static DashboardViewModel Build() =>
        new(new QuietRecovery(), new EchoLocalization(), RouteRegistry.Create());

    [Fact]
    public async Task The_dashboard_offers_four_quick_actions()
    {
        var vm = Build();
        await vm.LoadAsync();

        Assert.Equal(4, vm.QuickActions.Count);
    }

    /// <summary>
    /// Плитка не хранит своё название и свой значок — она спрашивает их у того же реестра,
    /// из которого строится левая панель. Иначе переименованный раздел называется в двух
    /// местах по-разному, и расходятся они молча.
    /// </summary>
    [Fact]
    public async Task A_tile_takes_its_name_and_icon_from_the_route_registry()
    {
        var registry = RouteRegistry.Create();
        var vm = Build();
        await vm.LoadAsync();

        foreach (var action in vm.QuickActions)
        {
            var route = registry.Find(action.RouteKey);

            Assert.Equal(route.TitleResourceKey, action.Title);
            Assert.Equal(route.IconGlyphKey, action.IconGlyphKey);
        }
    }

    /// <summary>Одни названия сделали бы плитки копией панели, стоящей правее.</summary>
    [Fact]
    public async Task Every_tile_says_something_the_pane_does_not()
    {
        var vm = Build();
        await vm.LoadAsync();

        Assert.All(vm.QuickActions, action => Assert.False(string.IsNullOrWhiteSpace(action.Description)));
        Assert.Equal(4, vm.QuickActions.Select(action => action.Description).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>Плитка, чей маршрут не зарегистрирован, — это клик, который молча не работает.</summary>
    [Fact]
    public async Task Every_tile_points_at_a_registered_route()
    {
        var registry = RouteRegistry.Create();
        var vm = Build();
        await vm.LoadAsync();

        Assert.All(vm.QuickActions, action => Assert.True(registry.TryFind(action.RouteKey, out _)));
    }
}
```

Если фактические имена `IRecoveryState` и `RecoveryOutcomeView` отличаются — взять их из
`src/Winora.App/Services/RecoveryState.cs` и подставить, ничего не выдумывая.

- [ ] **Шаг 2: Запустить, убедиться, что не собирается**

```bash
dotnet test tests/Winora.App.Tests/Winora.App.Tests.csproj -c Release --filter "FullyQualifiedName~DashboardViewModelTests"
```

Ожидается: FAIL сборки — `QuickAction` не существует, у `DashboardViewModel` нет `QuickActions` и нет конструктора с тремя параметрами.

- [ ] **Шаг 3: Завести тип плитки**

Новый файл `src/Winora.App/ViewModels/QuickAction.cs`:

```csharp
namespace Winora.App.ViewModels;

/// <summary>Одна плитка на Главной: куда она ведёт и чем себя называет.</summary>
/// <remarks>
/// Название и значок — не собственные значения, а копия того, что реестр маршрутов даёт левой
/// панели. Плитка не имеет права называть раздел иначе, чем он назван в панели.
/// </remarks>
/// <param name="RouteKey">Ключ маршрута, куда ведёт нажатие.</param>
/// <param name="Title">Ключ ресурса с названием — тот же, что у пункта панели.</param>
/// <param name="IconGlyphKey">Ключ каталога значков — тот же, что у пункта панели.</param>
/// <param name="Description">Строка о том, зачем туда идти. Единственное, чего в панели нет.</param>
public sealed record QuickAction(
    string RouteKey,
    string Title,
    string IconGlyphKey,
    string Description);
```

- [ ] **Шаг 4: Добавить плитки в модель представления**

В `DashboardViewModel.cs` — четыре правки.

Первая: `using Winora.App.Navigation;` к остальным using.

Вторая, следом за константой `CommunityUrl`:

```csharp
    /// <summary>
    /// Что предлагает Главная, в порядке слева направо.
    /// </summary>
    /// <remarks>
    /// Ключи, и только ключи. Название и значок берутся из реестра при загрузке — см. QuickAction.
    /// </remarks>
    private static readonly string[] QuickActionRoutes =
    [
        RouteKeys.Themes,
        RouteKeys.Cursors,
        RouteKeys.Taskbar,
        RouteKeys.Bypass,
    ];
```

Третья, к свойствам:

```csharp
    /// <summary>
    /// Четыре плитки: то, ради чего программу открывают.
    /// </summary>
    /// <remarks>
    /// Читать замечание к классу целиком, прежде чем добавлять сюда пятую или что-то рядом.
    /// Проверка, которую этот экран заваливал трижды: вещь на Главной должна быть тем, за чем
    /// человек пришёл, а не тем, что приложение захотело сказать. Плитки — это навигация, и они
    /// проходят. Счётчики, сводки и лента изменений её не прошли и были сняты, причём ленту сняли
    /// на следующий день после того, как её попросили сделать.
    /// </remarks>
    [ObservableProperty]
    public partial IReadOnlyList<QuickAction> QuickActions { get; set; } = [];
```

Четвёртая: конструктор получает реестр,

```csharp
    private readonly RouteRegistry _routes;

    public DashboardViewModel(IRecoveryState recovery, ILocalizationService text, RouteRegistry routes)
    {
        _recovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _routes = routes ?? throw new ArgumentNullException(nameof(routes));
    }
```

и в начало `LoadAsync`, следом за присвоением `CommunityTooltip`:

```csharp
        QuickActions = QuickActionRoutes
            .Select(key => _routes.Find(key))
            .Select(route => new QuickAction(
                route.Key,
                route.TitleResourceKey,
                route.IconGlyphKey!,
                $"Dashboard_Quick_{char.ToUpperInvariant(route.Key[0])}{route.Key[1..]}"))
            .ToArray();
```

Ключи маршрутов односложные и латинские (`themes`, `cursors`, `taskbar`, `bypass`), так что
`ToUpperInvariant` здесь безопасен: турецкой «i» в них нет и быть не может.

- [ ] **Шаг 5: Добавить четыре строки описаний**

В `Resources.resw`, рядом с остальными `Dashboard_*`:

```xml
  <data name="Dashboard_Quick_Themes"><value>Оформление, обои и цвета целиком</value></data>
  <data name="Dashboard_Quick_Cursors"><value>Указатели мыши и их вид</value></data>
  <data name="Dashboard_Quick_Taskbar"><value>Расположение, значки и поведение</value></data>
  <data name="Dashboard_Quick_Bypass"><value>Доступ к Discord и YouTube</value></data>
```

- [ ] **Шаг 6: Починить регистрацию службы**

`DashboardViewModel` получает `RouteRegistry` из контейнера. Проверить `ServiceRegistration.cs`:
`RouteRegistry` уже регистрируется ради `ShellViewModel`, и тогда правка не нужна. Если нет —
зарегистрировать одиночкой рядом с ним.

- [ ] **Шаг 7: Тесты зеленеют**

```bash
dotnet test -c Release
```

Ожидается: PASS целиком, включая `ResourceKeyTests` — он увидит четыре новых ключа в `.resw`.

- [ ] **Шаг 8: Коммит**

```bash
git add src/Winora.App/ViewModels/QuickAction.cs src/Winora.App/ViewModels/DashboardViewModel.cs src/Winora.App/Strings/ru-RU/Resources.resw tests/Winora.App.Tests/ViewModels/DashboardViewModelTests.cs
git commit -m "Give the dashboard four quick actions, named by the route registry"
```

---

## Task 4: Плитки — разметка

**Files:**
- Modify: `src/Winora.App/Views/DashboardPage.xaml`
- Modify: `src/Winora.App/Views/DashboardPage.xaml.cs`
- Modify: `src/Winora.App/Resources/Styles/Controls.xaml`

**Interfaces:**
- Consumes: `DashboardViewModel.QuickActions` из задачи 3.
- Produces: экран, на котором есть что нажать. Кнопки сообщества на нём больше нет — она переезжает в задаче 5.

- [ ] **Шаг 1: Стиль плитки**

В `Controls.xaml`, к остальным стилям:

```xml
    <!--
      Плитка Главной. Кнопка, а не карточка с обработчиком нажатия: она ведёт себя как кнопка,
      значит ей полагаются фокус, пробел, Enter и подсветка при наведении — всё это уже есть у
      Button и ничего из этого не пришлось бы писать заново.
    -->
    <Style x:Key="WinoraQuickTile" TargetType="Button">
        <Setter Property="Background" Value="{ThemeResource WinoraCardBrush}" />
        <Setter Property="BorderBrush" Value="{ThemeResource WinoraCardStroke}" />
        <Setter Property="BorderThickness" Value="1" />
        <Setter Property="CornerRadius" Value="12" />
        <Setter Property="Padding" Value="18,16,18,16" />
        <Setter Property="HorizontalAlignment" Value="Stretch" />
        <Setter Property="HorizontalContentAlignment" Value="Left" />
        <Setter Property="VerticalAlignment" Value="Stretch" />
    </Style>
```

Если ключей `WinoraCardBrush` / `WinoraCardStroke` в проекте нет — взять те, которыми пользуется
карточка профиля в `ProfileCard.xaml`, и подставить их. Новых кистей не заводить.

- [ ] **Шаг 2: Ряд плиток на странице**

В `DashboardPage.xaml`, сразу под `<ctl:ProfileCard … />` и перед `<InfoBar …>`:

```xml
                        <!--
                          Переносятся, а не сжимаются: минимального размера у окна не задано, и
                          ряд из четырёх намертво зафиксированных плиток пережил бы сужение плохо.
                          UniformGridLayout переносит сам, когда ширины на следующую не хватает.
                        -->
                        <ItemsRepeater ItemsSource="{x:Bind ViewModel.QuickActions, Mode=OneWay}"
                                       Margin="{StaticResource WinoraStackGapL}">
                            <ItemsRepeater.Layout>
                                <UniformGridLayout MinItemWidth="220"
                                                   MinItemHeight="92"
                                                   MinColumnSpacing="12"
                                                   MinRowSpacing="12"
                                                   ItemsStretch="Fill" />
                            </ItemsRepeater.Layout>
                            <ItemsRepeater.ItemTemplate>
                                <DataTemplate x:DataType="vm:QuickAction">
                                    <Button Style="{StaticResource WinoraQuickTile}"
                                            Tag="{x:Bind RouteKey}"
                                            Click="OnQuickActionClick">
                                        <StackPanel Orientation="Horizontal" Spacing="14">
                                            <ContentPresenter x:Name="TileIcon" VerticalAlignment="Center" />
                                            <StackPanel Spacing="2" VerticalAlignment="Center">
                                                <TextBlock Style="{StaticResource WinoraQuickTileTitle}" />
                                                <TextBlock Style="{StaticResource WinoraQuickTileCaption}" />
                                            </StackPanel>
                                        </StackPanel>
                                    </Button>
                                </DataTemplate>
                            </ItemsRepeater.ItemTemplate>
                        </ItemsRepeater>
```

и в шапку страницы — `xmlns:vm="using:Winora.App.ViewModels"`.

**Внимание.** Название и значок в шаблон подставляются **не** привязкой: `Title` — это ключ
ресурса, а `IconGlyphKey` — ключ каталога, и оба надо разрешить. Разрешать их в шаблоне нечем.
Поэтому шаблон оставляет пустые `TextBlock` и `ContentPresenter`, а заполняет их код-behind на
`ElementPrepared` — так же, как `MainWindow.CreateItem` заполняет пункт панели. Стили
`WinoraQuickTileTitle` и `WinoraQuickTileCaption` завести рядом с `WinoraQuickTile`, взяв размеры
у `WinoraMetric` и `WinoraMetricCaption`.

- [ ] **Шаг 3: Заполнение и переход**

В `DashboardPage.xaml.cs`:

```csharp
    /// <summary>
    /// Разрешает ключи плитки в то, что видно: ресурс — в надпись, ключ каталога — в значок.
    /// </summary>
    /// <remarks>
    /// В шаблоне это сделать нечем: привязка отдала бы на экран сам ключ, что однажды уже видели
    /// в виде "[winora.cleanup.windows-serviced]" посреди страницы. Пункт панели заполняется тем
    /// же способом и по той же причине — см. MainWindow.CreateItem.
    /// </remarks>
    private void OnQuickActionPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not Button button || button.DataContext is not QuickAction action)
        {
            return;
        }

        var text = App.Services.GetRequiredService<ILocalizationService>();
        var title = text.Get(action.Title);

        // Пройтись по дереву шаблона и заполнить три места. Имена заданы в DataTemplate.
        if (button.FindName("TileIcon") is ContentPresenter presenter &&
            FluentIconCatalog.TryGetGlyph(action.IconGlyphKey, out var glyph))
        {
            presenter.Content = new FontIcon
            {
                Glyph = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 20,
            };
        }

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, title);
    }

    private void OnQuickActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string routeKey })
        {
            App.Services.GetRequiredService<INavigationService>().Navigate(routeKey);
        }
    }
```

Точное имя метода перехода взять из `src/Winora.App/Navigation/INavigationService.cs`, ничего не
выдумывая. Если разрешение имён внутри `DataTemplate` через `FindName` не сработает — а в WinUI
оно работает не всегда, — заменить шаблон на пользовательский элемент `QuickTile` с тремя
свойствами и одним методом `Show(QuickAction)`, по образцу `ProfileCard.Show`. Это надёжнее и
ближе к тому, как в этом проекте уже сделано.

- [ ] **Шаг 4: Убрать кнопку сообщества со страницы**

Удалить из `DashboardPage.xaml` весь `<Button Grid.Row="1" Style="{StaticResource WinoraCommunityButton}" …>`
вместе с `<Grid.RowDefinitions>` внешней сетки — она существовала только чтобы удержать эту
кнопку в углу. Из `DashboardPage.xaml.cs` удалить `OnCommunityClick` и разбор `"discord"` из
конструктора.

- [ ] **Шаг 5: Собрать и посмотреть**

```bash
dotnet build src/Winora.App/Winora.App.csproj -c Release -p:Platform=x64
```

Затем собрать переносимую сборку, поставить её и снять окно (см. Global Constraints). Убедиться
глазами: четыре плитки, у каждой значок, название и описание; кнопки в углу нет.

- [ ] **Шаг 6: Коммит**

```bash
git add src/Winora.App/Views/DashboardPage.xaml src/Winora.App/Views/DashboardPage.xaml.cs src/Winora.App/Resources/Styles/Controls.xaml
git commit -m "Draw the quick actions and take the corner button off the dashboard"
```

---

## Task 5: Подвал панели — сообщество и версия

**Files:**
- Modify: `src/Winora.App/ViewModels/ShellViewModel.cs`
- Modify: `src/Winora.App/ViewModels/DashboardViewModel.cs`
- Modify: `src/Winora.App/MainWindow.xaml`, `src/Winora.App/MainWindow.xaml.cs`
- Modify: `src/Winora.App/Strings/ru-RU/Resources.resw`
- Create: `tests/Winora.App.Tests/ViewModels/ShellViewModelTests.cs`

**Interfaces:**
- Consumes: `IAppEnvironment.Version` (строка), `ILocalizationService.Get`.
- Produces: `ShellViewModel.CommunityUrl` (константа), `ShellViewModel.CommunityTooltip`, `ShellViewModel.VersionLabel` — пустая строка, когда версия не читается.

- [ ] **Шаг 1: Написать падающий тест**

Новый файл `tests/Winora.App.Tests/ViewModels/ShellViewModelTests.cs`:

```csharp
using Winora.App.Navigation;
using Winora.App.Services;
using Winora.App.ViewModels;
using Xunit;

namespace Winora.App.Tests.ViewModels;

public sealed class ShellViewModelTests
{
    private sealed class EchoLocalization : ILocalizationService
    {
        public bool IsAvailable => true;

        public string Get(string resourceKey) => resourceKey switch
        {
            "Shell_Version" => "Winora {0}",
            _ => resourceKey,
        };
    }

    private sealed class FixedEnvironment(string version) : IAppEnvironment
    {
        public string Version { get; } = version;
    }

    private static ShellViewModel Build(string version) =>
        new(RouteRegistry.Create(), new EchoLocalization(), new FixedEnvironment(version));

    [Fact]
    public void The_shell_says_which_version_this_is()
    {
        var vm = Build("0.3.8.0");
        vm.Load();

        Assert.Equal("Winora 0.3.8.0", vm.VersionLabel);
    }

    /// <summary>
    /// Пустое место лучше слова "неизвестно": номер версии либо факт, либо его нет.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unreadable_version_leaves_the_line_off(string version)
    {
        var vm = Build(version);
        vm.Load();

        Assert.Equal(string.Empty, vm.VersionLabel);
    }
}
```

Остальные члены `IAppEnvironment` (если их больше одного) добавить в заглушку, взяв сигнатуры из
`src/Winora.App/Services/AppEnvironment.cs`.

- [ ] **Шаг 2: Запустить, убедиться, что не собирается**

```bash
dotnet test tests/Winora.App.Tests/Winora.App.Tests.csproj -c Release --filter "FullyQualifiedName~ShellViewModelTests"
```

Ожидается: FAIL сборки — у `ShellViewModel` нет ни такого конструктора, ни `VersionLabel`.

- [ ] **Шаг 3: Перенести сообщество и завести версию**

В `ShellViewModel.cs` — конструктор получает ещё два параметра, и добавляются три члена:

```csharp
    /// <summary>
    /// Discord проекта. Переехал сюда с Главной вместе с кнопкой: ссылка на сообщество — свойство
    /// оболочки, а не одной страницы, и в углу одного экрана она читалась как забытый элемент.
    /// </summary>
    /// <remarks>
    /// Литерал, а не настройка: сервер один, он не меняется от машины к машине, и ссылку, которую
    /// нечем переписать, ничто прочитанное с диска не уведёт в другое место.
    /// </remarks>
    public const string CommunityUrl = "https://discord.gg/bJCWdzx4D6";

    [ObservableProperty]
    public partial string CommunityTooltip { get; set; } = string.Empty;

    /// <summary>
    /// "Winora 0.3.8.0" в подвале панели, или пусто.
    /// </summary>
    /// <remarks>
    /// Текст, а не ссылка. Номер версии — это факт, а не орган управления; кнопка обновления живёт
    /// в настройках, и пункт "Настройки" стоит прямо над этой строкой.
    /// </remarks>
    [ObservableProperty]
    public partial string VersionLabel { get; set; } = string.Empty;
```

В конец `Load()`:

```csharp
        CommunityTooltip = _text.Get("Shell_CommunityAction");

        var version = _environment.Version;
        VersionLabel = string.IsNullOrWhiteSpace(version)
            ? string.Empty
            : string.Format(CultureInfo.CurrentCulture, _text.Get("Shell_Version"), version);
```

Из `DashboardViewModel` удалить константу `CommunityUrl`, свойство `CommunityTooltip` и строку
`CommunityTooltip = _text.Get("Dashboard_CommunityAction");`.

- [ ] **Шаг 4: Строки**

В `Resources.resw` заменить `Dashboard_CommunityAction` на `Shell_CommunityAction` с тем же
значением и добавить формат версии:

```xml
  <data name="Shell_CommunityAction"><value>Discord-сервер проекта</value></data>
  <data name="Shell_Version"><value>Winora {0}</value></data>
```

- [ ] **Шаг 5: Разметка подвала**

В `MainWindow.xaml`, внутрь `<NavigationView>` перед `<Border …>`:

```xml
            <NavigationView.PaneFooter>
                <Grid Padding="16,4,12,12" ColumnSpacing="8">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="*" />
                        <ColumnDefinition Width="Auto" />
                    </Grid.ColumnDefinitions>

                    <TextBlock Grid.Column="0"
                               VerticalAlignment="Center"
                               Style="{StaticResource WinoraPaneVersion}"
                               Text="{x:Bind Shell.VersionLabel, Mode=OneWay}" />

                    <Button Grid.Column="1"
                            Style="{StaticResource WinoraCommunityButton}"
                            ToolTipService.ToolTip="{x:Bind Shell.CommunityTooltip, Mode=OneWay}"
                            AutomationProperties.Name="{x:Bind Shell.CommunityTooltip, Mode=OneWay}"
                            Click="OnCommunityClick">
                        <Viewbox Width="20" Height="20">
                            <Canvas Width="24" Height="24">
                                <Path x:Name="CommunityGlyph" Fill="{ThemeResource WinoraCommunityGlyphBrush}" />
                            </Canvas>
                        </Viewbox>
                    </Button>
                </Grid>
            </NavigationView.PaneFooter>
```

`Shell` — свойство окна, ссылающееся на `ShellViewModel`. Если такого свойства нет, а модель лежит
в поле `_shell`, завести `public ShellViewModel Shell => _shell;`: `x:Bind` работает только с
доступными членами. Стиль `WinoraPaneVersion` завести в `Controls.xaml`: 11-12 пунктов, цвет
`TextFillColorTertiaryBrush`.

- [ ] **Шаг 6: Значок и переход в код-behind**

В конструктор `MainWindow.xaml.cs`, после `InitializeComponent()`, перенести разбор пути и
обработчик из `DashboardPage.xaml.cs` дословно:

```csharp
        if (FluentIconCatalog.TryGetPathData("discord", out var communityPath))
        {
            CommunityGlyph.Data = IconGeometry.FromPathData(communityPath);
        }
```

```csharp
    /// <summary>Открывает Discord проекта в браузере по умолчанию.</summary>
    /// <remarks>
    /// Адрес — константа модели представления, никогда не то, что приложение прочитало с диска
    /// или из реестра, так что перенаправить эту ссылку установленным софтом нельзя.
    /// </remarks>
    private async void OnCommunityClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(ShellViewModel.CommunityUrl));
        }
        catch (Exception)
        {
            // Нет браузера, или запуск отклонён. Прерывать из-за этого экран не стоит.
        }
    }
```

- [ ] **Шаг 7: Тесты и сборка**

```bash
dotnet test -c Release
```

Ожидается: PASS. `ResourceKeyTests` подтвердит, что `Dashboard_CommunityAction` больше никто не
просит, а `Shell_CommunityAction` и `Shell_Version` существуют.

- [ ] **Шаг 8: Коммит**

```bash
git add src/Winora.App tests/Winora.App.Tests/ViewModels/ShellViewModelTests.cs
git commit -m "Put the version and the community link in the pane footer"
```

---

## Task 6: Окно регистрации — фокус и центрирование

**Files:**
- Modify: `src/Winora.App/Views/RegistrationWindow.xaml`
- Modify: `src/Winora.App/Views/RegistrationWindow.xaml.cs`

**Interfaces:**
- Consumes: ничего.
- Produces: ничего, чем пользовались бы другие задачи.

Тестами не проверяется: и фокус, и вертикальное расположение — свойства разметки, до которых
тестовый прогон не доходит. Проверка — снимок живого окна.

- [ ] **Шаг 1: Центрировать содержимое шага**

В `RegistrationWindow.xaml` у `<Grid x:Name="StepHost">` поднять `MinHeight` с `320` до `548` и
дописать замечание:

```xml
                        <!--
                          One panel per step; the code-behind shows one and animates the swap.

                          548 is what is left of the card's 720 below the title strip, the padding
                          and the step indicator. The floor is here rather than on the card so the
                          shortest step still fills the window while a step taller than expected —
                          a large text scale on top of the password step — still grows the card and
                          falls back to the ScrollViewer above rather than clipping.
                        -->
                        <Grid x:Name="StepHost" MinHeight="548">
```

Затем у **каждой** из четырёх панелей шагов (`StepName`, `StepEmail`, `StepPassword` и панели
«Готово») заменить `VerticalAlignment="Top"` на `VerticalAlignment="Center"`.

Индикатор шагов при этом остаётся на месте: он лежит выше `StepHost`, в том же `StackPanel`, и
центрируется только содержимое под ним. Если бы центрировался весь блок, индикатор ездил бы
вверх-вниз при каждой смене шага.

- [ ] **Шаг 2: Перевести фокус на поле шага**

В `RegistrationWindow.xaml.cs` — метод и два его вызова:

```csharp
    /// <summary>
    /// Ставит фокус в поле текущего шага.
    /// </summary>
    /// <remarks>
    /// Без этого фокус достаётся крестику — первому элементу в обходе, — и Enter сразу после
    /// появления окна закрывает программу вместо перехода к следующему шагу. Окно регистрации
    /// единственное, что видно при первом запуске, так что это не мелкая оплошность: это первое,
    /// что человек делает с программой.
    /// </remarks>
    private void FocusCurrentStep()
    {
        Control? field = Model.Step switch
        {
            RegistrationStep.Name => NameBox,
            RegistrationStep.Email => EmailBox,
            RegistrationStep.Password => PasswordBox,
            _ => null,
        };

        _ = field?.Focus(FocusState.Programmatic);
    }
```

Точные имена шага и полей взять из уже написанного `RegistrationWindow.xaml` и
`RegistrationViewModel` — тип перечисления шагов и `x:Name` полей там уже есть, выдумывать их
нельзя.

Вызвать из `Loaded` окна (в конструкторе фокус ставить некуда — дерево ещё не построено) и из
`OnModelChanged`, когда меняется свойство шага, после того как код-behind показал новую панель.

- [ ] **Шаг 3: Собрать, поставить, посмотреть**

```bash
dotnet build src/Winora.App/Winora.App.csproj -c Release -p:Platform=x64
```

Затем: собрать переносимую сборку, отложить `%USERPROFILE%\Winora\State\profile.json` в сторону,
запустить, снять окно, вернуть файл на место. Убедиться: содержимое первого шага стоит по центру,
белой рамки на крестике нет, курсор мигает в поле имени.

- [ ] **Шаг 4: Коммит**

```bash
git add src/Winora.App/Views/RegistrationWindow.xaml src/Winora.App/Views/RegistrationWindow.xaml.cs
git commit -m "Centre the registration step and put the focus in its field"
```

---

## Task 7: Проверка целиком

- [ ] **Шаг 1: Весь набор тестов**

```bash
dotnet test -c Release
```

Ожидается: PASS целиком, ни одного пропущенного.

- [ ] **Шаг 2: Собрать и поставить**

```bash
dotnet publish src/Winora.App/Winora.App.csproj -c Release -p:WinoraPortable=true -p:Platform=x64 -o publish/final
```

Скопировать `publish/final/Winora.exe` поверх `%LOCALAPPDATA%\Programs\Winora\Winora.exe`,
предварительно закрыв программу. **Файл не переименовывать ни на каком шаге:** WinUI ищет
`resources.pri` по имени работающего модуля, и переименованный `.exe` не открывается вовсе
(`0xC000027B`). Имя задано `AssemblyName` в `Winora.App.csproj` и должно остаться `Winora`.

- [ ] **Шаг 3: Снять и посмотреть**

Снять главное окно и убедиться: четыре плитки; в панели нет «Звуков» и «Производительности»; в
подвале панели версия и значок Discord; у «Обхода блокировок» глобус, и логотип Discord на экране
ровно один.

- [ ] **Шаг 4: Отметить план выполненным**

Поставить галочки в этом файле и закоммитить его вместе с записью в леджер
`.superpowers/sdd/2026-08-26-winora-finished-look/progress.md`, если он заведён.

---

## Самопроверка плана

**Покрытие спецификации.** Раздел 3 (Главная) — задачи 3 и 4. Раздел 4 (панель без тупиков) —
задача 2. Раздел 5 (подвал панели, включая развод значков) — задачи 1 и 5. Раздел 6 (регистрация)
— задача 6. Раздел 9 (что проверять тестами) — тесты в задачах 2, 3 и 5; две строки этого раздела
про фокус и расположение прямо сказано проверять снимком, и это сделано в задачах 4, 6 и 7.

**Заглушек нет.** Код приведён целиком везде, где он нужен. Три места, где сказано «взять точное
имя из такого-то файла», — это не заглушки, а запрет выдумывать сигнатуру, которую я не проверял:
`IRecoveryState`, `INavigationService.Navigate` и перечисление шагов регистрации.

**Согласованность имён.** `QuickAction(RouteKey, Title, IconGlyphKey, Description)` заведён в
задаче 3 и используется в задаче 4 в том же виде. `DashboardViewModel.QuickActions` — то же имя в
тестах, модели и разметке. `ShellViewModel.CommunityUrl` заведён в задаче 5 и там же используется
в `MainWindow`; из `DashboardViewModel` одноимённая константа в той же задаче удаляется, так что
двух не остаётся ни на одном шаге.

**Известный риск.** Заполнение шаблона `ItemsRepeater` через `FindName` (задача 4, шаг 3) в WinUI
работает не всегда. Запасной путь — пользовательский элемент `QuickTile` с методом `Show` — назван
там же, в том же шаге, а не оставлен на догадку.
