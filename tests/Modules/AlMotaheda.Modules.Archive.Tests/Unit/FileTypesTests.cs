using AlMotaheda.Modules.Archive.Data;
using AlMotaheda.Modules.Archive.Files;

namespace AlMotaheda.Modules.Archive.Tests.Unit;

/// <summary>The type of a file comes from its first bytes; the name only has to agree.</summary>
public sealed class FileTypesTests
{
    public static TheoryData<string, string, string, string> AcceptedFiles => new()
    {
        // sample, expected type, content type, kind
        { "pdf", "PDF", "application/pdf", "Pdf" },
        { "jpeg", "JPEG", "image/jpeg", "Image" },
        { "cameraJpeg", "JPEG", "image/jpeg", "Image" },
        { "png", "PNG", "image/png", "Image" },
        { "webp", "WebP", "image/webp", "Image" },
        { "heic", "HEIC", "image/heic", "Image" },
        { "androidHeic", "HEIC", "image/heic", "Image" },
        { "heif", "HEIF", "image/heif", "Image" },
        { "mp4", "MP4", "video/mp4", "Video" },
        { "mov", "MOV", "video/quicktime", "Video" },
        { "oldMov", "MOV", "video/quicktime", "Video" },
        { "dwg", "DWG", "image/vnd.dwg", "Drawing" },
    };

    [Theory]
    [MemberData(nameof(AcceptedFiles))]
    public void Every_accepted_type_is_recognised_from_its_first_bytes(string sample, string name, string contentType, string kind)
    {
        var type = FileTypes.Detect(Sample(sample).AsSpan(0, FileTypes.HeaderLength));

        Assert.NotNull(type);
        Assert.Equal(name, type.Name);
        Assert.Equal(contentType, type.ContentType);
        Assert.Equal(kind, type.Kind.ToString());
    }

    [Theory]
    [InlineData("docx", "DOCX", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("xlsx", "XLSX", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    public void Word_and_excel_files_are_told_apart_by_their_table_of_contents(string sample, string name, string contentType)
    {
        var bytes = Sample(sample);

        Assert.Equal(FileTypes.OfficePackage, FileTypes.Detect(bytes));
        var type = FileTypes.IdentifyOfficePackage(new MemoryStream(bytes));

        Assert.NotNull(type);
        Assert.Equal(name, type.Name);
        Assert.Equal(contentType, type.ContentType);
        Assert.Equal(DocumentKind.Office, type.Kind);
    }

    [Fact]
    public void A_zip_that_is_not_an_office_file_is_refused()
    {
        var bytes = SampleFiles.PlainZip();

        Assert.Equal(FileTypes.OfficePackage, FileTypes.Detect(bytes));
        Assert.Null(FileTypes.IdentifyOfficePackage(new MemoryStream(bytes)));
    }

    [Fact]
    public void A_damaged_zip_is_refused_without_an_error()
    {
        var bytes = SampleFiles.Docx()[..100];

        Assert.Null(FileTypes.IdentifyOfficePackage(new MemoryStream(bytes)));
    }

    [Theory]
    [InlineData("exe")]
    [InlineData("html")]
    [InlineData("avif")]
    public void Anything_else_is_refused(string sample)
    {
        Assert.Null(FileTypes.Detect(Sample(sample)));
    }

    [Fact]
    public void Text_and_tiny_files_are_refused()
    {
        Assert.Null(FileTypes.Detect("hello, this is a text file"u8));
        Assert.Null(FileTypes.Detect([0xFF, 0xD8]));
        Assert.Null(FileTypes.Detect([]));
        Assert.Null(FileTypes.Detect("AC10"u8));
        Assert.Null(FileTypes.Detect([0, 0, 0, 0x18, .. "ftyp"u8, .. "3gp5"u8]));
    }

    [Fact]
    public void An_executable_renamed_to_pdf_is_not_a_pdf()
    {
        var type = FileTypes.Detect(SampleFiles.Exe());

        Assert.Null(type);
    }

    [Theory]
    [InlineData("pdf", "contract.pdf", true)]
    [InlineData("pdf", "contract.PDF", true)]
    [InlineData("pdf", "contract.jpg", false)]
    [InlineData("jpeg", "IMG_0001.JPG", true)]
    [InlineData("jpeg", "photo.jpeg", true)]
    [InlineData("jpeg", "photo.png", false)]
    [InlineData("heic", "IMG_0002.HEIC", true)]
    [InlineData("heic", "IMG_0002.heif", true)]
    [InlineData("heif", "photo.heic", true)]
    [InlineData("mov", "IMG_0003.MOV", true)]
    [InlineData("mov", "IMG_0003.mp4", false)]
    [InlineData("mp4", "VID_2026.mp4", true)]
    [InlineData("dwg", "plan.dwg", true)]
    [InlineData("dwg", "plan.dxf", false)]
    [InlineData("jpeg", "blob", true)]
    [InlineData("jpeg", "image", true)]
    public void The_extension_must_agree_with_the_content(string sample, string fileName, bool agrees)
    {
        var type = FileTypes.Detect(Sample(sample))!;

        Assert.Equal(agrees, FileTypes.ExtensionAgrees(type, fileName));
    }

    [Fact]
    public void Office_extensions_agree_only_with_their_own_package()
    {
        Assert.True(FileTypes.ExtensionAgrees(FileTypes.Docx, "minutes.docx"));
        Assert.False(FileTypes.ExtensionAgrees(FileTypes.Docx, "minutes.xlsx"));
        Assert.True(FileTypes.ExtensionAgrees(FileTypes.Xlsx, "boq.XLSX"));
        Assert.False(FileTypes.ExtensionAgrees(FileTypes.Xlsx, "boq.zip"));
    }

    [Fact]
    public void Every_accepted_type_has_one_kind_and_a_usual_extension()
    {
        Assert.Equal(11, FileTypes.All.Count);
        Assert.All(FileTypes.All, type => Assert.StartsWith(".", type.Extensions[0]));
        Assert.Equal(
            ["Drawing", "Image", "Office", "Pdf", "Video"],
            FileTypes.All.Select(type => type.Kind.ToString()).Distinct().Order());
    }

    internal static byte[] Sample(string name) => name switch
    {
        "pdf" => SampleFiles.Pdf(),
        "jpeg" => SampleFiles.Jpeg(),
        "cameraJpeg" => SampleFiles.CameraJpeg(),
        "png" => SampleFiles.Png(),
        "webp" => SampleFiles.WebP(),
        "heic" => SampleFiles.Heic(),
        "androidHeic" => SampleFiles.AndroidHeic(),
        "heif" => SampleFiles.Heif(),
        "avif" => SampleFiles.Avif(),
        "mp4" => SampleFiles.Mp4(),
        "mov" => SampleFiles.Mov(),
        "oldMov" => SampleFiles.OldMov(),
        "dwg" => SampleFiles.Dwg(),
        "exe" => SampleFiles.Exe(),
        "html" => SampleFiles.Html(),
        "docx" => SampleFiles.Docx(),
        "xlsx" => SampleFiles.Xlsx(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown sample."),
    };
}
