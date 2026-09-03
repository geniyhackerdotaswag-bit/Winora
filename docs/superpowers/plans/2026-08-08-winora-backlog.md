# Winora backlog

Recorded 2026-08-08 at the owner's request. Nothing here is being built yet. Each entry says what it
is, why it is not being done now, and what would have to be true before it starts.

The order below is the order the owner set: everything in "After the app is finished" waits until the
application itself is done, and the Discord bot waits longest.

---

## 1. Match the Windows colour scheme to Winora's — done, as a guided route

**Shipped 2026-08-09, and not as originally imagined.** The appearance screen names the Windows
theme the current scheme corresponds to and opens `ms-settings:personalization-colors`. It does not
write anything.

The reason it cannot write is settled rather than open, and it was found before any code was
written. The values behind the Windows light/dark preference and the accent —
`Themes\Personalize\AppsUseLightTheme` and the DWM colourization values — are not documented by
Microsoft as programmatically settable, and the specification's non-goals already name "theme
registry tweaks" among the things Winora does not do. So the standing rule decides it: a mechanism
with no Learn URI ships as `Guided`, never as an unconditional fallback.

That also disposes of the sync question. A switch keeping Windows continuously in step would have
to write on every colour change, which is exactly the write that is not available. What is left is
the honest half: say which theme matches, and open the page where Windows makes it a one-click
change.

The approximation problem noted earlier stands and is why only light/dark is mentioned, not the
accent: Winora's accent is a free colour and Windows picks from its own set, so naming a "matching"
accent would be inventing a correspondence that does not exist.

## 2. Useful interaction on the dashboard

The dashboard shows a title and, when something is wrong, one notice. The owner wants it to be worth
opening — but specifically **not** decorative widgets. Useful interaction only, and it does not have
to change anything to qualify.

Candidates that fit "useful and honest", none of them chosen yet:

- What Winora has changed on this machine, most recent first, with a rollback within reach.
- Live figures that already exist behind the performance screen.
- Whether the last change verified, and what is still pending a restart.

The rule that constrains this: a screen built over a data source nothing writes to is a feature that
lies. Whatever goes here must read something real.

## 3. Publish as open source

The owner intends this to be an open-source project. **Not yet** — recorded so it is not forgotten.

Before it starts:

- The signing certificate, its private key, and `%USERPROFILE%\WinoraSigning` must stay out of the
  repository. `.gitignore` covers packages and certificates today; re-check before the first push.
- `AGENTS.md` and the specs carry measured findings about this machine, including paths under the
  owner's profile. They are the most valuable documents here and also the ones naming a real person.
- Decide the licence, and check the bundled third-party pieces: the bypass module runs
  Flowseal's `zapret-discord-youtube`, which is fetched at runtime rather than vendored, and the
  Discord mark in the icon catalog is that company's.

## 4. One executable that unpacks itself

Ship as a single file that creates the directories and resources it needs on first run, instead of a
layout of loose files.

Why it is not now: Winora is an MSIX package today, and the whole safety story rests on that — the
registry domains only work from a properly installed signed package, and that was measured. A
single-file build is a second distribution shape, not a replacement, and it has to answer what
happens to the store at `%USERPROFILE%\Winora\State`, which deliberately outlives the app.

Before it starts: decide whether the single file replaces the MSIX or sits beside it, and re-verify
the registry domains from whichever shape is meant to ship.

## 5. Discord bot

The owner has a Discord bot and intends Winora and the bot to become one product.

**Only after the application is finished.** That is the owner's sequencing, not a technical
constraint.

### What is there

`M:\папки\проекты\TTFD-Discord`, surveyed 2026-08-08 without editing anything.

