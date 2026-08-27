namespace Winora.Core.Profile;

/// <summary>Which of the two pictures a file is being offered for.</summary>
public enum ProfilePictureKind
{
    /// <summary>The round mark beside the name. Cropped square from the centre.</summary>
    Avatar,

    /// <summary>The picture behind the card's contents.</summary>
    CardBackground,
}

/// <summary>What the leading bytes say a file actually is.</summary>
/// <remarks>
/// Deliberately an allow-list of three. Anything else — an SVG, a PDF, an archive somebody renamed
/// to .png — is <see cref="Unknown"/>, which is the only safe default for a value derived from a
/// file the program did not write.
/// </remarks>
public enum PictureFormat
{
    /// <summary>None of the three. The file is refused.</summary>
    Unknown,

    Png,

    Jpeg,

    WebP,
}

/// <summary>Why a candidate picture was accepted or turned away.</summary>
/// <remarks>
/// Each refusal is its own value because each one has its own sentence on screen. A single
/// "неверный файл" would leave the person guessing which of four rules they broke.
/// </remarks>
public enum PictureVerdict
{
    /// <summary>Accepted.</summary>
    Ok,

    /// <summary>Not PNG, JPEG or WebP, whatever the extension claimed.</summary>
    UnsupportedFormat,

    /// <summary>Over the byte limit, or over the pixel limit.</summary>
    TooLarge,

    /// <summary>Fewer pixels than the place it is going needs.</summary>
    TooSmall,

    /// <summary>
    /// No longer returned by anything.
    /// </summary>
    /// <remarks>
    /// The card crops to its own shape, so proportions were never a reason to refuse a picture.
    /// The value stays so that a profile written by an older build, carrying this verdict, still
    /// reads back as something the program understands rather than as a number with no name.
    /// </remarks>
    WrongShape,

    /// <summary>Gone, locked, or a recognised header whose dimensions could not be read.</summary>
    Unreadable,

    /// <summary>
    /// Read and accepted, but not kept.
    /// </summary>
    /// <remarks>
    /// Never returned by <see cref="ProfilePictureRules.Check"/>, which decides nothing about
    /// storage. It exists so the layer that does the copying and the writing has a way to say "your
    /// file was fine, mine went wrong" instead of blaming the picture.
    /// </remarks>
    NotStored,
}

/// <param name="Format">Unknown when the bytes match none of the three accepted kinds.</param>
/// <param name="Width">Zero when the format was recognised but its dimensions were not.</param>
/// <param name="Height">Zero on the same terms as <paramref name="Width"/>.</param>
public sealed record PictureHeader(PictureFormat Format, int Width, int Height);

/// <summary>
/// What may become somebody's avatar or card background.
/// </summary>
/// <remarks>
/// <para>
/// The content decides, never the extension. This is the same rule <c>AppDownloadCheck</c> applies
/// to a downloaded release — read the front of the file and refuse anything that does not begin the
/// way the format says it must — and it is here for a stronger reason. A downloaded .exe that is
/// really an HTML error page merely fails to run; a "picture" that is really an SVG is a document
/// with a script element in it, handed to a renderer.
/// </para>
/// <para>
/// SVG is therefore not merely absent from the list, it is the reason there is a list. Nothing here
/// decodes anything: the dimensions come out of the header fields the three formats publish, which
/// is enough to judge size and shape without ever handing the bytes to an image decoder.
/// </para>
/// <para>
/// Pure, and over a span, so the whole of it is testable without a file system — the same shape as
/// <see cref="PasswordStrengthRules"/>.
/// </para>
/// </remarks>
public static class ProfilePictureRules
{
    /// <summary>The most a picture may weigh.</summary>
    public const long MaxBytes = 4L * 1024 * 1024;

    /// <summary>
    /// The most a picture may measure on either side.
    /// </summary>
    /// <remarks>
    /// A limit on bytes is not a limit on pixels: four megabytes of PNG can be thirty thousand
    /// pixels square if most of it is one flat colour, and decoding that wants gigabytes of memory
    /// for a picture drawn at ninety-six points. Refused as "too large", which is what it is.
    /// </remarks>
    public const int MaxSide = 8000;

