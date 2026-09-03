using Winora.Core.Licence;
using Winora.Infrastructure.Licence;
using Winora.System.Licence;

namespace Winora.App.Services;

/// <summary>Активация ключа, пробные дни и ответ на вопрос «пускать ли внутрь».</summary>
public interface ILicenceService
{
    /// <summary>Что известно прямо сейчас, без обращения к сайту.</summary>
    LicenceState Current { get; }

    /// <summary>Отпечаток этой машины. Пустой, если его не удалось прочитать.</summary>
    string HardwareId { get; }

    /// <summary>Знает ли эта сборка адрес сайта.</summary>
    bool IsConfigured { get; }

    /// <summary>Меняет ключ на токен и сохраняет и то и другое.</summary>
    Task<LicenceResult> ActivateAsync(string key, string? promoCode, CancellationToken cancellationToken);

    /// <summary>Спрашивает сайт про сохранённый токен, если он есть и пора.</summary>
    Task<LicenceResult> RefreshAsync(bool force, CancellationToken cancellationToken);

    /// <summary>
    /// Есть ли право пользоваться программой прямо сейчас.
    /// </summary>
    /// <remarks>
    /// Ключ, потом проба. Пробу просит только тот, у кого ключа нет: спросить её
    /// при живой подписке значило бы потратить единственную пробу машины впустую.
    /// </remarks>
    Task<LicenceResult> EnsureAccessAsync(CancellationToken cancellationToken);

    /// <summary>Забывает ключ на этой машине. Сама подписка не трогается.</summary>
    bool Forget();
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// Программа без действующего ключа или пробы не работает — это решение владельца
/// от 3 сентября 2026, и оно поменяло то, чем Winora была до сих пор.
/// </para>
/// <para>
/// Скажу здесь то же, что сказано в переписке: программа открывается, и проверку
/// снимут. Привязка к железу поднимает планку, не более. Смысл в том, чтобы
/// платное лежало на сервере — наборы курсоров, списки для обхода, перенос
/// настроек: взломанная копия их не получит, потому что их нет в файле.
/// </para>
/// </remarks>
public sealed class LicenceService : ILicenceService
{
    private readonly ILicenceClient _client;
    private readonly ILicenceStore _store;
    private readonly IHardwareId _hardware;
    private readonly TimeProvider _time;

    public LicenceService(
        ILicenceClient client,
        ILicenceStore store,
        IHardwareId hardware,
        TimeProvider? time = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
        _time = time ?? TimeProvider.System;
    }

    public LicenceState Current => _store.Read();

    public string HardwareId => _hardware.Value;

    public bool IsConfigured => LicenceEndpoint.IsConfigured;

    public async Task<LicenceResult> ActivateAsync(
        string key,
        string? promoCode,
        CancellationToken cancellationToken)
    {
        var (result, token) = await _client
            .ActivateAsync(key, Environment.MachineName, Blank(promoCode), _hardware.Value, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return result;
        }

        // Сохраняется только после согласия сайта. Запись по дороге оставила бы
        // машину с подпиской, которую сайт не подтверждал.
        return _store.Write(token, result.State)
            ? result
            : LicenceResult.Failed(LicenceOutcome.Unreachable);
    }

    public async Task<LicenceResult> RefreshAsync(bool force, CancellationToken cancellationToken)
    {
        var stored = _store.Read();
        var token = _store.Token;

        if (!stored.Exists || token.Length == 0)
        {
            return LicenceResult.Failed(LicenceOutcome.Rejected);
        }

        if (!force && !stored.NeedsRecheck(_time.GetUtcNow()))
        {
            return new LicenceResult(LicenceOutcome.Confirmed, stored);
        }

        var result = await _client.CheckAsync(token, _hardware.Value, cancellationToken).ConfigureAwait(false);

        if (result.Succeeded)
        {
            // Имя машины проверка не возвращает, поэтому берётся сохранённое.
            _store.Write(token, result.State with { Machine = stored.Machine });
            return result;
        }

        /*
         * Недоступный сайт подписку не заканчивает.
         *
         * К Winora тянутся, когда машина ведёт себя плохо, — то есть тогда, когда
         * сеть работает хуже всего. Считать молчание за истёкший срок значило бы
         * отнимать программу в худший момент и без единого доказательства.
         */
        if (result.Outcome is LicenceOutcome.Unreachable or LicenceOutcome.NotConfigured)
        {
            return new LicenceResult(result.Outcome, stored);
        }

        /*
         * «Другая машина» — тоже не повод стирать ключ.
         *
         * Человек мог перенести папку и вернуть её обратно, или сменить диск.
         * Стереть здесь значило бы заставить его искать ключ, который на этой
         * машине ещё вчера работал. Он отвяжет машины в кабинете и вернётся.
         */
        if (result.Outcome is LicenceOutcome.OtherMachine)
        {
            return result;
        }

        // Сайт ответил, и ответ был «нет». Сохранённая копия теперь неверна.
        _store.Clear();
        return result;
    }

    public async Task<LicenceResult> EnsureAccessAsync(CancellationToken cancellationToken)
    {
        var stored = _store.Read();

        if (stored.Exists && !stored.IsTrial)
        {
            return await RefreshAsync(force: false, cancellationToken).ConfigureAwait(false);
        }

        /*
         * Проба спрашивается у сервера каждый раз, а не читается из своей же записи.
         *
         * Записанный рядом с программой срок сбрасывается удалением папки, и проба
         * становится бесконечной. Сервер помнит машину по отпечатку и второй пробы
         * ей не даст — в этом и весь смысл. Цена названа честно: первый запуск
         * требует интернета.
         */
        var trial = await _client.TrialAsync(_hardware.Value, cancellationToken).ConfigureAwait(false);

        if (trial.Outcome is LicenceOutcome.Trial)
        {
            // Токена у пробы нет: она привязана к железу, а не к ключу.
            _store.Write(string.Empty, trial.State);
        }

        return trial;
    }

    public bool Forget() => _store.Clear();

    private static string? Blank(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
