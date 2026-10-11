using Microsoft.Extensions.DependencyInjection;

namespace AlMotaheda.Api.Tests.Infrastructure;

public sealed class ServiceLifetimeTests
{
    [Fact]
    public async Task Startup_fails_when_a_singleton_captures_a_scoped_service()
    {
        // Proves TestApplication validates lifetimes outside Development too.
        var error = await Assert.ThrowsAsync<AggregateException>(() => TestApplication.StartAsync(configureBuilder: builder =>
        {
            builder.Services.AddScoped<ScopedDependency>();
            builder.Services.AddSingleton<SingletonConsumer>();
        }));

        Assert.Contains(error.InnerExceptions, inner => inner.Message.Contains(nameof(ScopedDependency)));
    }

    private sealed class ScopedDependency;

    private sealed class SingletonConsumer(ScopedDependency dependency)
    {
        public ScopedDependency Dependency { get; } = dependency;
    }
}
