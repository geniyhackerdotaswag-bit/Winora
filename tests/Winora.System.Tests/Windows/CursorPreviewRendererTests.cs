using Winora.System.Windows;
using Xunit;

namespace Winora.System.Tests.Platform;

/// <summary>
/// Exists because the first attempt at a preview bound the file path straight to an Image and
/// crashed the app: WinUI decodes neither .cur nor .ani. These tests run the real drawing path
/// against real cursor files, so "it renders" is measured rather than assumed a second time.
/// </summary>
public sealed class CursorPreviewRendererTests
{
    private static string? FindShippedCursor(string extension)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Cursors");

        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*" + extension).FirstOrDefault()
            : null;
    }

    [Theory]
    [InlineData(".cur")]
    [InlineData(".ani")]
    public void A_shipped_cursor_renders_to_visible_pixels(string extension)
    {
        // Windows always ships both kinds; asserting that keeps the test honest rather than
        // silently passing on a machine where it found nothing to render.
        var file = FindShippedCursor(extension);
        Assert.NotNull(file);

        var preview = new CursorPreviewRenderer().TryRender(file!, 32);

        Assert.NotNull(preview);
        Assert.Equal(32, preview!.Width);
        Assert.Equal(32, preview.Height);
        Assert.Equal(32 * 32 * 4, preview.Bgra.Length);

        // At least one pixel must be opaque, otherwise the card would show an empty square and the
        // preview would be worse than none.
        Assert.Contains(preview.Bgra.Where(static (_, index) => index % 4 == 3), static alpha => alpha != 0);
    }

    [Fact]
    public void A_missing_file_yields_no_preview_rather_than_throwing()
    {
        var preview = new CursorPreviewRenderer().TryRender(
            Path.Combine(Path.GetTempPath(), "winora-does-not-exist.ani"),
            32);

        Assert.Null(preview);
    }

    [Fact]
    public void A_file_that_is_not_a_cursor_yields_no_preview()
    {
        var file = Path.Combine(Path.GetTempPath(), $"winora-not-a-cursor-{Guid.NewGuid():N}.cur");
        File.WriteAllText(file, "this is not a cursor");
        try
        {
            Assert.Null(new CursorPreviewRenderer().TryRender(file, 32));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-8)]
    public void A_nonsense_size_is_refused(int size)
    {
        var file = FindShippedCursor(".cur");
        Assert.NotNull(file);

        Assert.Null(new CursorPreviewRenderer().TryRender(file!, size));
    }
}
