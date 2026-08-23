using System.Security.Cryptography;
using Winora.System.Updates;
using Xunit;

namespace Winora.System.Tests.Updates;

/// <summary>
/// Deciding whether what arrived is safe to put in place of the running program.
/// </summary>
/// <remarks>
/// Three checks, and each one exists because of a different way this goes wrong. The size catches a
/// connection that dropped and a disk that filled. The hash catches bytes that changed on the way.
/// The signature catches the case where the download succeeded perfectly and delivered a web page —
/// a proxy sign-in, a rate-limit notice, an error page — which is the most common of the three and
/// the only one the other two can miss.
/// </remarks>
public sealed class AppDownloadCheckTests : IDisposable
{
    private readonly string _folder;

    public AppDownloadCheckTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "winora-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception)
        {
            // A leftover temp folder is not worth failing a passing test over.
        }
    }

    /// <summary>Bytes that start like a Windows executable, because that is what is being checked.</summary>
    private static byte[] Executable(int length)
    {
        var bytes = new byte[length];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        for (var index = 2; index < length; index++)
        {
            bytes[index] = (byte)(index % 251);
        }

        return bytes;
    }

    private string Write(byte[] content)
    {
        var path = Path.Combine(_folder, "Winora.exe.new");
        File.WriteAllBytes(path, content);
        return path;
    }

    private static string Hash(byte[] content) => Convert.ToHexString(SHA256.HashData(content));

    [Fact]
    public void A_good_download_passes()
    {
        var content = Executable(4096);
        var path = Write(content);

        Assert.Equal(DownloadVerdict.Ok, AppDownloadCheck.Verify(path, content.Length, Hash(content)));
    }

    /// <summary>The hash may be written the way sha256sum writes it: lower case, then the name.</summary>
    [Fact]
    public void The_hash_is_read_however_the_tool_wrote_it()
    {
        var content = Executable(4096);
        var path = Write(content);
        var written = Hash(content).ToLowerInvariant() + "  Winora.exe\n";

        Assert.Equal(DownloadVerdict.Ok, AppDownloadCheck.Verify(path, content.Length, written));
    }

    [Fact]
    public void A_short_file_is_caught_by_its_size()
    {
        var content = Executable(4096);
        var path = Write(content);

        Assert.Equal(DownloadVerdict.WrongSize, AppDownloadCheck.Verify(path, 8192, Hash(content)));
    }

    [Fact]
    public void Changed_bytes_are_caught_by_the_hash()
    {
        var content = Executable(4096);
        var path = Write(content);
        var other = Executable(4096);
        other[100] ^= 0xFF;

        Assert.Equal(DownloadVerdict.WrongHash, AppDownloadCheck.Verify(path, content.Length, Hash(other)));
    }

    /// <summary>
    /// The case the other two checks cannot see: a complete, uncorrupted file that is not a program.
    /// </summary>
    [Fact]
    public void A_web_page_instead_of_a_program_is_caught_by_its_first_two_bytes()
    {
        var page = "<!doctype html><title>Sign in</title>"u8.ToArray();
        var path = Write(page);

        Assert.Equal(
            DownloadVerdict.NotAnExecutable,
            AppDownloadCheck.Verify(path, page.Length, Hash(page)));
    }

    [Fact]
    public void An_empty_file_is_not_an_executable()
    {
        var path = Write([]);

        Assert.Equal(DownloadVerdict.NotAnExecutable, AppDownloadCheck.Verify(path, 0, Hash([])));
    }

    [Fact]
    public void A_file_that_is_not_there_is_unreadable()
    {
        Assert.Equal(
            DownloadVerdict.Unreadable,
            AppDownloadCheck.Verify(Path.Combine(_folder, "absent.exe"), 1, new string('0', 64)));
    }

    /// <summary>A checksum file with nothing usable in it fails rather than passing by accident.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-hash")]
    public void An_unusable_checksum_never_passes(string checksum)
    {
        var content = Executable(4096);
        var path = Write(content);

        Assert.Equal(DownloadVerdict.WrongHash, AppDownloadCheck.Verify(path, content.Length, checksum));
    }
}
