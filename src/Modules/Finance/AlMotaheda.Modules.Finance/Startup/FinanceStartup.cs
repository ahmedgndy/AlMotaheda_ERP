using AlMotaheda.Modules.Finance.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AlMotaheda.Modules.Finance.Startup;

/// <summary>
/// Applies pending Finance migrations once before the API accepts requests, only when
/// <c>Database:ApplyMigrationsOnStartup</c> is true (meant for development; servers run the reviewed script).
/// </summary>
/// <remarks>
/// Finance is last in ModuleCatalog: its first migration copies the payments already recorded in the Projects,
/// Workforce, Subcontractors and Fleet tables into the ledger, so those tables must exist. See
/// <see cref="ModuleMigrations"/>.
/// </remarks>
internal sealed class FinanceStartup(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<FinanceStartup> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue<bool>(ModuleMigrations.ApplyMigrationsSetting))
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FinanceDbContext>();
        await ModuleMigrations.ApplyAsync(
            db,
            "Finance",
            ["projects.SupplierPayment", "workforce.WorkerPayment", "sub.SubPayment", "fleet.EquipmentCost"],
            "Projects, Workforce, Subcontractors and Fleet",
            logger,
            cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
