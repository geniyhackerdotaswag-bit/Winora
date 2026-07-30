using Winora.Core.Changes;
using Winora.Core.Contracts;
using Winora.System.Operations;
using Winora.System.Windows;
using Xunit;

namespace Winora.System.Tests.Operations;

public sealed class CompositeOperationCatalogTests
{
    private static VisualEffectsOperation Known() =>
        new(VisualEffectSetting.ClientAreaAnimation, new StubAccess());

    [Fact]
    public void A_registered_operation_resolves_by_its_catalog_id()
    {
        var known = Known();
        var catalog = new CompositeOperationCatalog([known], []);

        Assert.True(catalog.TryResolve(known.OperationId, out var resolved));
        Assert.Same(known, resolved);
    }

    /// <summary>
    /// The reason this type exists. A domain whose targets are discovered at runtime cannot be
    /// registered up front, and startup reconciliation runs in a fresh process where no instance
    /// from the original session survives, so the id alone has to be enough.
    /// </summary>
    [Fact]
    public void An_operation_absent_at_startup_is_reconstructed_from_its_id_alone()
    {
        var catalog = new CompositeOperationCatalog([], [new SlugFactory()]);

        Assert.True(catalog.TryResolve("winora.dynamic.some-target", out var resolved));
        Assert.NotNull(resolved);
        Assert.Equal("winora.dynamic.some-target", resolved!.OperationId);
    }

    [Fact]
    public void Reconstruction_is_repeatable_across_independent_catalogs()
    {
        var first = new CompositeOperationCatalog([], [new SlugFactory()]);
        var second = new CompositeOperationCatalog([], [new SlugFactory()]);

        Assert.True(first.TryResolve("winora.dynamic.x", out var a));
        Assert.True(second.TryResolve("winora.dynamic.x", out var b));
        Assert.Equal(a!.OperationId, b!.OperationId);
    }

    [Fact]
    public void Registered_operations_win_over_factories()
    {
        var known = Known();
        var catalog = new CompositeOperationCatalog([known], [new AlwaysFactory(known.OperationId)]);

        Assert.True(catalog.TryResolve(known.OperationId, out var resolved));
        Assert.Same(known, resolved);
    }

    /// <summary>
    /// A factory returning an operation under a different id would let a confirmed plan be carried
    /// out by something other than the operation it names.
    /// </summary>
    [Fact]
    public void A_factory_that_returns_a_different_operation_is_rejected_loudly()
    {
        var catalog = new CompositeOperationCatalog([], [new AlwaysFactory("winora.other.thing")]);

        Assert.Throws<InvalidOperationException>(() => catalog.TryResolve("winora.dynamic.x", out _));
    }

    [Fact]
    public void An_unknown_id_fails_rather_than_returning_an_arbitrary_operation()
    {
        var catalog = new CompositeOperationCatalog([Known()], []);

        Assert.False(catalog.TryResolve("winora.nope", out var resolved));
        Assert.Null(resolved);
        Assert.Throws<KeyNotFoundException>(() => catalog.Resolve("winora.nope"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_id_is_refused(string? operationId)
    {
        var catalog = new CompositeOperationCatalog([Known()], []);

        Assert.False(catalog.TryResolve(operationId!, out _));
    }

    private sealed class SlugFactory : IOperationFactory
    {
        public bool TryCreate(string operationId, out IOperation? operation)
        {
            if (operationId.StartsWith("winora.dynamic.", StringComparison.Ordinal))
            {
                operation = new FakeOperation(operationId);
                return true;
            }

            operation = null;
            return false;
        }
    }

    private sealed class AlwaysFactory(string produces) : IOperationFactory
    {
        public bool TryCreate(string operationId, out IOperation? operation)
        {
            operation = new FakeOperation(produces);
            return true;
        }
    }

    private sealed class FakeOperation(string operationId) : IOperation
    {
        public string OperationId { get; } = operationId;

        public ValueTask<OperationCapability> ProbeAsync(OperationTarget target, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ChangePlan> PreviewAsync(OperationDraft draft, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<StepResult> ApplyStepAsync(ChangePlan plan, ChangeStep step, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<VerificationResult> VerifyStepAsync(ChangePlan plan, ChangeStep step, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<StepResult> RollbackStepAsync(RollbackPlan plan, ChangeStep step, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubAccess : IVisualEffectsAccess
    {
        public VisualEffectReading Read(VisualEffectSetting setting) => new(true, true, false);

        public VisualEffectWriteOutcome Write(VisualEffectSetting setting, bool value) =>
            VisualEffectWriteOutcome.Written;
    }
}
