using AlMotaheda.Modules.Archive.Files;

namespace AlMotaheda.Modules.Archive.Tests.Unit;

/// <summary>A client's file name is only shown and put in the download header, after cleaning.</summary>
public sealed class FileNamesTests
{
    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..\\..\\Windows\\System32\\cmd.exe", "cmd.exe")]
    [InlineData("C:\\Users\\eng\\Desktop\\certificate 7.pdf", "certificate 7.pdf")]
    [InlineData("/var/www/../../root/.ssh/id_rsa", "id_rsa")]
    [InlineData("..", "file")]
    [InlineData("../", "file")]
    [InlineData("...", "file")]
    [InlineData("", "file")]
    [InlineData(null, "file")]
    [InlineData("   ", "file")]
    public void Path_parts_are_removed(string? sent, string expected)
    {
        Assert.Equal(expected, FileNames.Clean(sent));
    }

    [Fact]
    public void Control_characters_and_direction_marks_are_removed()
    {
        var rightToLeftOverride = (char)0x202E;

        Assert.Equal("invoice.pdf", FileNames.Clean("invo\0ice\r\n.pdf"));
        Assert.Equal("report.pdf", FileNames.Clean("report\t.pdf"));

        // "invoice[RLO]fdp.exe" displays as "invoiceexe.pdf"; without the mark it shows what it really is.
        Assert.Equal("invoicefdp.exe", FileNames.Clean("invoice" + rightToLeftOverride + "fdp.exe"));
    }

    [Fact]
    public void Characters_windows_refuses_become_underscores_and_spaces_are_tidied()
    {
        Assert.Equal("a_b_c_d_.pdf", FileNames.Clean("a<b>c|d?.pdf"));
        Assert.Equal("site photo 1.jpg", FileNames.Clean("  site   photo  1.jpg  "));
        Assert.Equal("minutes.docx", FileNames.Clean("minutes.docx. . "));
    }

    [Fact]
    public void Arabic_names_are_kept()
    {
        var arabic = "\u0645\u0633\u062a\u062e\u0644\u0635 \u0631\u0642\u0645 3.pdf";

        Assert.Equal(arabic, FileNames.Clean(arabic));
    }

    [Fact]
    public void Long_names_are_cut_to_255_characters_keeping_the_extension()
    {
        var cleaned = FileNames.Clean(new string('a', 400) + ".pdf");

        Assert.Equal(255, cleaned.Length);
        Assert.EndsWith("aaa.pdf", cleaned);
    }

    [Fact]
    public void A_name_without_extension_gets_the_one_of_its_type()
    {
        Assert.Equal("blob.jpg", FileNames.WithExtension("blob", FileTypes.Jpeg));
        Assert.Equal("file.heic", FileNames.WithExtension(FileNames.Clean(""), FileTypes.Heic));
        Assert.Equal("photo.JPEG", FileNames.WithExtension("photo.JPEG", FileTypes.Jpeg));
    }
}
