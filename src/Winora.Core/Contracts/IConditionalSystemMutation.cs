namespace Winora.Core.Contracts;

/// <summary>
/// Declares that an operation's final expected-state comparison and indivisible write are
/// protected by one documented conditional Windows mechanism rather than a read-then-write gap.
/// </summary>
public interface IConditionalSystemMutation
{
    string ConditionalMutationMechanismId { get; }
}
