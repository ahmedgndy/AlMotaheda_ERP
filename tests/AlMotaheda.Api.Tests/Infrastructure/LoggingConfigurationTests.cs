using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Api.Tests.Infrastructure;

public sealed class LoggingConfigurationTests
{
    [Fact]
    public async Task Production_console_logger_drops_messages_instead_of_blocking_requests()
    {
        // With the default (Wait), a slow log output makes request threads wait for the logger,
        // which stalled the whole API in load tests. DropWrite keeps requests moving.
        await using var app = await TestApplication.StartAsync(environment: "Production");

        var options = app.Services.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>().CurrentValue;

        Assert.Equal(ConsoleLoggerQueueFullMode.DropWrite, options.QueueFullMode);
        Assert.Equal("json", options.FormatterName);
    }
}
