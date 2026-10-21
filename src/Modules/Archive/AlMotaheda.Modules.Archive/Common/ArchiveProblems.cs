using AlMotaheda.BuildingBlocks.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AlMotaheda.Modules.Archive.Common;

/// <summary>The Archive module's error responses. Shared codes come from <see cref="ApiProblem"/>.</summary>
internal static class ArchiveProblems
{
    public const string ProjectCompleted = "project_completed";
    public const string FileRequired = "file_required";
    public const string FileEmpty = "file_empty";
    public const string FileTooLarge = "file_too_large";
    public const string FileTypeNotAllowed = "file_type_not_allowed";
    public const string FileExtensionMismatch = "file_extension_mismatch";
    public const string StorageUnavailable = "storage_unavailable";
    public const string IdempotencyKeyReused = "idempotency_key_reused";

    /// <summary>The form field that carries the file, and the field file errors are listed under.</summary>
    public const string FileField = "file";

    public static ValidationProblem Validation(ValidationErrors errors) => ApiProblem.Validation(errors.ToDictionary());

    /// <summary>
    /// A 400 about the file itself, with its own <c>code</c> so the frontend can say exactly what is wrong, and the
    /// message listed under <c>errors.file</c> like any other field.
    /// </summary>
    public static ValidationProblem File(string code, string message) =>
        TypedResults.ValidationProblem(
            new Dictionary<string, string[]> { [FileField] = [message] },
            title: message,
            extensions: new Dictionary<string, object?> { [ApiProblem.CodeKey] = code });

    public static ValidationProblem NoFile() =>
        File(FileRequired, "Take a photo or choose a file, and send it in the form field named file.");

    public static ValidationProblem EmptyFile() => File(FileEmpty, "The file is empty.");

    public static ValidationProblem TypeNotAllowed() =>
        File(FileTypeNotAllowed, "This type of file can't be archived. Send a PDF, a photo (JPEG, PNG, WebP, HEIC), a video (MP4, MOV), a Word or Excel file (DOCX, XLSX) or a drawing (DWG).");

    public static ValidationProblem ExtensionMismatch(string detected) =>
        File(FileExtensionMismatch, $"The file's name doesn't match its content, which is {detected}. Rename the file and try again.");

    /// <summary>413: the request is larger than the limit for this kind of file.</summary>
    public static ProblemHttpResult TooLarge(long maxBytes) =>
        ApiProblem.Create(
            StatusCodes.Status413PayloadTooLarge,
            FileTooLarge,
            $"The file is too large. The limit for this kind of file is {maxBytes / (1024 * 1024)} MB.");

    /// <summary>503: the file store can't be read or written. Safe to retry later.</summary>
    public static ProblemHttpResult StorageDown() =>
        ApiProblem.Create(StatusCodes.Status503ServiceUnavailable, StorageUnavailable, "The file storage is not available right now. Try again later.");

    /// <summary>
    /// The project doesn't exist, or the caller may not see it. Both get the same answer, so a guessed id
    /// reveals nothing.
    /// </summary>
    public static ProblemHttpResult ProjectNotFound() => ApiProblem.NotFoundResult("The project was not found.");

    /// <summary>The document doesn't exist, belongs to another project, or was deleted.</summary>
    public static ProblemHttpResult DocumentNotFound() => ApiProblem.NotFoundResult("The document was not found.");

    /// <summary>409: the upload's <c>Idempotency-Key</c> already belongs to another document of this user.</summary>
    public static ProblemHttpResult KeyUsedElsewhere() =>
        ApiProblem.Create(
            StatusCodes.Status409Conflict,
            IdempotencyKeyReused,
            "This idempotency key was already used for another document. Make a new key for a new upload.");

    public static ProblemHttpResult CompletedProject() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, ProjectCompleted, "A completed project can only be changed by an Executive.");
}
