using System.IO.Compression;
using System.Text;
using AlMotaheda.Modules.Archive.Data;

namespace AlMotaheda.Modules.Archive.Files;

/// <summary>One file type the archive accepts: what it is, the content type the server sends, and its extensions.</summary>
/// <param name="Extensions">Lower case, with the dot. The first one is added to a file name that has none.</param>
internal sealed record FileType(string Name, DocumentKind Kind, string ContentType, IReadOnlyList<string> Extensions)
{
    public bool Allows(string extension) => Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Decides a file's type from its first bytes, never from the content type or the name the client sends. Only the
/// types listed here are accepted: PDF, JPEG, PNG, WebP, HEIC / HEIF, MP4, MOV, DOCX, XLSX and DWG.
/// </summary>
/// <remarks>
/// <para>DOCX and XLSX are ZIP packages, so their first bytes only say "ZIP". <see cref="Detect"/> answers
/// <see cref="OfficePackage"/>; after the file is stored, <see cref="IdentifyOfficePackage"/> reads the package's
/// table of contents (never its contents) to tell a Word file from an Excel file from any other ZIP.</para>
/// <para>Phone cameras save JPEG, or HEIC on iPhones and many Android phones; both are accepted.</para>
/// </remarks>
internal static class FileTypes
{
    /// <summary>How many first bytes <see cref="Detect"/> looks at. Enough for an ISO media "ftyp" box with its brand list.</summary>
    public const int HeaderLength = 512;

    public static readonly FileType Pdf = new("PDF", DocumentKind.Pdf, "application/pdf", [".pdf"]);
    public static readonly FileType Jpeg = new("JPEG", DocumentKind.Image, "image/jpeg", [".jpg", ".jpeg", ".jpe", ".jfif"]);
    public static readonly FileType Png = new("PNG", DocumentKind.Image, "image/png", [".png"]);
    public static readonly FileType WebP = new("WebP", DocumentKind.Image, "image/webp", [".webp"]);

    // Phones name HEIF files .heic and HEIC files .heif, so both extensions are accepted for both.
    public static readonly FileType Heic = new("HEIC", DocumentKind.Image, "image/heic", [".heic", ".heif"]);
    public static readonly FileType Heif = new("HEIF", DocumentKind.Image, "image/heif", [".heif", ".heic"]);

    public static readonly FileType Mp4 = new("MP4", DocumentKind.Video, "video/mp4", [".mp4", ".m4v"]);
    public static readonly FileType Mov = new("MOV", DocumentKind.Video, "video/quicktime", [".mov", ".qt"]);