Python, `discord.py` 2.3.2, with `psycopg2-binary` — so PostgreSQL, and a `migrate_to_postgres.py`
at the root suggests it moved there from JSON files. Thirty-four `.py` files, of which twenty-nine
are in `py\`. The substantial ones:

| File | Size | What it appears to own |
| --- | --- | --- |
| `slash_commands.py` | 45 KB | Slash command surface |
| `bot.py` | 31 KB | Client and event wiring |
| `game_integration.py` | 23 KB | A link to some game |
| `database.py` / `database_postgres.py` | 20 KB / 18 KB | Two storage layers, plus a `database_sync_wrapper.py` between them |
| `shop_system.py`, `tickets_system.py`, `voice_tracking.py` | 13–19 KB each | Shop, tickets, voice-time tracking |
| `verification_system.py`, `rank_roles.py`, `updates_system.py` | 4–10 KB | Verification, XP roles, an auto-update mechanism |

Six `test_*.py` files exist at the root and in `py\`; they look like scripts rather than a suite.

### What to expect before writing any code

- **The root is 122 `.txt` and 54 `.md` status files** — `ГОТОВО_ЗАПУСКАЙ.txt`,
  `ФИНАЛЬНАЯ_СВОДКА_1.9.txt`, `ИСПРАВЛЕНИЯ_ГОТОВЫ.txt` and so on, many describing the same features
  at different moments. They are a changelog written as separate files, and several contradict each
  other by construction. Do not treat any of them as current: read the code, and use them only as
  hints about intent.
- **Two database layers coexist.** `database.py`, `database_postgres.py` and a sync wrapper between
  them is the shape of a migration that was not finished. That is the first thing to establish:
  which one is live.
- **It is not a git repository.** There is no history to read and no way to see what changed when,
  which is most of why the `.txt` files exist. Putting it under git is probably the first useful act.

### Credentials

`.env` is present and holds the real token. **It is not read, not copied, and not quoted anywhere** —
not into this document, not into a commit, not into a chat message. The owner said they would supply
tokens when needed; the right destination is that `.env`, and nowhere else.

`.gitignore` already excludes `.env`, `*.env`, `__pycache__/` and `venv/`, and keeps `.env.example`.
That is correct and it must survive whatever restructuring happens. Note it also ignores `*.json`
wholesale, which will silently exclude configuration that ought to be tracked — worth narrowing when
the repository is set up, and worth checking that nothing needed was lost.

### The unanswered question

What "one product" means. Winora is a local Windows application that changes the machine it runs on;
the bot is a server-side service for a Discord guild. Nothing yet says what they share — accounts, a
licence check, telemetry, a shared shop currency, something else. That has to be decided before any
integration code, because it determines whether Winora ever talks to a network service at all, and
today it deliberately does not.

---

## Сайт TTFD: отложенное (записано 2026-08-14)

Работа по сайту прервана на переносе оформления под референс
[noth.in](https://www.noth.in). Сделано: белый холст, чёрный текст, чёрные
пилюли, типографика по замерам с живой страницы (вес 500, без капса, трекинг
−0.01em, интерлиньяж 1.0, h1 5vw), моноширинные подписи, номера разделов в
скобках, живой фон удалён.

Не сделано — владелец назвал это прямо перед переключением на приложение:

- **Эффекта курсора нет.** У референса курсор заменён собственным: кружок,
  который тянется за указателем с задержкой и меняет вид над ссылками. Это
  заметная часть впечатления, и без него страница выглядит «просто белой».
- **Анимаций нет почти нигде.** У референса плавная прокрутка (Lenis или
  подобное), появление блоков при въезде в экран, бегущая строка в подвале,
  прелоадер со счётчиком 0→100 и маркой по центру.
- **Шапка не сверена.** У них марка слева и значок меню справа; у нас пилюля с
  четырьмя пунктами. Для 1-в-1 навигацию надо убрать под значок — но у них
  четыре страницы, а у нас восемь, и ходят между ними постоянно. Решение за
  владельцем.
- **Восемь страниц не просмотрены глазами** после переворота палитры. Проверены
  только главная и магазин; достижения, рейтинг, оформление, подарки и 404 —
  нет.

Два ограничения, которые не обойти и о которых владелец знает:

- **PP Neue Montreal** из референса — коммерческий шрифт Pangram Pangram.
  Скачать и положить в проект нельзя. Осталась пара Funnel Display + Onest.
- **Моноширинный системный**, не IBM Plex Mono. Plex под OFL, положить локально
  можно — просто ещё не делали.

Что заметили попутно и починили, чтобы не потерялось: в живом режиме **четыре
страницы ломались у того, кто не вошёл** (магазин падал, профиль обвинял
несуществующего человека). Сделан общий экран «нужен вход». В демо-режиме этого
не было видно никогда — там выдуманный профиль есть всегда.

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

## Запуск от имени администратора: отложено (записано 2026-08-27)

Владелец попросил, чтобы Winora всегда открывалась с правами администратора. Сделать позже, но
записать сразу три вещи, которые придётся решить.

- **Чем это оплачивается.** Окно, запущенное с повышенными правами, нельзя снять (`PrintWindow`
  отдаёт пустой кадр), к нему не присоединяется UI Automation, и его нельзя закрыть ни
  `Stop-Process`, ни `CloseMainWindow` из обычного сеанса. Весь контур живой проверки, которым
  27 августа было поймано с десяток настоящих ошибок — наезжающее кольцо, обрыв заметок на
  полуслове, неотличимые записи журнала, — перестаёт работать. Это не довод против, но это цена, и
  назвать её нужно до, а не после.
- **Как именно повышать.** Прошлый подход — перезапуск себя с глаголом `runas` — оставлял на
  секунду два процесса и мигание окна. Манифест с `requireAdministrator` честнее: Windows спросит
  один раз, до старта, и второго процесса не будет. Но тогда программа не запустится вовсе, если
  UAC отклонён, и это надо показать по-человечески.
- **Что делать с обновлением.** Установленная копия лежит в `%LOCALAPPDATA%`, куда обычный
  пользователь пишет и так. Повышение прав ничего там не открывает нового, но перезапуск после
  подмены должен сохранять повышение — иначе после каждого обновления программа откатится к
  обычным правам и разница станет незаметной и непредсказуемой.

Пока не сделано, в силе остаётся [[winora-elevated-app-blocks-tooling]]: переносимая сборка
запускается обычным процессом, и снимки с автоматизацией работают.

## Сервер для сайта TTFD и бота: решение принято 2026-08-30

Разговор о том, «когда ставить личные кабинеты на сервер», свёлся к двум фактам, которые сужают
выбор до одного варианта. Записаны, чтобы не выяснять их заново.

- **Discord заблокирован в России на сетевом уровне.** Российский хостинг для бота не годится: он
  не достучится до Discord API без обхода, а обход на сервере — лишняя движущаяся деталь. Значит
  локация только зарубежная.
- **Карты Visa/Mastercard российских банков не работают за границей с 2022 года.** Railway, Fly,
  Vercel, DigitalOcean отпадают не по качеству, а потому что им нечем заплатить.

Пересечение: провайдер, принимающий рубли, с зарубежной локацией. Практически это **Aeza**
(Германия, Нидерланды, Финляндия; от ~260 ₽/мес, рубли и российские карты). Timeweb Cloud дешевле
и надёжнее по репутации, но его дата-центры в Москве и Петербурге — для бота непригодны по первой
причине.

**Одна машина на всё:** бот, сайт и PostgreSQL рядом. Один счёт, одно место. Это же снимает
зависимость бота от того, включён ли компьютер владельца.

**Порядок:** домен (нужен для приложения Discord) → приложение в Discord Developer Portal
(бесплатно, даёт Client ID и Secret) → хостинг. Платить приходится только на третьем шаге, когда
уже есть что размещать.

**Что не делает Claude:** не заводит учётные записи и не вводит платёжные данные. Домен, оплата,
аккаунт хостинга и приложение Discord — за владельцем.

**Что можно писать до сервера:** сайт целиком (страницы, вход через Discord, профиль, рулетка),
приведение бота к виду, готовому к развёртыванию, и скрипты выкладки.

**Личные кабинеты в самой Winora строить отдельно не нужно.** Учётная запись имеет смысл ровно
тогда, когда она общая с сайтом; две системы входа — две базы и две вещи, ломающиеся порознь. То,
ради чего кабинет спрашивали раньше — перенос настроек между машинами — закрыто файлом в v0.7.0 и
сервера не требует.

---

## Чат с поддержкой внутри программы (записано 2026-09-03)

**Идея владельца, не план.** Записано, чтобы не забыть; ничего не проектируется и не пишется,
пока владелец не скажет.

Человек пишет в поддержку прямо из Winora, сообщение уходит владельцу, ответ приходит обратно в
программу. Смысл в том, что писать приходится оттуда, где сломалось: сейчас единственный путь —
уйти в Discord, а туда уходит тот, кто и так знает, что делать.

Что придётся решить, когда дойдёт очередь:

- **Куда приходит.** Telegram владельцу — самое дешёвое: бот у нас уже есть в соседнем проекте,
  а сервер и база — на Railway. Почта проще, но отвечать с неё некому и некогда.
- **Как возвращается ответ.** Односторонняя отправка — это не чат, а форма обратной связи, и
  назвать её чатом значит пообещать больше, чем сделано. Настоящий чат требует, чтобы программа
  спрашивала сервер о новых сообщениях, — а это первое, что заставит Winora ходить в сеть
  постоянно, а не только за ключом.
- **Кто пишет.** Ключ уже привязан к машине, так что отправитель опознаётся без отдельного входа.
  Это же и ограничитель: писать может тот, у кого есть ключ или идут пробные дни, иначе форма
  превращается в открытый ящик для спама.
- **Что прикладывать.** У Winora есть журнал действий и `diagnostics.log`, и половина обращений
  без них не разбирается. Но это личные данные с машины человека, и уезжать они могут только с
  его явного согласия, показанного до отправки, а не мелким шрифтом.

Порядок: не раньше, чем закроется заслон по ключу при запуске, — иначе поддержку будет писать
некому и не о чем.
