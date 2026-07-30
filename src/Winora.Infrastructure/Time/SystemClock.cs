using Winora.Core.Contracts;

namespace Winora.Infrastructure.Time;

/// <summary>
/// The production <see cref="IClock"/>. Backed by <see cref="TimeProvider"/> so tests can substitute
/// time the same way the rest of Infrastructure already does.
/// </summary>
public sealed class SystemClock : IClock
{
    private readonly TimeProvider _timeProvider;

    public SystemClock()
        : this(TimeProvider.System)
    {
    }

    public SystemClock(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <remarks>Always UTC: durable records must never carry a local timestamp.</remarks>
    public DateTimeOffset UtcNow => _timeProvider.GetUtcNow();
}
