using System.Net;
using System.Security.Claims;
using AlMotaheda.Api.Tests;
using AlMotaheda.Modules.Access.Tests;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.Extensions.DependencyInjection;
using static AlMotaheda.Api.Tests.ApiCalls;

namespace AlMotaheda.Modules.Fleet.Tests.Api;

/// <summary>
/// Every Fleet endpoint over real HTTP without SQL Server: sign-in, permissions, project scope (404), validation
/// (400), and the rule checks that run before any save.
/// </summary>
public sealed class FleetApiSecurityTests
{
    private const string Executive = "executive";
    private const string Manager = "manager";
    private const string RowVersion = "AAAAAAAAB9E=";

    /// <summary>Every endpoint, with the permission it needs ("CE" = Create or Edit).</summary>
    public static TheoryData<string, string, string> Endpoints => new()
    {
        { "GET", "/api/equipment", "equipment:V" },
        { "GET", "/api/equipment/1", "equipment:V" },
        { "POST", "/api/equipment", "equipment:C" },
        { "PUT", "/api/equipment/1", "equipment:E" },
        { "POST", "/api/equipment/1/rentals", "equipment:E" },
        { "GET", "/api/projects/1/equipment", "equipment:V" },
        { "POST", "/api/projects/1/equipment", "equipment:E" },
        { "POST", "/api/projects/1/equipment/2/end", "equipment:E" },
        { "GET", "/api/projects/1/equipment-costs", "equipment:V" },
        { "GET", "/api/projects/1/equipment-costs/2", "equipment:V" },
        { "POST", "/api/projects/1/equipment-costs", "equipment:CE" },
        { "PUT", "/api/projects/1/equipment-costs/2", "equipment:CE" },
        { "POST", "/api/projects/1/equipment-costs/2/approve", "equipment:A" },
        { "POST", "/api/projects/1/equipment-costs/2/return", "equipment:A" },
        { "POST", "/api/projects/1/equipment-costs/2/reject", "equipment:A" },
        { "POST", "/api/projects/1/equipment-costs/2/payment", "payments:C" },
        { "POST", "/api/projects/1/equipment-costs/2/payment/void", "payments:A" },
        { "GET", "/api/projects/1/equipment-costs/2/voided-payments", "equipment:V" },
        { "GET", "/api/projects/1/equipment-summary", "equipment:F" },
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Requests_without_a_token_get_401(string method, string path, string permission)
    {
        _ = permission;
        await using var app = await TestApplication.StartAsync();

        var response = await Send(app, method, path, token: null, ValidBody(method, path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("unauthenticated", await CodeOf(response));
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Requests_without_the_permission_get_403(string method, string path, string permission)
    {
        await using var app = await TestApplication.StartAsync();
        // Every other permission of both modules, but not the one(s) this endpoint needs.
        var (module, letters) = (permission.Split(':')[0], permission.Split(':')[1]);
        var others = new string("VCEAF".Where(letter => !letters.Contains(letter)).ToArray());
        var token = TestTokens.Create(role: Manager, permissions:
            [(module, others), (module == "equipment" ? "payments" : "equipment", "VCEAF"), ("projects", "VCEAF")]);

        var response = await Send(app, method, path, token, ValidBody(method, path));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("forbidden", await CodeOf(response));
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task A_project_the_caller_cannot_see_answers_404_on_every_project_endpoint(string method, string path, string permission)
    {
        _ = permission;
        if (!path.StartsWith("/api/projects/", StringComparison.Ordinal))
        {
            return; // the register is company-wide
        }

        var (app, _) = await StartAsync();
        await using var __ = app;
        var token = TestTokens.Create(role: Manager, permissions: [("equipment", "VCEAF"), ("payments", "VCEAF"), ("projects", "VCEAF")]);

        var response = await Send(app, method, path, token, ValidBody(method, path));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", await CodeOf(response));
    }

    [Theory]
    [InlineData("GET", "/api/equipment/1")]
    [InlineData("PUT", "/api/equipment/1")]
    [InlineData("POST", "/api/equipment/1/rentals")]
    public async Task A_machine_that_is_not_in_the_register_answers_404(string method, string path)
    {
        var (app, _) = await StartAsync();
        await using var __ = app;
        var token = TestTokens.Create(role: Executive, permissions: [("equipment", "VCEAF")]);

        var response = await Send(app, method, path, token, ValidBody(method, path));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", await CodeOf(response));
    }

    [Theory]
    [InlineData("POST", "/api/equipment", "assetNo,category,condition,nameAr,nameEn,ownership,typeName")]
    [InlineData("PUT", "/api/equipment/1", "assetNo,category,condition,nameAr,nameEn,ownership,rowVersion,typeName")]
    [InlineData("POST", "/api/equipment/1/rentals", "rate,rateBasis,startDate,supplierId")]
    [InlineData("POST", "/api/projects/1/equipment", "equipmentId")]
    [InlineData("POST", "/api/projects/1/equipment/2/end", "rowVersion")]
    [InlineData("POST", "/api/projects/1/equipment-costs", "channel,costDate,equipmentId,type")]
    [InlineData("PUT", "/api/projects/1/equipment-costs/2", "channel,costDate,equipmentId,rowVersion,type")]
    [InlineData("POST", "/api/projects/1/equipment-costs/2/approve", "rowVersion")]
    [InlineData("POST", "/api/projects/1/equipment-costs/2/return", "note,rowVersion")]
    [InlineData("POST", "/api/projects/1/equipment-costs/2/reject", "note,rowVersion")]
    [InlineData("POST", "/api/projects/1/equipment-costs/2/payment", "method,paidOn,rowVersion")]
    [InlineData("POST", "/api/projects/1/equipment-costs/2/payment/void", "reason,rowVersion")]
    public async Task Empty_forms_list_their_required_fields(string method, string path, string fields)
    {
        var (app, database) = await StartAsync();
        await using var _ = app;
        var token = TestTokens.Create(role: Executive, permissions: [("equipment", "VCEAF"), ("payments", "VCEAF")]);

        var response = await Send(app, method, path, token, new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", await CodeOf(response));
        Assert.Equal(fields.Split(','), (await ErrorFields(response)).Order());
        Assert.Empty(database.Commands);
    }

    [Fact]
    public async Task A_fuel_cost_form_reports_every_failing_field_before_any_database_work()
    {
        var (app, database) = await StartAsync();
        await using var _ = app;
        var token = TestTokens.Create(role: Manager, permissions: [("equipment", "VE")]);

        var response = await Send(app, "POST", "/api/projects/1/equipment-costs", token, new
        {
            equipmentId = 0,
            costDate = "2999-01-01",
            type = "Fuel",
            basis = "Day",
            liters = -5,
            channel = "Credit",
            invoiceNo = new string('x', 51),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            ["basis", "costDate", "equipmentId", "invoiceNo", "liters", "pricePerLiter", "supplierId"],
            (await ErrorFields(response)).Order());
        Assert.Empty(database.Commands);
    }

    [Theory]
    [InlineData("Rental", "Custody")]
    [InlineData("Fuel", "Credit")]
    [InlineData("Repair", "Credit")]
    public async Task Rental_and_credit_costs_need_a_supplier(string type, string channel)
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: Manager, permissions: [("equipment", "VE")]);

        var response = await Send(app, "POST", "/api/projects/1/equipment-costs", token, CostBody(type, channel, supplierId: null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["supplierId"], await ErrorFields(response));
    }

    [Theory]
    [InlineData("C")]
    [InlineData("E")]
    public async Task Recording_a_cost_needs_equipment_create_or_edit_and_either_one_is_enough(string letter)
    {
        await using var app = await TestApplication.StartAsync();
        var viewer = TestTokens.Create(role: "engineer", permissions: [("equipment", "VAF")]);
        var recorder = TestTokens.Create(role: "engineer", permissions: [("equipment", letter)]);

        var refused = await Send(app, "POST", "/api/projects/1/equipment-costs", viewer, CostBody("Fuel", "Custody", null));
        var reached = await Send(app, "POST", "/api/projects/1/equipment-costs", recorder, new { });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, reached.StatusCode);
    }

    [Fact]
    public async Task A_rented_machine_is_registered_with_its_rental_terms_and_an_owned_one_without()
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: Executive, permissions: [("equipment", "VCEAF")]);

        var rentedWithout = await Send(app, "POST", "/api/equipment", token, EquipmentBody("Rented", rental: null));
        var ownedWith = await Send(app, "POST", "/api/equipment", token, EquipmentBody("Owned", rental: RentalBody()));
        var badTerms = await Send(app, "POST", "/api/equipment", token, EquipmentBody("Rented", rental: new { rateBasis = "Week", rate = 0 }));

        Assert.Equal(["rental"], await ErrorFields(rentedWithout));
        Assert.Equal(["rental"], await ErrorFields(ownedWith));
        Assert.Equal(["rental.rate", "rental.rateBasis", "rental.startDate", "rental.supplierId"], (await ErrorFields(badTerms)).Order());
    }

    [Theory]
    [InlineData("POST", "/api/projects/1/equipment")]
    [InlineData("POST", "/api/projects/1/equipment/2/end")]
    [InlineData("POST", "/api/projects/1/equipment-costs")]
    [InlineData("PUT", "/api/projects/1/equipment-costs/2")]
    [InlineData("POST", "/api/projects/1/equipment-costs/2/approve")]
    [InlineData("POST", "/api/projects/1/equipment-costs/2/reject")]
    [InlineData("POST", "/api/projects/1/equipment-costs/2/payment")]
    [InlineData("POST", "/api/projects/1/equipment-costs/2/payment/void")]
    public async Task A_completed_project_takes_no_change_from_anyone_but_an_executive(string method, string path)
    {
        var (app, _) = await StartAsync(new VisibleProject(IsCompleted: true, Executive: false));
        await using var __ = app;
        var token = TestTokens.Create(role: Manager, permissions: [("equipment", "VCEAF"), ("payments", "VCEAF")]);

        var response = await Send(app, method, path, token, ValidBody(method, path));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("project_completed", await CodeOf(response));
    }

    [Fact]
    public async Task An_executive_gets_past_the_completed_check_to_the_reference_checks()
    {
        var (app, database) = await StartAsync(new VisibleProject(IsCompleted: true, Executive: true));
        await using var _ = app;
        var token = TestTokens.Create(role: Executive, permissions: [("equipment", "VCEAF")]);

        // The stand-in database knows no machine and no supplier.
        var response = await Send(app, "POST", "/api/projects/1/equipment-costs", token, CostBody("Rental", "Credit", supplierId: 4));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["equipmentId", "supplierId"], (await ErrorFields(response)).Order());
        Assert.Contains(database.Commands, sql => sql.Contains("[fleet].[Equipment]", StringComparison.Ordinal));
        Assert.Contains(database.Commands, sql => sql.Contains("[projects].[Supplier]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Assigning_a_machine_that_is_not_in_the_register_answers_400_before_any_lock()
    {
        var (app, database) = await StartAsync(new VisibleProject(IsCompleted: false, Executive: false));
        await using var _ = app;
        var token = TestTokens.Create(role: Manager, permissions: [("equipment", "VE")]);

        var response = await Send(app, "POST", "/api/projects/1/equipment", token, ValidBody("POST", "/api/projects/1/equipment"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["equipmentId"], await ErrorFields(response));
        Assert.DoesNotContain(database.Commands, sql => sql.Contains("sp_getapplock", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Records_of_a_project_are_read_with_the_project_id_in_the_sql()
    {
        var (app, database) = await StartAsync(new VisibleProject(IsCompleted: false, Executive: true));
        await using var _ = app;
        var token = TestTokens.Create(role: Executive, permissions: [("equipment", "VCEAF")]);

        var costs = await Send(app, "GET", "/api/projects/1/equipment-costs?type=Fuel&status=Approved&equipmentId=3&from=2026-01-01&to=2026-12-31", token);
        var summary = await Send(app, "GET", "/api/projects/1/equipment-summary", token);
        var machines = await Send(app, "GET", "/api/projects/1/equipment?includeEnded=true", token);
        var missingCost = await Send(app, "GET", "/api/projects/1/equipment-costs/2", token);

        Assert.Equal(HttpStatusCode.OK, costs.StatusCode);
        Assert.Equal(HttpStatusCode.OK, summary.StatusCode);
        Assert.Equal(HttpStatusCode.OK, machines.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingCost.StatusCode);

        var body = await Json(summary);
        Assert.Equal(0m, body.GetProperty("toDate").GetProperty("total").GetDecimal());
        Assert.Equal(0, body.GetProperty("pendingReviewCount").GetInt32());
        Assert.Empty(body.GetProperty("machines").EnumerateArray());

        Assert.All(
            database.Commands.Where(sql => sql.Contains("[fleet].[EquipmentCost]", StringComparison.Ordinal)
                                           || sql.Contains("[fleet].[EquipmentAssignment]", StringComparison.Ordinal)),
            sql => Assert.Contains("[ProjectId] = @", sql));
    }

    [Fact]
    public async Task Without_see_money_the_cost_list_is_limited_to_the_callers_own_records_in_sql()
    {
        var (app, database) = await StartAsync(new VisibleProject(IsCompleted: false, Executive: false));
        await using var _ = app;
        var token = TestTokens.Create(userId: 42, role: Manager, permissions: [("equipment", "VEA")]);

        var response = await Send(app, "GET", "/api/projects/1/equipment-costs", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var count = Assert.Single(database.Commands, sql => sql.TrimStart().StartsWith("SELECT COUNT(*)", StringComparison.Ordinal));
        Assert.Contains("[CreatedBy] = @", count);
    }

    [Fact]
    public async Task Register_search_text_is_sent_as_a_parameter_never_inside_the_sql()
    {
        var (app, database) = await StartAsync();
        await using var _ = app;
        var token = TestTokens.Create(role: "engineer", permissions: [("equipment", "V")]);

        var response = await Send(app, "GET", "/api/equipment?search=x'%20OR%201=1--&category=heavy&ownership=rented&available=true", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEmpty(database.Commands);
        Assert.All(database.Commands, sql => Assert.DoesNotContain("OR 1=1", sql));
    }

    [Fact]
    public async Task Equipment_fuel_and_rental_can_not_be_recorded_as_a_project_expense()
    {
        // No double counting: the expense form of the Projects module has no equipment, fuel or rental category,
        // so equipment costs can only be recorded here.
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: "engineer", permissions: [("projects", "VC")]);

        foreach (var category in new[] { "Fuel", "Equipment", "EquipmentRental", "Rental", "Maintenance" })
        {
            var response = await Send(app, "POST", "/api/projects/1/expenses", token, new
            {
                kind = "Expense",
                expenseDate = "2026-01-15",
                category,
                description = "Diesel for the excavator",
                amount = 450,
                channel = "Custody",
            });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(["category"], await ErrorFields(response));
        }
    }

    [Theory]
    [InlineData("/api/equipment?category=Cars", "category")]
    [InlineData("/api/equipment?ownership=Leased", "ownership")]
    [InlineData("/api/equipment?status=Assigned", "status")]
    [InlineData("/api/equipment?pageSize=101", "pageSize")]
    [InlineData("/api/projects/1/equipment?page=0", "page")]
    [InlineData("/api/projects/1/equipment-costs?type=Purchase", "type")]
    [InlineData("/api/projects/1/equipment-costs?status=Paid", "status")]
    [InlineData("/api/projects/1/equipment-costs?equipmentId=0", "equipmentId")]
    [InlineData("/api/projects/1/equipment-costs?from=2026-13-01", "from")]
    [InlineData("/api/projects/1/equipment-costs?from=2026-02-01&to=2026-01-01", "to")]
    public async Task List_parameters_are_validated(string path, string field)
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: Executive, permissions: [("equipment", "VCEAF")]);

        var response = await Send(app, "GET", path, token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, await ErrorFields(response));
    }

    /// <summary>A body that passes validation for each endpoint, so tests reach the checks after it.</summary>
    internal static object? ValidBody(string method, string path)
    {
        if (method == "GET")
        {
            return null;
        }

        if (path.EndsWith("/payment/void", StringComparison.Ordinal))
        {
            return new { reason = "Entered twice", rowVersion = RowVersion };
        }

        if (path.EndsWith("/payment", StringComparison.Ordinal))
        {
            return new { paidOn = "2026-01-20", method = "BankTransfer", reference = "TRF-1", rowVersion = RowVersion };
        }

        if (path.EndsWith("/approve", StringComparison.Ordinal) || path.EndsWith("/return", StringComparison.Ordinal)
            || path.EndsWith("/reject", StringComparison.Ordinal))
        {
            return new { note = "Attach the fuel receipt", rowVersion = RowVersion };
        }

        if (path.Contains("/equipment-costs", StringComparison.Ordinal))
        {
            // Fixing a cost also sends the rowVersion of the last read.
            return method == "PUT"
                ? new { equipmentId = 3, costDate = "2026-01-15", type = "Fuel", liters = 182.5m, pricePerLiter = 15.25m, channel = "Custody", rowVersion = RowVersion }
                : CostBody("Fuel", "Custody", supplierId: null);
        }

        if (path.EndsWith("/end", StringComparison.Ordinal))
        {
            return new { endDate = "2026-01-20", rowVersion = RowVersion };
        }

        if (path.StartsWith("/api/projects/", StringComparison.Ordinal))
        {
            return new { equipmentId = 3, operatorName = "Hany Wagdy", startDate = "2026-01-05" };
        }

        if (path.EndsWith("/rentals", StringComparison.Ordinal))
        {
            return RentalBody();
        }

        return EquipmentBody("Owned", rental: null);
    }

    private static object CostBody(string type, string channel, int? supplierId) => type == "Fuel"
        ? new { equipmentId = 3, costDate = "2026-01-15", type, liters = 182.5m, pricePerLiter = 15.25m, channel, supplierId }
        : new { equipmentId = 3, costDate = "2026-01-15", type, basis = "Day", quantity = 15, rate = 3500, description = "Rental 1-15 Jan", channel, supplierId };

    private static object RentalBody() => new { supplierId = 4, rateBasis = "Day", rate = 3500, startDate = "2026-01-01" };

    private static object EquipmentBody(string ownership, object? rental) => new
    {
        assetNo = "ex-104",
        nameAr = "حفار EX-104",
        nameEn = "Excavator EX-104",
        category = "Heavy",
        typeName = "Excavator",
        serialNo = "CAT320-2019-7741",
        ownership,
        condition = "Good",
        rental,
        rowVersion = RowVersion,
    };

    private static async Task<(TestApplication App, StandInDatabase Database)> StartAsync(IProjectAccess? access = null)
    {
        var database = new StandInDatabase();
        var app = await TestApplication.StartAsync(configureBuilder: builder =>
        {
            database.AddTo(builder);
            if (access is not null)
            {
                builder.Services.AddScoped(_ => access);
            }
        });
        return (app, database);
    }

    /// <summary>Says every project is visible, so tests reach the rules that come after the scope check.</summary>
    private sealed record VisibleProject(bool IsCompleted, bool Executive) : IProjectAccess
    {
        public Task<ProjectAccessInfo?> FindVisibleAsync(ClaimsPrincipal caller, int projectId, CancellationToken cancellationToken) =>
            Task.FromResult<ProjectAccessInfo?>(new ProjectAccessInfo(projectId, "P-1", "مشروع", "Project", IsCompleted, null, Executive));
    }
}
