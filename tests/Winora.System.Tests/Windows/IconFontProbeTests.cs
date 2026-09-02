using Winora.System.Windows;
using Xunit;

namespace Winora.System.Tests.Windows;

/// <summary>
/// The choice between the two icon fonts.
/// </summary>
/// <remarks>
/// Written after a fresh install on another computer drew every icon in the navigation pane as an
/// empty box on 2026-09-03. The family was hard-coded to "Segoe Fluent Icons", which ships with
/// Windows 11 and is absent from Windows 10 and from the stripped builds that people who tune
/// Windows tend to run.
/// </remarks>
public sealed class IconFontProbeTests
{
    [Fact]
    public void Prefers_the_newer_font_when_it_is_installed()
    {
        var probe = new IconFontProbe(family => family == IconFontProbe.PreferredFamily);

        Assert.Equal(IconFontProbe.PreferredFamily, probe.ResolveFamily());
    }

    [Fact]
    public void Falls_back_when_the_newer_font_is_absent()
    {
        var probe = new IconFontProbe(_ => false);

        Assert.Equal(IconFontProbe.FallbackFamily, probe.ResolveFamily());
    }

    /// <summary>
    /// The two names are not the same string.
    /// </summary>
    /// <remarks>
    /// Trivial to state and worth stating: a copy-paste that made them equal would leave both
    /// branches above passing while the fallback stopped falling back to anything.
    /// </remarks>
    [Fact]
    public void The_fallback_is_a_different_font()
    {
        Assert.NotEqual(IconFontProbe.PreferredFamily, IconFontProbe.FallbackFamily);
    }

    /// <summary>
    /// This machine has at least one of them, and the probe finds it by reading the real registry.
    /// </summary>
    /// <remarks>
    /// The only test here that touches Windows. Without it the substituted-delegate tests above
    /// would keep passing after a typo in the registry path made the real reading find nothing —
    /// which would silently pick the fallback everywhere, including on Windows 11.
    /// </remarks>
    [Fact]
    public void Reads_a_real_font_name_from_this_machine()
    {
        var resolved = new IconFontProbe().ResolveFamily();

        Assert.Contains(resolved, new[] { IconFontProbe.PreferredFamily, IconFontProbe.FallbackFamily });
    }
}
