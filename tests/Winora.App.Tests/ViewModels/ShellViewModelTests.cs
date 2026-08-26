using Winora.App.Navigation;
using Winora.App.Services;
using Winora.App.ViewModels;
using Xunit;

namespace Winora.App.Tests.ViewModels;

/// <summary>The pane's own two lines: which version this is, and where the project lives.</summary>
public sealed class ShellViewModelTests
{
    private sealed class EchoLocalization : ILocalizationService
    {
        public bool IsAvailable => true;

        // The key comes back, except where a test needs a real format template.
        public string Get(string resourceKey) => resourceKey switch
        {
            "Shell_Version" => "Winora {0}",
            _ => resourceKey,
        };
    }

    private sealed class FixedEnvironment(string version) : IAppEnvironment
    {
        public string Version { get; } = version;

        public bool IsElevated => false;

        public string StorageRoot => @"C:\nowhere";
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
    /// SourceLink appends "+&lt;commit&gt;" to the informational version, and forty characters of
    /// hexadecimal in the corner of a window are for a bug report, not for a person looking to see
    /// what they are running. A pre-release label survives, because that one is worth knowing.
    /// </summary>
    [Theory]
    [InlineData("0.4.0+dff5eee03627861a1e4a1a5f0c9d2b3e4f5a6b7", "Winora 0.4.0")]
    [InlineData("0.4.0-beta.1+abc123", "Winora 0.4.0-beta.1")]
    [InlineData("  0.4.0  ", "Winora 0.4.0")]
    public void The_commit_it_was_built_from_is_not_part_of_the_version(string version, string expected)
    {
        var vm = Build(version);
        vm.Load();

        Assert.Equal(expected, vm.VersionLabel);
    }

    /// <summary>
    /// Blank is the answer when the assembly carries no version, which is what an unbuilt or
    /// hand-assembled copy looks like.
    /// </summary>
    /// <remarks>
    /// An empty line beats the word "unknown": a version number is a fact or it is absent, and a
    /// pane that states its own ignorance is worse than a pane that says nothing.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unreadable_version_leaves_the_line_off(string version)
    {
        var vm = Build(version);
        vm.Load();

        Assert.Equal(string.Empty, vm.VersionLabel);
    }

    [Fact]
    public void The_community_link_is_named_for_the_person_who_cannot_see_the_mark()
    {
        var vm = Build("0.3.8.0");
        vm.Load();

        Assert.Equal("Shell_CommunityAction", vm.CommunityTooltip);
    }

    /// <summary>
    /// A literal, never anything read from disk or the registry, so nothing a person installed can
    /// point this link somewhere else.
    /// </summary>
    [Fact]
    public void The_community_address_is_the_project_discord()
    {
        Assert.StartsWith("https://discord.gg/", ShellViewModel.CommunityUrl, StringComparison.Ordinal);
    }
}
