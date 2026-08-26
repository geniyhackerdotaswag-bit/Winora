using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Winora.Architecture.Tests;

/// <summary>
/// The animation screen offers a restart with administrator rights for exactly one block code.
/// </summary>
/// <remarks>
/// <para>
/// A view model may not reference the system layer, so the code is written there as a literal.
/// A literal that drifts from the constant it copies fails silently and in the worst way: the
/// notice simply never appears, and the screen looks like eleven settings that are broken for no
/// stated reason. That is what happened on 2026-08-26, when the literal was written in the
/// underscored spelling the resource file uses rather than the dotted one the probe emits.
/// </para>
/// <para>
/// Compared as source text, the way every test in this project works: loading the WinUI assembly
/// from a plain host fails on activation regardless of what is being asserted.
/// </para>
/// </remarks>
public sealed class ThemesElevationCodeTests
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void The_screen_matches_the_code_the_system_layer_declares()
    {
        var declared = Read(
            Path.Combine(Root, "src", "Winora.System", "Safety", "CapabilityBlockCodes.cs"),
            @"Prefix\s*=\s*""([^""]+)""");

        var suffix = Read(
            Path.Combine(Root, "src", "Winora.System", "Safety", "CapabilityBlockCodes.cs"),
            @"TargetNotWritable\s*=\s*Prefix\s*\+\s*""([^""]+)""");

        var used = Read(
            Path.Combine(Root, "src", "Winora.App", "ViewModels", "ThemesViewModel.cs"),
            @"WritableByAdministrator\s*=\s*""([^""]+)""");

        Assert.Equal(declared + suffix, used);
    }

    private static string Read(string path, string pattern)
    {
        var match = Regex.Match(File.ReadAllText(path), pattern);
        Assert.True(match.Success, $"Nothing matched /{pattern}/ in {Path.GetFileName(path)}.");
        return match.Groups[1].Value;
    }

    private static string FindRoot([CallerFilePath] string path = "") =>
        Directory.GetParent(path)!.Parent!.Parent!.FullName;
}
