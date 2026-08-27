using Winora.Core.Profile;
using Xunit;

namespace Winora.Core.Tests.Profile;

/// <summary>
/// What may become somebody's picture, decided from the bytes.
/// </summary>
/// <remarks>
/// The headers below are built by hand rather than loaded from sample files. A test that needs
/// six binary fixtures on disk is a test nobody can read, and the point of every one of these is
/// which byte at which offset carries the answer.
/// </remarks>
public sealed class ProfilePictureRulesTests
{
    private static PictureVerdict Checked(int width, int height, ProfilePictureKind kind)
    {
        var bytes = Png(width, height);
        return ProfilePictureRules.Check(kind, bytes.Length, bytes);
    }

    private static byte[] Png(int width, int height)
    {
        var bytes = new byte[24];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);

        // The IHDR chunk length, which is always thirteen, then the chunk type.
        bytes[11] = 13;
        "IHDR"u8.CopyTo(bytes.AsSpan(12));

        BigEndian(bytes.AsSpan(16), width);
        BigEndian(bytes.AsSpan(20), height);

        return bytes;
    }

    private static void BigEndian(Span<byte> four, int value)
    {
        four[0] = (byte)(value >> 24);
        four[1] = (byte)(value >> 16);
        four[2] = (byte)(value >> 8);
        four[3] = (byte)value;
    }

    /// <param name="leadingBytes">
    /// How much metadata sits in front of the frame header. A real photograph has thousands.
    /// </param>
    private static byte[] Jpeg(int width, int height, int leadingBytes = 0)
    {
        var bytes = new List<byte> { 0xFF, 0xD8, 0xFF };

        if (leadingBytes > 0)
        {
            // An APP1 segment — the shape EXIF arrives in — of the requested size, so the frame
            // header is not where a reader that only looked at the front would find it.
            var length = leadingBytes + 2;
            bytes.Add(0xE1);
            bytes.Add((byte)(length >> 8));
            bytes.Add((byte)length);
            bytes.AddRange(new byte[leadingBytes]);
            bytes.Add(0xFF);
        }

        // SOF0: marker, a length of eleven, one byte of precision, then height, then width.
        bytes.Add(0xC0);
        bytes.Add(0x00);
        bytes.Add(0x11);
        bytes.Add(0x08);
        bytes.Add((byte)(height >> 8));
        bytes.Add((byte)height);
        bytes.Add((byte)(width >> 8));
        bytes.Add((byte)width);

        return [.. bytes];
    }

    private static byte[] WebPLossy(int width, int height)
    {
        var bytes = new byte[30];
        "RIFF"u8.CopyTo(bytes.AsSpan(0));
        "WEBP"u8.CopyTo(bytes.AsSpan(8));
        "VP8 "u8.CopyTo(bytes.AsSpan(12));

        // Three bytes of frame tag, then the start code the format requires.
        bytes[23] = 0x9D;
        bytes[24] = 0x01;
        bytes[25] = 0x2A;

        bytes[26] = (byte)width;
        bytes[27] = (byte)(width >> 8);
        bytes[28] = (byte)height;
        bytes[29] = (byte)(height >> 8);

        return bytes;
    }

    private static byte[] WebPLossless(int width, int height)
    {
        var bytes = new byte[25];
        "RIFF"u8.CopyTo(bytes.AsSpan(0));
        "WEBP"u8.CopyTo(bytes.AsSpan(8));
        "VP8L"u8.CopyTo(bytes.AsSpan(12));
        bytes[20] = 0x2F;

        // Fourteen bits each, both stored one less than they are.
        var packed = (uint)(width - 1) | ((uint)(height - 1) << 14);
        bytes[21] = (byte)packed;
        bytes[22] = (byte)(packed >> 8);
        bytes[23] = (byte)(packed >> 16);
        bytes[24] = (byte)(packed >> 24);

        return bytes;
    }

    private static byte[] WebPExtended(int width, int height)
    {
        var bytes = new byte[30];
        "RIFF"u8.CopyTo(bytes.AsSpan(0));
        "WEBP"u8.CopyTo(bytes.AsSpan(8));
        "VP8X"u8.CopyTo(bytes.AsSpan(12));

        var canvasWidth = width - 1;
        var canvasHeight = height - 1;

        bytes[24] = (byte)canvasWidth;
        bytes[25] = (byte)(canvasWidth >> 8);
        bytes[26] = (byte)(canvasWidth >> 16);
        bytes[27] = (byte)canvasHeight;
        bytes[28] = (byte)(canvasHeight >> 8);
        bytes[29] = (byte)(canvasHeight >> 16);

        return bytes;
    }

    [Fact]
    public void A_png_states_its_size_in_its_first_chunk()
    {
        var header = ProfilePictureRules.Inspect(Png(640, 480));

        Assert.Equal(PictureFormat.Png, header.Format);
        Assert.Equal(640, header.Width);
        Assert.Equal(480, header.Height);
    }

    [Fact]
    public void A_jpeg_states_its_size_in_a_frame_header()
    {
        var header = ProfilePictureRules.Inspect(Jpeg(1920, 1080));

        Assert.Equal(PictureFormat.Jpeg, header.Format);
        Assert.Equal(1920, header.Width);
        Assert.Equal(1080, header.Height);
    }

    /// <summary>
    /// A photograph's frame header sits behind its metadata, which is routinely tens of kilobytes.
    /// </summary>
    /// <remarks>
    /// A reader that only looked at the first thirty bytes would find nothing and report every
    /// photograph ever taken as unreadable.
    /// </remarks>
    [Fact]
    public void A_jpeg_is_measured_from_behind_its_metadata()
    {
        var header = ProfilePictureRules.Inspect(Jpeg(4000, 3000, leadingBytes: 40_000));

        Assert.Equal(PictureFormat.Jpeg, header.Format);
        Assert.Equal(4000, header.Width);
        Assert.Equal(3000, header.Height);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void All_three_webp_encodings_state_their_size(int encoding)
    {
        var bytes = encoding switch
        {
            0 => WebPLossy(1200, 400),
            1 => WebPLossless(1200, 400),
            _ => WebPExtended(1200, 400),
        };

        var header = ProfilePictureRules.Inspect(bytes);

        Assert.Equal(PictureFormat.WebP, header.Format);
        Assert.Equal(1200, header.Width);
        Assert.Equal(400, header.Height);
    }

    /// <summary>
    /// SVG is refused, and it is the reason the list is an allow-list.
    /// </summary>
    /// <remarks>
    /// It is a picture in the sense that it draws one, and a document in the sense that matters
    /// here: it can carry a script element, and it would be handed to a renderer. A check that
    /// trusted the .svg extension being absent would have let a renamed one straight through, which
    /// is why nothing here looks at the name.
    /// </remarks>
    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")]
    [InlineData("<?xml version=\"1.0\"?><svg width=\"800\" height=\"200\"></svg>")]
    public void An_svg_is_not_a_picture(string markup)
    {
        var bytes = global::System.Text.Encoding.UTF8.GetBytes(markup);

        Assert.Equal(PictureFormat.Unknown, ProfilePictureRules.Inspect(bytes).Format);

        Assert.Equal(
            PictureVerdict.UnsupportedFormat,
            ProfilePictureRules.Check(ProfilePictureKind.Avatar, bytes.Length, bytes));
    }

    /// <summary>The extension is never consulted, so a renamed file is judged on what it is.</summary>
    [Theory]
    [InlineData("GIF89a")]
    [InlineData("%PDF-1.7")]
    [InlineData("PK\u0003\u0004")]
    [InlineData("MZ")]
    [InlineData("")]
    public void Anything_that_is_not_one_of_the_three_is_refused(string content)
    {
        var bytes = global::System.Text.Encoding.ASCII.GetBytes(content);

        Assert.Equal(
            PictureVerdict.UnsupportedFormat,
            ProfilePictureRules.Check(ProfilePictureKind.Avatar, bytes.Length, bytes));
    }

    /// <summary>The right magic number and a body that says nothing is not a format complaint.</summary>
    [Fact]
    public void A_truncated_png_is_unreadable_rather_than_unsupported()
    {
        var bytes = Png(128, 128)[..12];

        Assert.Equal(
            PictureVerdict.Unreadable,
            ProfilePictureRules.Check(ProfilePictureKind.Avatar, bytes.Length, bytes));
    }

    [Fact]
    public void A_file_over_four_megabytes_is_turned_away_without_being_read()
    {
        Assert.Equal(
            PictureVerdict.TooLarge,
            ProfilePictureRules.Check(
                ProfilePictureKind.Avatar,
                ProfilePictureRules.MaxBytes + 1,
                Png(512, 512)));
    }

    /// <summary>
    /// A limit on bytes is not a limit on pixels.
    /// </summary>
    /// <remarks>
    /// Four megabytes of PNG is thirty thousand pixels square if most of it is one flat colour, and
    /// decoding that wants gigabytes for a picture drawn at ninety-six points.
    /// </remarks>
    [Fact]
    public void A_picture_beyond_the_pixel_limit_is_too_large_however_little_it_weighs()
    {
        var bytes = Png(30_000, 30_000);

        Assert.Equal(
            PictureVerdict.TooLarge,
            ProfilePictureRules.Check(ProfilePictureKind.Avatar, bytes.Length, bytes));
    }

    [Theory]
    [InlineData(128, 128, PictureVerdict.Ok)]
    [InlineData(512, 512, PictureVerdict.Ok)]
    [InlineData(4000, 128, PictureVerdict.Ok)]
    [InlineData(127, 512, PictureVerdict.TooSmall)]
    [InlineData(512, 127, PictureVerdict.TooSmall)]
    [InlineData(64, 64, PictureVerdict.TooSmall)]
    public void An_avatar_needs_both_of_its_sides(int width, int height, PictureVerdict expected)
    {
        var bytes = Png(width, height);

        Assert.Equal(
            expected,
            ProfilePictureRules.Check(ProfilePictureKind.Avatar, bytes.Length, bytes));
    }

    /// <summary>An avatar is cropped square from the centre, so its proportions are never wrong.</summary>
    [Fact]
    public void An_avatar_is_never_the_wrong_shape()
    {
        var bytes = Png(200, 3000);

        Assert.Equal(
            PictureVerdict.Ok,
            ProfilePictureRules.Check(ProfilePictureKind.Avatar, bytes.Length, bytes));
    }

    [Theory]
    [InlineData(1600, 400, PictureVerdict.Ok)]
    [InlineData(800, 400, PictureVerdict.Ok)]
    [InlineData(2000, 400, PictureVerdict.Ok)]
    [InlineData(799, 200, PictureVerdict.TooSmall)]
    [InlineData(1600, 900, PictureVerdict.Ok)]
    [InlineData(1000, 1000, PictureVerdict.Ok)]
    [InlineData(3000, 400, PictureVerdict.Ok)]
    [InlineData(1000, 3000, PictureVerdict.Ok)]
    public void A_card_background_needs_width_and_proportion(int width, int height, PictureVerdict expected)
    {
        var bytes = Png(width, height);

        Assert.Equal(
            expected,
            ProfilePictureRules.Check(ProfilePictureKind.CardBackground, bytes.Length, bytes));
    }

    /// <summary>
    /// A strip that is both too narrow and correctly proportioned is called too small.
    /// </summary>
    /// <remarks>
    /// Told it was the wrong shape, somebody would go looking for a rounder picture, which is the
    /// opposite of what would help.
    /// </remarks>
    [Fact]
    public void A_small_but_well_proportioned_background_is_told_it_is_small()
    {
        var bytes = Png(400, 100);

        Assert.Equal(
            PictureVerdict.TooSmall,
            ProfilePictureRules.Check(ProfilePictureKind.CardBackground, bytes.Length, bytes));
    }

    /// <summary>
    /// A portrait photograph is accepted, because the card crops it.
    /// </summary>
    /// <remarks>
    /// This test used to assert the opposite. The shape rule it guarded turned away pictures the
    /// card draws correctly: the background is a brush set to UniformToFill inside a rounded
    /// border, so a tall photograph shows its middle and the rest is cropped — never squashed.
    /// </remarks>
    [Fact]
    public void A_portrait_photograph_is_accepted_and_cropped_by_the_card()
    {
        var bytes = Jpeg(3000, 4000);

        Assert.Equal(
            PictureVerdict.Ok,
            ProfilePictureRules.Check(ProfilePictureKind.CardBackground, bytes.Length, bytes));
    }

    [Theory]
    [InlineData(ProfilePictureKind.Avatar, PictureFormat.Png)]
    [InlineData(ProfilePictureKind.Avatar, PictureFormat.Jpeg)]
    [InlineData(ProfilePictureKind.Avatar, PictureFormat.WebP)]
    [InlineData(ProfilePictureKind.CardBackground, PictureFormat.Png)]
    [InlineData(ProfilePictureKind.CardBackground, PictureFormat.Jpeg)]
    [InlineData(ProfilePictureKind.CardBackground, PictureFormat.WebP)]
    public void A_generated_name_is_one_the_store_will_accept_back(
        ProfilePictureKind kind,
        PictureFormat format)
    {
        var name = ProfilePictureRules.NewFileName(kind, format);

        Assert.True(ProfilePictureRules.IsStoredFileName(name), name);
    }

    [Fact]
    public void Two_generated_names_never_collide()
    {
        var names = Enumerable
            .Range(0, 256)
            .Select(_ => ProfilePictureRules.NewFileName(ProfilePictureKind.Avatar, PictureFormat.Png))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(256, names.Count);
    }

    /// <summary>
    /// A name from profile.json is joined to a folder, so it must not be able to leave it.
    /// </summary>
    /// <remarks>
    /// profile.json is plain text in the person's own folder, and Winora runs elevated. A name of
    /// <c>..\..\Windows\System32\x.png</c> joined to the media folder resolves somewhere else
    /// entirely; there is nothing in this program that should ever follow one.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData("../avatar.png")]
    [InlineData("..\\..\\Windows\\System32\\config\\SAM.png")]
    [InlineData("C:\\Windows\\notepad.png")]
    [InlineData("\\\\server\\share\\x.png")]
    [InlineData("avatar.png\\..\\x.png")]
    [InlineData(".avatar.png")]
    [InlineData("avatar.png.")]
    [InlineData("avatar.tar.png")]
    [InlineData("avatar.svg")]
    [InlineData("avatar.exe")]
    [InlineData("avatar")]
    [InlineData("AVATAR.PNG")]
    [InlineData("avatar file.png")]
    [InlineData("avatar:stream.png")]
    public void A_name_that_is_not_one_this_program_wrote_is_refused(string? name)
    {
        Assert.False(ProfilePictureRules.IsStoredFileName(name));
    }

    [Theory]
    [InlineData("avatar-0123456789abcdef0123456789abcdef.png")]
    [InlineData("background-0123456789abcdef0123456789abcdef.jpg")]
    [InlineData("background-0123456789abcdef0123456789abcdef.webp")]
    public void A_name_of_the_generated_shape_is_accepted(string name)
    {
        Assert.True(ProfilePictureRules.IsStoredFileName(name));
    }

    [Fact]
    public void An_unreasonably_long_name_is_refused()
    {
        Assert.False(ProfilePictureRules.IsStoredFileName(new string('a', 200) + ".png"));
    }

    /// <summary>
    /// The pictures that sent this rule to be rewritten.
    /// </summary>
    /// <remarks>
    /// Both were refused by the old rule and both are drawn correctly by the card: 736x414 for
    /// being neither wide enough nor the right shape, 736x271 for the width alone. The card crops
    /// to its own shape and lays an 86% sheet over the result, so neither refusal protected
    /// anything a person could see.
    /// </remarks>
    [Theory]
    [InlineData(736, 414)]
    [InlineData(736, 271)]
    public void An_ordinary_wallpaper_is_accepted_as_a_background(int width, int height)
    {
        Assert.Equal(
            PictureVerdict.Ok,
            Checked(width, height, ProfilePictureKind.CardBackground));
    }

    /// <summary>An icon blown up to fill a card is still refused.</summary>
    [Theory]
    [InlineData(64, 64)]
    [InlineData(1200, 120)]
    public void Something_too_small_to_be_a_picture_is_still_refused(int width, int height)
    {
        Assert.Equal(
            PictureVerdict.TooSmall,
            Checked(width, height, ProfilePictureKind.CardBackground));
    }
}
