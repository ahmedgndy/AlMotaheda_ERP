using AlMotaheda.Modules.Reports.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Reports.Tests.Unit;

public sealed class DatabaseSchemaTests
{
    [Fact]
    public void Committed_migrations_match_the_model()
    {
        using var db = new ReportsDbContextDesignTimeFactory().CreateDbContext([]);

        Assert.False(db.Database.HasPendingModelChanges(), "The model changed without a migration. Run dotnet ef migrations add.");
        Assert.Equal(["InitialReports"], db.Database.GetMigrations().Select(id => id[(id.IndexOf('_') + 1)..]));
        Assert.Contains("[Content] varbinary(max) NULL", db.Database.GenerateCreateScript());
    }
}