    /// <summary>An avatar is cropped square and drawn in a circle, so both sides must reach this.</summary>
    public const int AvatarMinSide = 128;

    /// <summary>A card background is stretched across the card, so its width is what matters.</summary>
    /// <summary>
    /// The smallest side a background may have.
    /// </summary>
    /// <remarks>
    /// Was 800 and applied to the width alone, which turned away ordinary pictures — a 736-pixel
    /// wallpaper, for one. The card crops to its own shape and then lays an 86% sheet of the
    /// scheme's own colour over the result, so sharpness barely survives to be judged: the floor
    /// exists only to keep an icon from being blown up into a smear, not to enforce a standard.
    /// </remarks>
    public const int BackgroundMinWidth = 240;

    /// <summary>The longest a stored file name may be.</summary>
    private const int MaxStoredNameLength = 64;

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ReadOnlySpan<byte> PngHeaderChunk => "IHDR"u8;

    private static ReadOnlySpan<byte> JpegSignature => [0xFF, 0xD8, 0xFF];

    private static ReadOnlySpan<byte> RiffSignature => "RIFF"u8;

    private static ReadOnlySpan<byte> WebPSignature => "WEBP"u8;

    /// <summary>
    /// What the file is, and how big, from its header alone.
    /// </summary>
    /// <param name="bytes">
    /// The file's contents, or as much of the front of it as is available. PNG and WebP need about
    /// thirty bytes; JPEG keeps its dimensions in a segment that can sit further in, so it is walked
    /// segment by segment through whatever is given.
    /// </param>
    public static PictureHeader Inspect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(PngSignature))
        {
            return ReadPng(bytes);
        }

        if (bytes.StartsWith(JpegSignature))
        {
            return ReadJpeg(bytes);
        }

        if (bytes.Length >= 12 &&
            bytes[..4].SequenceEqual(RiffSignature) &&
            bytes[8..12].SequenceEqual(WebPSignature))
        {
            return ReadWebP(bytes);
        }

        // Everything else, including SVG — which begins "<?xml" or "<svg" and would sail through any
        // check that trusted the extension. It is a document, and a document is not a picture.
        return new PictureHeader(PictureFormat.Unknown, 0, 0);
    }

    /// <summary>
    /// Whether this file may become the picture of the given kind.
    /// </summary>
    /// <param name="kind">Where the picture is going. The limits differ by place, not by format.</param>
    /// <param name="byteCount">
    /// The length of the whole file, which may be more than <paramref name="bytes"/> holds. Passed
    /// separately so an oversized file can be refused without ever being read into memory.
    /// </param>
    /// <param name="bytes">The front of the file, or all of it.</param>
    /// <remarks>
    /// Ordered by what each step costs, as <c>AppDownloadCheck</c> is: the length first because it
    /// is free, then the few bytes at the front, then the dimensions those bytes carry. Nothing here
    /// decodes an image.
    /// </remarks>
    public static PictureVerdict Check(ProfilePictureKind kind, long byteCount, ReadOnlySpan<byte> bytes)
    {
        if (byteCount > MaxBytes)
        {
            return PictureVerdict.TooLarge;
        }

        var header = Inspect(bytes);

        if (header.Format == PictureFormat.Unknown)
        {
            return PictureVerdict.UnsupportedFormat;
        }

        // The right magic number and an unreadable body: a truncated download, or a PNG whose IHDR
        // was edited. Not a format complaint — the format was recognised — so it says so separately.
        if (header.Width <= 0 || header.Height <= 0)
        {
            return PictureVerdict.Unreadable;
        }

        if (header.Width > MaxSide || header.Height > MaxSide)
        {
            return PictureVerdict.TooLarge;
        }

        return kind switch
        {
            ProfilePictureKind.Avatar =>
                Math.Min(header.Width, header.Height) < AvatarMinSide
                    ? PictureVerdict.TooSmall
                    : PictureVerdict.Ok,

            // Shape is not checked, and that is deliberate. The card paints the picture as a brush
            // set to UniformToFill inside its own rounded border, so whatever the proportions, the
            // middle of the picture fills the card and the rest is cropped — never squashed. A
            // square photograph and a wide strip both land correctly. Refusing one of them was a
            // rule protecting nothing: it turned away pictures the card would have drawn properly,
            // and the person had no way to tell, because what they saw was a refusal.
            ProfilePictureKind.CardBackground =>
                header.Width < BackgroundMinWidth || header.Height < BackgroundMinWidth
                    ? PictureVerdict.TooSmall
                    : PictureVerdict.Ok,

            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    /// <summary>The extension a stored copy of this format carries.</summary>
    public static string ExtensionFor(PictureFormat format) => format switch
    {
        PictureFormat.Png => "png",
        PictureFormat.Jpeg => "jpg",
        PictureFormat.WebP => "webp",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <summary>
    /// A name for the copy kept beside the profile.
    /// </summary>
    /// <remarks>
    /// Generated rather than taken from the source, because the source name is the one piece of the
    /// original that a person can put anything at all into — including a path. What is stored is
    /// therefore never anything the file itself said about where it lives.
    /// </remarks>
    public static string NewFileName(ProfilePictureKind kind, PictureFormat format)
    {
        var prefix = kind switch
        {
            ProfilePictureKind.Avatar => "avatar",
            ProfilePictureKind.CardBackground => "background",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        return $"{prefix}-{Guid.NewGuid():N}.{ExtensionFor(format)}";
    }

    /// <summary>
    /// Whether a name read back out of profile.json may be joined to the media folder.
    /// </summary>
    /// <remarks>
    /// profile.json is a plain text file in the person's own profile folder, so its contents are
    /// whatever the last thing to write it put there. A name of <c>..\..\Windows\System32\x.png</c>
    /// joined to a folder resolves somewhere else entirely, and an elevated program must never
    /// follow one. Only names of the shape this class generates are accepted; anything else reads
    /// as "no picture", which the card already knows how to draw.
    /// </remarks>
    public static bool IsStoredFileName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxStoredNameLength)
        {
            return false;
        }

        var dot = -1;

        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];

            if (character == '.')
            {
                // Exactly one, and never at either end — so no "..", no leading dot, no double
                // extension.
                if (dot >= 0 || index == 0 || index == name.Length - 1)
                {
                    return false;
                }

                dot = index;
                continue;
            }

            // Lower-case ASCII, digits and the one separator the generated names use. That refuses
            // every path separator, every drive colon and every character Windows reserves, without
            // having to enumerate them.
            if (!char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character) && character != '-')
            {
                return false;
            }
        }

        if (dot < 0)
        {
            return false;
        }

        var extension = name[(dot + 1)..];

        return extension is "png" or "jpg" or "webp";
    }

    /// <summary>
    /// PNG publishes its size in IHDR, which the specification requires to be the first chunk.
    /// </summary>
    /// <remarks>
    /// Layout: the eight signature bytes, then a four-byte chunk length, then the four-character
    /// chunk type, then the payload — width and height as big-endian 32-bit values.
    /// </remarks>
    private static PictureHeader ReadPng(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 24 || !bytes[12..16].SequenceEqual(PngHeaderChunk))
        {
            return new PictureHeader(PictureFormat.Png, 0, 0);
        }

        return new PictureHeader(
            PictureFormat.Png,
            BigEndian32(bytes[16..20]),
            BigEndian32(bytes[20..24]));
    }

    /// <summary>
    /// JPEG keeps its size in a start-of-frame segment, whose position is not fixed.
    /// </summary>
    /// <remarks>
    /// The file is a chain of segments, each 0xFF, a marker byte, and — for all but a handful — a
    /// big-endian length that counts itself. Walking the chain is the only way to reach the frame
    /// header, because everything before it is metadata of arbitrary size: an EXIF block with a
    /// thumbnail in it routinely pushes the frame tens of kilobytes in.
    /// </remarks>
    private static PictureHeader ReadJpeg(ReadOnlySpan<byte> bytes)
    {
        var index = 2;

        while (index + 3 < bytes.Length)
        {
            if (bytes[index] != 0xFF)
            {
                // Out of step with the segment chain. Whatever this is, its dimensions are not
                // being guessed at.
                break;
            }

            var marker = bytes[index + 1];

            // Any number of 0xFF bytes may pad in front of a marker.
            if (marker == 0xFF)
            {
                index++;
                continue;
            }

            // Restart markers and the two padding markers carry no length and no payload.
            if (marker == 0x01 || marker is >= 0xD0 and <= 0xD9)
            {
                index += 2;
                continue;
            }

            var length = (bytes[index + 2] << 8) | bytes[index + 3];

            if (length < 2)
            {
                break;
            }

            // SOF0 through SOF15, less the three markers that share the range without being frame
            // headers: DHT (0xC4), JPG (0xC8) and DAC (0xCC).
            if (marker is >= 0xC0 and <= 0xCF && marker is not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (index + 8 >= bytes.Length)
                {
                    break;
                }

                // One byte of sample precision, then height, then width — height first, which is
                // the opposite of every other format here.
                return new PictureHeader(
                    PictureFormat.Jpeg,
                    (bytes[index + 7] << 8) | bytes[index + 8],
                    (bytes[index + 5] << 8) | bytes[index + 6]);
            }

            index += 2 + length;
        }

        return new PictureHeader(PictureFormat.Jpeg, 0, 0);
    }

    /// <summary>
    /// WebP has three encodings and states its size differently in each.
    /// </summary>
    /// <remarks>
    /// After the RIFF header comes a four-character chunk name: "VP8 " for lossy, "VP8L" for
    /// lossless, "VP8X" for the extended form that carries animation or transparency. All three are
    /// little-endian, and the two newer ones store each dimension one less than it is.
    /// </remarks>
    private static PictureHeader ReadWebP(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 16)
        {
            return new PictureHeader(PictureFormat.WebP, 0, 0);
        }

        var chunk = bytes[12..16];

        if (chunk.SequenceEqual("VP8 "u8) && bytes.Length >= 30)
        {
            // Three bytes of frame tag, then the start code, then the two dimensions in fourteen
            // bits each — the top two bits of each pair are a scaling hint, not part of the size.
            if (bytes[23] == 0x9D && bytes[24] == 0x01 && bytes[25] == 0x2A)
            {
                return new PictureHeader(
                    PictureFormat.WebP,
                    ((bytes[27] << 8) | bytes[26]) & 0x3FFF,
                    ((bytes[29] << 8) | bytes[28]) & 0x3FFF);
            }
        }
        else if (chunk.SequenceEqual("VP8L"u8) && bytes.Length >= 25 && bytes[20] == 0x2F)
        {
            // Fourteen bits of width then fourteen of height, packed into one little-endian word
            // and each stored one less than it is.
            var packed = (uint)(bytes[21] | (bytes[22] << 8) | (bytes[23] << 16) | (bytes[24] << 24));

            return new PictureHeader(
                PictureFormat.WebP,
                (int)(packed & 0x3FFF) + 1,
                (int)((packed >> 14) & 0x3FFF) + 1);
        }
        else if (chunk.SequenceEqual("VP8X"u8) && bytes.Length >= 30)
        {
            // A flags byte and three reserved, then the canvas size as two little-endian 24-bit
            // values, each one less than it is.
            return new PictureHeader(
                PictureFormat.WebP,
                (bytes[24] | (bytes[25] << 8) | (bytes[26] << 16)) + 1,
                (bytes[27] | (bytes[28] << 8) | (bytes[29] << 16)) + 1);
        }

        return new PictureHeader(PictureFormat.WebP, 0, 0);
    }

    private static int BigEndian32(ReadOnlySpan<byte> four)
    {
        var value = ((long)four[0] << 24) | ((long)four[1] << 16) | ((long)four[2] << 8) | four[3];

        // A PNG may legally declare a width up to 2^31-1, which does not fit an int as a positive
        // number. Anything that large is refused by the pixel limit anyway, so it is clamped here
        // rather than allowed to come out negative and read as "unreadable".
        return value > int.MaxValue ? int.MaxValue : (int)value;
    }
}
