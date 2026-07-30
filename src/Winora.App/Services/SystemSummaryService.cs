using Winora.System.Windows;

namespace Winora.App.Services;

/// <param name="VersionText">Documented major.minor.build triple.</param>
/// <param name="MeetsBaseline">Whether the running build satisfies the supported Windows 11 minimum.</param>
/// <param name="BaselineText">The minimum Winora supports, for display next to the actual version.</param>
public sealed record SystemSummary(string VersionText, bool MeetsBaseline, string BaselineText);

/// <summary>
/// Reports the running Windows version to the presentation layer without letting a ViewModel
/// reference <c>Winora.System</c> directly. Read-only.
/// </summary>
public interface ISystemSummaryService
{
    SystemSummary Read();
}

/// <inheritdoc />
public sealed class SystemSummaryService : ISystemSummaryService
{
    private readonly IWindowsBuildProbe _probe;

    public SystemSummaryService(IWindowsBuildProbe probe)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    public SystemSummary Read()
    {
        var facts = _probe.Read();
        var baseline = WindowsBuildFacts.Windows11Baseline;

        return new SystemSummary(
            $"{facts.Major}.{facts.Minor}.{facts.Build}",
            facts.MeetsMinimum(baseline),
            $"{baseline.Major}.{baseline.Minor}.{baseline.Build}");
    }
}
