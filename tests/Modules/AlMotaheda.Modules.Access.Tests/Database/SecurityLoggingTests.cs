using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;

namespace AlMotaheda.Modules.Access.Tests.Database;

/// <summary>Security events are logged (they are the audit trail until the AuditLog table exists), but never secrets.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class SecurityLoggingTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task Security_events_are_logged_without_passwords_or_tokens()
    {
        var sink = new CapturingLoggerProvider();
        await using var api = await AccessApi.StartAsync(
            database,
            new Dictionary<string, string?> { ["Logging:LogLevel:Default"] = "Information" },
            builder => builder.Logging.AddProvider(sink));

        await api.PostLoginAsync(AccessApi.ExecutiveEmail, "a-wrong-password-123");
        var executive = await api.SignInExecutiveAsync();
        var created = await api.CreateUserAsync(executive, "staff");
        var refreshed = await Session.FromAsync(await api.RefreshAsync(executive.RefreshCookie));
        var replay = await api.RefreshAsync(executive.RefreshCookie);

        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        var log = string.Join("\n", sink.Messages);
        Assert.Contains("Sign-in failed: WrongPassword", log);
        Assert.Contains($"User {executive.UserId} signed in", log);
        Assert.Contains($"created user {created.GetProperty("id").GetInt32()} with role staff", log);
        Assert.Contains("Refresh token replay detected", log);

        foreach (var secret in new[]
                 {
                     AccessApi.BootstrapPassword, AccessApi.ExecutivePassword, AccessApi.UserPassword, "a-wrong-password-123",
                     executive.AccessToken, refreshed.AccessToken, executive.RefreshCookie!, refreshed.RefreshCookie!,
                 })
        {
            Assert.DoesNotContain(secret, log);
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                provider.Messages.Enqueue(formatter(state, exception) + (exception is null ? "" : " " + exception));
        }
    }
}
