namespace Winora.Core.Contracts;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
