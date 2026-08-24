using Winora.App.Views;
using Xunit;

namespace Winora.App.Tests.Views;

/// <summary>What the welcome dialog decides to save, kept apart from the ContentDialog itself.</summary>
public sealed class WelcomeOutcomeTests
{
    [Fact]
    public void Start_with_a_valid_name_saves_it()
    {
        var outcome = WelcomeOutcome.Resolve(
            startPressed: true,
            typedName: "Alex",
            typedEmail: string.Empty,
            suggestedName: "ignored-because-start-was-pressed");

        Assert.Equal(("Alex", string.Empty), outcome);
    }

    [Fact]
    public void Start_with_a_blank_name_saves_nothing()
    {
        var outcome = WelcomeOutcome.Resolve(
            startPressed: true,
            typedName: "   ",
            typedEmail: string.Empty,
            suggestedName: "Alex");

        Assert.Null(outcome);
    }

    [Fact]
    public void Skip_saves_the_suggested_name()
    {
        var outcome = WelcomeOutcome.Resolve(
            startPressed: false,
            typedName: "ignored-because-skip-was-pressed",
            typedEmail: "ignored@example.com",
            suggestedName: "Alex");

        Assert.Equal(("Alex", string.Empty), outcome);
    }

    [Fact]
    public void Skip_with_a_blank_suggested_name_saves_nothing()
    {
        var outcome = WelcomeOutcome.Resolve(
            startPressed: false,
            typedName: "ignored-because-skip-was-pressed",
            typedEmail: "ignored@example.com",
            suggestedName: string.Empty);

        Assert.Null(outcome);
    }
}
