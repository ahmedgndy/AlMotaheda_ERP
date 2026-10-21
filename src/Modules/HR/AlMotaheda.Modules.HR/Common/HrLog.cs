using Microsoft.Extensions.Logging;

namespace AlMotaheda.Modules.HR.Common;

/// <summary>Structured events of the HR module (event ids 9001 to 9999). Ids only: never names, national ids or amounts.</summary>
internal sealed partial class HrLog(ILogger<HrLog> logger)
{
    private readonly ILogger<HrLog> _logger = logger;

    [LoggerMessage(EventId = 9001, Level = LogLevel.Information, Message = "User {ActorId} created department {DepartmentId}")]
    public partial void DepartmentCreated(int actorId, int departmentId);

    [LoggerMessage(EventId = 9002, Level = LogLevel.Information, Message = "User {ActorId} updated department {DepartmentId}")]
    public partial void DepartmentUpdated(int actorId, int departmentId);

    [LoggerMessage(EventId = 9011, Level = LogLevel.Information, Message = "User {ActorId} created employee {EmployeeId}")]
    public partial void EmployeeCreated(int actorId, int employeeId);

    [LoggerMessage(EventId = 9012, Level = LogLevel.Information, Message = "User {ActorId} updated employee {EmployeeId}")]
    public partial void EmployeeUpdated(int actorId, int employeeId);

    [LoggerMessage(EventId = 9013, Level = LogLevel.Information, Message = "User {ActorId} ended employee {EmployeeId}")]
    public partial void EmployeeEnded(int actorId, int employeeId);

    [LoggerMessage(EventId = 9021, Level = LogLevel.Information, Message = "User {ActorId} added document {DocumentId} to employee {EmployeeId}")]
    public partial void DocumentAdded(int actorId, int employeeId, int documentId);

    [LoggerMessage(EventId = 9022, Level = LogLevel.Information, Message = "User {ActorId} updated document {DocumentId}")]
    public partial void DocumentUpdated(int actorId, int documentId);

    [LoggerMessage(EventId = 9031, Level = LogLevel.Information, Message = "User {ActorId} set salary {StructureId} of employee {EmployeeId}")]
    public partial void SalarySet(int actorId, int employeeId, int structureId);

    [LoggerMessage(EventId = 9041, Level = LogLevel.Information, Message = "User {ActorId} added payroll input {InputId} ({Kind}) for employee {EmployeeId}")]
    public partial void InputAdded(int actorId, int employeeId, int inputId, string kind);

    [LoggerMessage(EventId = 9042, Level = LogLevel.Information, Message = "User {ActorId} cancelled payroll input {InputId}")]
    public partial void InputCancelled(int actorId, int inputId);

    [LoggerMessage(EventId = 9051, Level = LogLevel.Information, Message = "User {ActorId} calculated salary sheet {SheetId} ({EmployeeCount} employees)")]
    public partial void SheetCalculated(int actorId, int sheetId, int employeeCount);

    [LoggerMessage(EventId = 9052, Level = LogLevel.Information, Message = "User {ActorId} moved salary sheet {SheetId} to {Status}")]
    public partial void SheetMoved(int actorId, int sheetId, string status);

    [LoggerMessage(EventId = 9053, Level = LogLevel.Information, Message = "User {ActorId} requested payment {RequestId} of salary sheet {SheetId}")]
    public partial void SheetPaymentRequested(int actorId, int sheetId, int requestId);

    [LoggerMessage(EventId = 9054, Level = LogLevel.Information, Message = "User {ActorId} confirmed payment request {RequestId}: salary sheet {SheetId} paid")]
    public partial void SheetPaid(int actorId, int sheetId, int requestId);

    [LoggerMessage(EventId = 9061, Level = LogLevel.Information, Message = "User {ActorId} assigned employee {EmployeeId} to project {ProjectId} (assignment {AssignmentId}, {Role})")]
    public partial void EmployeeAssigned(int actorId, int employeeId, int projectId, int assignmentId, string role);

    [LoggerMessage(EventId = 9062, Level = LogLevel.Information, Message = "User {ActorId} ended assignment {AssignmentId} of employee {EmployeeId}")]
    public partial void AssignmentEnded(int actorId, int employeeId, int assignmentId);

    [LoggerMessage(EventId = 9071, Level = LogLevel.Information, Message = "User {ActorId} referred employee {EmployeeId} to HR (referral {ReferralId}, {SourceType} {SourceId})")]
    public partial void Referred(int actorId, int employeeId, int referralId, string sourceType, int sourceId);

    [LoggerMessage(EventId = 9081, Level = LogLevel.Information, Message = "User {ActorId} submitted HR request {RequestId} ({Type}) for employee {EmployeeId}")]
    public partial void RequestSubmitted(int actorId, int requestId, string type, int employeeId);

    [LoggerMessage(EventId = 9082, Level = LogLevel.Information, Message = "User {ActorId} moved HR request {RequestId} to {Status}")]
    public partial void RequestMoved(int actorId, int requestId, string status);

    [LoggerMessage(EventId = 9083, Level = LogLevel.Information, Message = "User {ActorId} wrote on HR request {RequestId}")]
    public partial void RequestMessage(int actorId, int requestId);

    [LoggerMessage(EventId = 9084, Level = LogLevel.Information, Message = "User {ActorId} uploaded HR attachment {AttachmentId} ({SizeBytes} bytes)")]
    public partial void AttachmentUploaded(int actorId, int attachmentId, long sizeBytes);

    [LoggerMessage(EventId = 9085, Level = LogLevel.Warning, Message = "User {ActorId} was refused {Action} on HR request {RequestId}: {Reason}")]
    public partial void RequestRefused(int actorId, string action, int requestId, string reason);

    [LoggerMessage(EventId = 9072, Level = LogLevel.Information, Message = "User {ActorId} moved referral {ReferralId} to {Status} ({Outcome})")]
    public partial void ReferralMoved(int actorId, int referralId, string status, string? outcome);
}
