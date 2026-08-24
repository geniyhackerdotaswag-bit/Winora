using Winora.Core.Profile;
using Xunit;

namespace Winora.Core.Tests.Profile;

/// <summary>
/// Storing a password without storing the password.
/// </summary>
/// <remarks>
/// Nothing here invents cryptography. The point of these tests is the handling around it: that a
/// fresh salt is drawn every time, that the digest is checkable, and that a wrong password is
/// refused — including the shapes of wrong that come from an edited file rather than from a person.
/// </remarks>
public sealed class PasswordHashTests
{
    [Fact]
    public void The_right_password_is_accepted()
    {
        var digest = PasswordHash.Create("Password1!");

        Assert.True(PasswordHash.Verify("Password1!", digest));
    }

    [Theory]
    [InlineData("password1!")]
    [InlineData("Password1")]
    [InlineData("Password1! ")]
    [InlineData("")]
    public void A_wrong_password_is_refused(string attempt)
    {
        var digest = PasswordHash.Create("Password1!");

        Assert.False(PasswordHash.Verify(attempt, digest));
    }

    /// <summary>
    /// Two people who choose the same password must not end up with the same stored digest.
    /// </summary>
    /// <remarks>
    /// That is what the salt is for, and it is the one property that a hand-rolled implementation
    /// most often gets wrong by reusing a constant.
    /// </remarks>
    [Fact]
    public void The_same_password_twice_gives_different_digests()
    {
        var first = PasswordHash.Create("Password1!");
        var second = PasswordHash.Create("Password1!");

        Assert.NotEqual(first.Salt, second.Salt);
        Assert.NotEqual(first.Hash, second.Hash);
        Assert.True(PasswordHash.Verify("Password1!", first));
        Assert.True(PasswordHash.Verify("Password1!", second));
    }

    [Fact]
    public void The_digest_records_how_it_was_made()
    {
        var digest = PasswordHash.Create("Password1!");

        Assert.Equal(PasswordHash.DefaultIterations, digest.Iterations);
        Assert.NotEmpty(digest.Salt);
        Assert.NotEmpty(digest.Hash);
    }

    /// <summary>
    /// A digest that has been edited by hand refuses everything rather than throwing.
    /// </summary>
    /// <remarks>
    /// profile.json is a plain file a person can open. Whatever they do to it, Verify has to answer
    /// "no" — a thrown exception here would come out of a startup path and take the window with it.
    /// </remarks>
    [Theory]
    [InlineData("", "c2FsdA==", 210_000)]
    [InlineData("aGFzaA==", "", 210_000)]
    [InlineData("not base64!", "c2FsdA==", 210_000)]
    [InlineData("aGFzaA==", "not base64!", 210_000)]
    [InlineData("aGFzaA==", "c2FsdA==", 0)]
    [InlineData("aGFzaA==", "c2FsdA==", -1)]
    public void A_broken_digest_refuses_everything(string hash, string salt, int iterations)
    {
        var digest = new PasswordDigest(hash, salt, iterations);

        Assert.False(PasswordHash.Verify("Password1!", digest));
    }

    [Fact]
    public void A_null_digest_refuses_everything()
    {
        Assert.False(PasswordHash.Verify("Password1!", null));
    }
}
