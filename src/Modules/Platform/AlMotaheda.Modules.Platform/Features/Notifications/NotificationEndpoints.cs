using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Platform.Common;
using AlMotaheda.Modules.Platform.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Platform.Features.Notifications;

/// <summary>One notification as the client sees it. The client opens the record from entityType, entityKey and projectId.</summary>
internal sealed record NotificationView(
    long Id,
    string Type,
    string TitleAr,
    string TitleEn,
    string EntityType,
    string EntityKey,
    int? ProjectId,
    DateTime CreatedAt,
    DateTime? ReadAt);

/// <summary>Reads of the caller's own notifications. Every query is filtered by the caller's id: nobody reads another's.</summary>
internal static class NotificationQueries
{
    public static IQueryable<Notification> Of(PlatformDbContext db, int userId, bool unreadOnly)
    {
        var query = db.Notifications.AsNoTracking().Where(notification => notification.RecipientUserId == userId);
        return unreadOnly ? query.Where(notification => notification.ReadAt == null) : query;
    }

    public static IQueryable<NotificationView> Views(IQueryable<Notification> query) =>
        query
            .OrderByDescending(notification => notification.Id)
            .Select(notification => new NotificationView(
                notification.Id,
                notification.Type,
                notification.TitleAr,
                notification.TitleEn,
                notification.EntityType,
                notification.EntityKey,
                notification.ProjectId,
                notification.CreatedAt,
                notification.ReadAt));
}

/// <summary><c>GET /api/notifications</c>: the caller's notifications, newest first, with the unread count.</summary>
internal static class ListNotifications
{
    internal sealed record Response(IReadOnlyList<NotificationView> Items, int Page, int PageSize, int TotalCount, int UnreadCount);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/notifications", HandleAsync)
            .WithName(nameof(ListNotifications))
            .WithSummary("List the caller's own notifications, newest first (unreadOnly; page, pageSize), with the unread count");

    internal static async Task<Results<Ok<Response>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        HttpContext httpContext,
        PlatformDbContext db,
        CancellationToken cancellationToken,
        bool unreadOnly = false,
        int page = 1,
        int pageSize = Paging.DefaultPageSize)
    {
        if (httpContext.User.GetUserId() is not { } userId)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        Paging.Validate(page, pageSize, errors);
        if (errors.HasErrors)
        {
            return PlatformProblems.Validation(errors);
        }

        var query = NotificationQueries.Of(db, userId, unreadOnly);
        var totalCount = await query.CountAsync(cancellationToken);
        var unreadCount = unreadOnly ? totalCount : await NotificationQueries.Of(db, userId, unreadOnly: true).CountAsync(cancellationToken);
        var items = await NotificationQueries.Views(query).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);

        return TypedResults.Ok(new Response(items, page, pageSize, totalCount, unreadCount));
    }
}

/// <summary><c>GET /api/notifications/unread-count</c>: the number for the bell badge.</summary>
internal static class GetUnreadNotificationCount
{
    internal sealed record Response(int Count);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/notifications/unread-count", HandleAsync)
            .WithName(nameof(GetUnreadNotificationCount))
            .WithSummary("Count the caller's unread notifications");

    internal static async Task<Results<Ok<Response>, ProblemHttpResult>> HandleAsync(
        HttpContext httpContext, PlatformDbContext db, CancellationToken cancellationToken)
    {
        if (httpContext.User.GetUserId() is not { } userId)
        {
            return ApiProblem.ForbiddenResult();
        }

        var count = await NotificationQueries.Of(db, userId, unreadOnly: true).CountAsync(cancellationToken);
        return TypedResults.Ok(new Response(count));
    }
}

/// <summary>
/// <c>POST /api/notifications/{id}/read</c> and <c>POST /api/notifications/read-all</c>. Marking read twice is
/// harmless: the first read time is kept.
/// </summary>
internal static class MarkNotificationsRead
{
    internal sealed record Response(int Marked);

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapPost("/notifications/{notificationId:long}/read", MarkOneAsync)
            .WithName("MarkNotificationRead")
            .WithSummary("Mark one of the caller's notifications as read. 404 for someone else's");

        api.MapPost("/notifications/read-all", MarkAllAsync)
            .WithName("MarkAllNotificationsRead")
            .WithSummary("Mark all of the caller's notifications as read");
    }

    internal static async Task<Results<NoContent, ProblemHttpResult>> MarkOneAsync(
        long notificationId, HttpContext httpContext, PlatformDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        if (httpContext.User.GetUserId() is not { } userId)
        {
            return ApiProblem.ForbiddenResult();
        }

        var now = time.GetUtcNow().UtcDateTime;
        var found = await db.Notifications
            .Where(notification => notification.Id == notificationId && notification.RecipientUserId == userId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(notification => notification.ReadAt, notification => notification.ReadAt ?? now), cancellationToken);

        return found == 0 ? PlatformProblems.NotificationNotFound() : TypedResults.NoContent();
    }

    internal static async Task<Results<Ok<Response>, ProblemHttpResult>> MarkAllAsync(
        HttpContext httpContext, PlatformDbContext db, TimeProvider time, PlatformLog log, CancellationToken cancellationToken)
    {
        if (httpContext.User.GetUserId() is not { } userId)
        {
            return ApiProblem.ForbiddenResult();
        }

        var now = time.GetUtcNow().UtcDateTime;
        var marked = await db.Notifications
            .Where(notification => notification.RecipientUserId == userId && notification.ReadAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(notification => notification.ReadAt, now), cancellationToken);

        log.NotificationsRead(userId, marked);
        return TypedResults.Ok(new Response(marked));
    }
}