    public static readonly FileType Docx = new(
        "DOCX", DocumentKind.Office, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", [".docx"]);

    public static readonly FileType Xlsx = new(
        "XLSX", DocumentKind.Office, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", [".xlsx"]);

    public static readonly FileType Dwg = new("DWG", DocumentKind.Drawing, "image/vnd.dwg", [".dwg"]);

    /// <summary>A ZIP file that may be DOCX or XLSX; decided by <see cref="IdentifyOfficePackage"/> once stored.</summary>
    public static readonly FileType OfficePackage = new("Office", DocumentKind.Office, "application/zip", [".docx", ".xlsx"]);

    public static IReadOnlyList<FileType> All { get; } = [Pdf, Jpeg, Png, WebP, Heic, Heif, Mp4, Mov, Docx, Xlsx, Dwg];

    private static readonly string[] HeicBrands = ["heic", "heix", "hevc", "hevx", "heim", "heis", "hevm", "hevs"];
    private static readonly string[] HeifBrands = ["mif1", "msf1"];
    private static readonly string[] AvifBrands = ["avif", "avis"];

    private static readonly string[] Mp4Brands =
    [
        "isom", "iso2", "iso3", "iso4", "iso5", "iso6", "iso8", "iso9", "mp41", "mp42", "mp71", "avc1",
        "M4V ", "M4VH", "M4VP", "MSNV", "dash", "mmp4", "XAVC",
    ];

    private static ReadOnlySpan<byte> JpegStart => [0xFF, 0xD8, 0xFF];

    private static ReadOnlySpan<byte> PngStart => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ReadOnlySpan<byte> ZipStart => [0x50, 0x4B, 0x03, 0x04];

    // QuickTime files from old cameras start with a top-level atom instead of "ftyp".
    private static readonly string[] QuickTimeAtoms = ["moov", "mdat", "wide", "free", "skip", "pnot"];

    /// <summary>
    /// The type of a file from its first bytes (up to <see cref="HeaderLength"/>), or <c>null</c> when it is not an
    /// accepted type. A program, a script or an HTML page renamed to <c>.pdf</c> answers <c>null</c>.
    /// </summary>
    public static FileType? Detect(ReadOnlySpan<byte> header)
    {
        if (header.StartsWith("%PDF-"u8))
        {
            return Pdf;
        }

        if (header.StartsWith(JpegStart))
        {
            return Jpeg;
        }

        if (header.StartsWith(PngStart))
        {
            return Png;
        }

        if (header.Length >= 12 && header.StartsWith("RIFF"u8) && header.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return WebP;
        }

        // A ZIP local file header. Empty ZIPs ("PK\x05\x06") are refused: an office file always has entries.
        if (header.StartsWith(ZipStart))
        {
            return OfficePackage;
        }

        // AutoCAD drawings: "AC10" and two digits (AC1012 for R13 up to AC1032 for 2018 and later).
        if (header.Length >= 6 && header.StartsWith("AC10"u8) && char.IsAsciiDigit((char)header[4]) && char.IsAsciiDigit((char)header[5]))
        {
            return Dwg;
        }

        return DetectIsoMedia(header);
    }

    /// <summary>
    /// The DOCX or XLSX type of a stored ZIP package, read from its central directory, or <c>null</c> for any
    /// other ZIP (or a damaged one). The entries are never extracted, so a "ZIP bomb" costs nothing.
    /// </summary>
    public static FileType? IdentifyOfficePackage(Stream package)
    {
        try
        {
            using var zip = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
            var names = zip.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);

            if (!names.Contains("[Content_Types].xml"))
            {
                return null;
            }

            if (names.Contains("word/document.xml"))
            {
                return Docx;
            }

            return names.Contains("xl/workbook.xml") ? Xlsx : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when the file name's extension fits the detected type. A name with no extension fits any type (some
    /// phones and browsers send "blob" or "image"); <see cref="FileNames.WithExtension"/> then adds the right one.
    /// </summary>
    public static bool ExtensionAgrees(FileType type, string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension.Length == 0 || type.Allows(extension);
    }

    private static FileType? DetectIsoMedia(ReadOnlySpan<byte> header)
    {
        if (header.Length < 12)
        {
            return null;
        }

        var boxType = Encoding.ASCII.GetString(header.Slice(4, 4));
        if (boxType != "ftyp")
        {
            return QuickTimeAtoms.Contains(boxType, StringComparer.Ordinal) ? Mov : null;
        }

        var boxSize = (int)Math.Min(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header), (uint)header.Length);
        var major = Encoding.ASCII.GetString(header.Slice(8, 4));

        // The compatible brands follow the major brand and its 4-byte version, up to the end of the box.
        var brands = new List<string> { major };
        for (var offset = 16; offset + 4 <= boxSize; offset += 4)
        {
            brands.Add(Encoding.ASCII.GetString(header.Slice(offset, 4)));
        }

        if (brands.Exists(brand => HeicBrands.Contains(brand, StringComparer.Ordinal)))
        {
            return Heic;
        }

        if (HeifBrands.Contains(major, StringComparer.Ordinal))
        {
            // An AVIF image also starts with "mif1"; it is not an accepted type.
            return brands.Exists(brand => AvifBrands.Contains(brand, StringComparer.Ordinal)) ? null : Heif;
        }

        if (major == "qt  ")
        {
            return Mov;
        }

        return Mp4Brands.Contains(major, StringComparer.Ordinal) ? Mp4 : null;
    }
}
