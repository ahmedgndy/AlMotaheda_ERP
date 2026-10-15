using System.Text;
using AlMotaheda.Modules.Archive.Data.Configurations;

namespace AlMotaheda.Modules.Archive.Files;

/// <summary>
/// Turns the file name a client sends into a safe display name. It is only shown and used in the download
/// header; the stored file has a server-made key and no name at all.
/// </summary>
internal static class FileNames
{
    /// <summary>Used when nothing usable is left of the client's name.</summary>
    public const string Fallback = "file";

    // Characters Windows refuses in file names: a download must save under the same name on every system.
    private const string Reserved = "<>:\"/\\|?*";

    /// <summary>
    /// The base name only (no folder, so "../../etc/passwd" becomes "passwd"), without control characters or
    /// direction marks (which can disguise "exe.pdf" as "fdp.exe"), with spaces tidied, dots and spaces trimmed
    /// from the ends, and at most 255 characters with the extension kept.
    /// </summary>
    public static string Clean(string? fileName)
    {
        var text = fileName ?? "";

        // Both kinds of separator, whatever the server's system is: a Windows path can arrive on Linux.
        var lastSeparator = text.LastIndexOfAny(['/', '\\']);
        if (lastSeparator >= 0)
        {
            text = text[(lastSeparator + 1)..];
        }

        var builder = new StringBuilder(text.Length);
        var lastWasSpace = false;

        // Runes, not chars: a broken surrogate pair comes back as the replacement character and is dropped.
        foreach (var rune in text.Normalize(NormalizationForm.FormC).EnumerateRunes())
        {
            if (Rune.IsControl(rune) || rune == Rune.ReplacementChar || IsDirectionMark(rune.Value))
            {
                continue;
            }

            if (Rune.IsWhiteSpace(rune))
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                }

                lastWasSpace = true;
                continue;
            }

            if (rune.IsAscii && Reserved.Contains((char)rune.Value))
            {
                builder.Append('_');
            }
            else
            {
                builder.Append(rune.ToString());
            }

            lastWasSpace = false;
        }

        var cleaned = builder.ToString().Trim(' ', '.');
        if (cleaned.Length == 0)
        {
            return Fallback;
        }

        return Shorten(cleaned, DocumentConfiguration.FileNameMaxLength);
    }

    /// <summary>Adds the type's usual extension to a name that has none.</summary>
    public static string WithExtension(string fileName, FileType type) =>
        Path.GetExtension(fileName).Length > 0
            ? fileName
            : Shorten(fileName, DocumentConfiguration.FileNameMaxLength - type.Extensions[0].Length) + type.Extensions[0];

    private static string Shorten(string name, int maxLength)
    {
        if (name.Length <= maxLength)
        {
            return name;
        }

        var extension = Path.GetExtension(name);
        if (extension.Length is 0 or > 16)
        {
            extension = "";
        }

        var stem = name[..(maxLength - extension.Length)];

        // Never cut a character in half.
        if (char.IsHighSurrogate(stem[^1]))
        {
            stem = stem[..^1];
        }

        return stem.TrimEnd(' ', '.') + extension;
    }

    /// <summary>Unicode marks that change the display direction of text (left-to-right and right-to-left overrides).</summary>
    private static bool IsDirectionMark(int value) =>
        value is 0x200E or 0x200F or 0x061C or (>= 0x202A and <= 0x202E) or (>= 0x2066 and <= 0x2069);
}
