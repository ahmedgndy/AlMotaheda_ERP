using AlMotaheda.Modules.Access.Common;
using AlMotaheda.Modules.Access.Data;
using AlMotaheda.Modules.Access.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Access.Startup;

/// <summary>
/// Runs once before the API accepts requests:
/// <list type="number">
///   <item>applies pending migrations, only when <c>Database:ApplyMigrationsOnStartup</c> is true
///   (meant for development; servers run the reviewed script or a migration bundle instead);</item>
///   <item>creates the first Executive from <c>Access:Bootstrap</c>, only when the user table is empty (Q13).</item>
/// </list>
/// </summary>
internal sealed class AccessStartup(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    IOptions<AccessBootstrapOptions> bootstrapOptions,
    ILogger<AccessStartup> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var applyMigrations = configuration.GetValue<bool>(ModuleMigrations.ApplyMigrationsSetting);
        var bootstrap = bootstrapOptions.Value;

        if (!applyMigrations && !bootstrap.IsConfigured)
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccessDbContext>();

        if (applyMigrations)
        {
            // Access is first in ModuleCatalog: its tables point to no other module.
            await ModuleMigrations.ApplyAsync(db, "Access", [], "", logger, cancellationToken);
        }

        if (bootstrap.IsConfigured)
        {
            await CreateFirstExecutiveAsync(scope.ServiceProvider, db, bootstrap, cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task CreateFirstExecutiveAsync(
        IServiceProvider services, AccessDbContext db, AccessBootstrapOptions bootstrap, CancellationToken cancellationToken)
    {
        if (await db.Users.AnyAsync(cancellationToken))
        {
            logger.LogWarning("Access:Bootstrap is set but users already exist, so it was ignored. Remove the Access:Bootstrap settings.");
            return;
        }

        var errors = new ValidationErrors();
        var email = InputRules.Email(bootstrap.Email, errors, "Access:Bootstrap:Email");
        InputRules.NewPassword(bootstrap.Password, email, errors, "Access:Bootstrap:Password");
        var nameAr = InputRules.RequiredText(bootstrap.NameAr, InputRules.NameMaxLength, errors, "Access:Bootstrap:NameAr", "Arabic name");
        var nameEn = InputRules.RequiredText(bootstrap.NameEn, InputRules.NameMaxLength, errors, "Access:Bootstrap:NameEn", "English name");

        if (errors.HasErrors)
        {
            // The messages name the setting and the rule, never the value.
            var problems = errors.ToDictionary().SelectMany(entry => entry.Value.Select(message => $"{entry.Key}: {message}"));
            throw new InvalidOperationException("The Access:Bootstrap settings are invalid. " + string.Join(" ", problems));
        }

        var passwords = services.GetRequiredService<PasswordService>();
        var time = services.GetRequiredService<TimeProvider>();

        var executive = new User
        {
            Email = email!,
            NormalizedEmail = InputRules.NormalizeEmail(email!),
            NameAr = nameAr!,
            NameEn = nameEn!,
            RoleId = PermissionCatalog.RoleId(RoleCodes.Executive),
            Status = UserStatus.Active,
            MustChangePassword = true,
            CreatedAt = time.GetUtcNow().UtcDateTime,
            SignInState = new UserSignInState(),
        };
        executive.PasswordHash = passwords.Hash(executive, bootstrap.Password!);
        db.Users.Add(executive);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (UniqueViolations.Classify(exception) == UniqueViolation.Email)
        {
            // Another server instance created it at the same moment.
            return;
        }

        services.GetRequiredService<SecurityLog>().BootstrapExecutiveCreated(executive.Id);
    }
}
