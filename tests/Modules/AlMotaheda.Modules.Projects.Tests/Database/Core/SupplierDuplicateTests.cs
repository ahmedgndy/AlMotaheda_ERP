using System.Net;
using AlMotaheda.Modules.Access.Tests.Database;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;
using static AlMotaheda.Modules.Projects.Tests.Database.Core.CoreApi;

namespace AlMotaheda.Modules.Projects.Tests.Database.Core;

/// <summary>The same supplier can't be entered twice, by either name or by tax number (refactor plan M9).</summary>
[Collection(SqlServerCollection.Name)]
public sealed class SupplierDuplicateTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task A_supplier_is_unique_by_arabic_name_english_name_and_tax_number()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();

        await EnsureStatus(await CreateAsync(api, executive, "أسمنت السويس", "Suez Cement", "100-200-300"), HttpStatusCode.Created);

        var sameArabic = await CreateAsync(api, executive, "أسمنت السويس", "Suez Cement Co", null);
        var sameEnglish = await CreateAsync(api, executive, "شركة أخرى", "Suez Cement", null);
        var sameTax = await CreateAsync(api, executive, "اسم مختلف", "Different Name", "100-200-300");

        Assert.Equal("supplier_exists", await CodeOf(sameArabic));
        Assert.Equal("supplier_exists", await CodeOf(sameEnglish));
        Assert.Equal("supplier_tax_number_exists", await CodeOf(sameTax));

        // Suppliers without a tax number don't collide on it.
        await EnsureStatus(await CreateAsync(api, executive, "مورد بلا رقم", "No Tax One", null), HttpStatusCode.Created);
        await EnsureStatus(await CreateAsync(api, executive, "مورد بلا رقم ٢", "No Tax Two", null), HttpStatusCode.Created);
    }

    private static Task<HttpResponseMessage> CreateAsync(ProjectsApi api, Session session, string nameAr, string nameEn, string? taxNumber) =>
        api.SendAsync(HttpMethod.Post, "/api/suppliers", session, new { nameAr, nameEn, kind = "Materials", taxNumber });
}
