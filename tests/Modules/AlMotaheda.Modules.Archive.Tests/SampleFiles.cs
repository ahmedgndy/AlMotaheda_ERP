using System.IO.Compression;
using System.Text;

namespace AlMotaheda.Modules.Archive.Tests;

/// <summary>
/// Small files with the real first bytes of each type, padded to any size. Only the start of a file decides its
/// type, so these are enough to test detection, limits and the store.
/// </summary>
internal static class SampleFiles
{
    public static byte[] Pdf(int size = 2048) => Pad("%PDF-1.7\n1 0 obj\n"u8.ToArray(), size);

    public static byte[] Jpeg(int size = 2048) => Pad([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, .. "JFIF"u8, 0x00, 0x01, 0x01], size);

    /// <summary>A JPEG as a phone camera writes it: EXIF first instead of JFIF.</summary>
    public static byte[] CameraJpeg(int size = 4096) => Pad([0xFF, 0xD8, 0xFF, 0xE1, 0x1C, 0x45, .. "Exif"u8, 0x00, 0x00], size);

    public static byte[] Png(int size = 2048) => Pad([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, .. "IHDR"u8], size);

    public static byte[] WebP(int size = 2048) => Pad([.. "RIFF"u8, 0x24, 0x08, 0x00, 0x00, .. "WEBP"u8, .. "VP8 "u8], size);

    /// <summary>An iPhone photo: major brand "heic".</summary>
    public static byte[] Heic(int size = 4096) => Pad(Ftyp("heic", "mif1", "heic"), size);

    /// <summary>Some Android phones: major brand "mif1" with "heic" among the compatible brands.</summary>
    public static byte[] AndroidHeic(int size = 4096) => Pad(Ftyp("mif1", "mif1", "heic", "miaf"), size);

    public static byte[] Heif(int size = 4096) => Pad(Ftyp("mif1", "mif1", "miaf"), size);

    public static byte[] Avif(int size = 4096) => Pad(Ftyp("avif", "mif1", "miaf", "avif"), size);

    public static byte[] Mp4(int size = 4096) => Pad(Ftyp("isom", "isom", "iso2", "avc1", "mp41"), size);

    /// <summary>An iPhone video: QuickTime with major brand "qt  ".</summary>
    public static byte[] Mov(int size = 4096) => Pad(Ftyp("qt  ", "qt  "), size);

    /// <summary>An old QuickTime file that starts with a "moov" atom instead of "ftyp".</summary>
    public static byte[] OldMov(int size = 4096) => Pad([0x00, 0x00, 0x10, 0x00, .. "moov"u8], size);

    public static byte[] Dwg(int size = 2048) => Pad("AC1032"u8.ToArray(), size);

    /// <summary>A Windows program: what an attacker would rename to <c>invoice.pdf</c>.</summary>
    public static byte[] Exe(int size = 2048) => Pad([.. "MZ"u8, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00], size);

    public static byte[] Html(int size = 512) => Pad("<html><script>alert(1)</script></html>"u8.ToArray(), size);

    public static byte[] Docx() => Zip("[Content_Types].xml", "_rels/.rels", "word/document.xml");

    public static byte[] Xlsx() => Zip("[Content_Types].xml", "_rels/.rels", "xl/workbook.xml", "xl/worksheets/sheet1.xml");

    /// <summary>A plain ZIP: same first bytes as DOCX and XLSX, but not an office file.</summary>
    public static byte[] PlainZip() => Zip("readme.txt", "setup.exe");

    public static byte[] Zip(params string[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write("<x/>");
            }
        }

        return buffer.ToArray();
    }

    private static byte[] Ftyp(string major, params string[] compatible)
    {
        var size = 16 + (4 * compatible.Length);
        var box = new List<byte> { 0, 0, 0, (byte)size };
        box.AddRange("ftyp"u8.ToArray());
        box.AddRange(Encoding.ASCII.GetBytes(major));
        box.AddRange([0, 0, 0, 0]);
        foreach (var brand in compatible)
        {
            box.AddRange(Encoding.ASCII.GetBytes(brand));
        }

        return [.. box];
    }

    /// <summary>The header followed by repeatable filler bytes up to <paramref name="size"/>.</summary>
    private static byte[] Pad(byte[] header, int size)
    {
        var bytes = new byte[Math.Max(size, header.Length)];
        header.CopyTo(bytes, 0);
        for (var index = header.Length; index < bytes.Length; index++)
        {
            bytes[index] = (byte)(index * 31 % 251);
        }

        return bytes;
    }
}
