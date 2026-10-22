using System.Net;
using AlMotaheda.Api.Tests;
using AlMotaheda.Modules.Access.Tests;
using static AlMotaheda.Api.Tests.ApiCalls;

namespace AlMotaheda.Modules.Fleet.Tests.Api;

/// <summary>
/// Every vehicle endpoint over real HTTP without SQL Server: sign-in, the permission each one needs (fleet for the
/// register, drivers, violations and custody; fuel for claims), validation, and the GPS answer with no provider.
/// </summary>
public sealed class VehicleApiSecurityTests
{
    private const string RowVersion = "AAAAAAAAB9E=";

    /// <summary>Every endpoint, with the module and the letters that satisfy it ("CA" = Create or Approve).</summary>
    public static TheoryData<string, string, string> Endpoints => new()
    {
        { "GET", "/api/vehicles", "fleet:V" },
        { "GET", "/api/vehicles/1", "fleet:V" },
        { "POST", "/api/vehicles", "fleet:C" },
        { "PUT", "/api/vehicles/1", "fleet:E" },
        { "POST", "/api/vehicles/1/odometer", "fleet:E" },
        { "GET", "/api/vehicles/1/drivers", "fleet:V" },
        { "POST", "/api/vehicles/1/drivers", "fleet:E" },
        { "POST", "/api/vehicles/1/drivers/2/end", "fleet:E" },
        { "GET", "/api/vehicles/1/custody", "fleet:V" },
        { "GET", "/api/vehicles/1/location", "fleet:V" },
        { "GET", "/api/vehicles/1/location-history", "fleet:V" },
        { "GET", "/api/vehicles/1/trips", "fleet:V" },
        { "GET", "/api/fleet/gps-status", "fleet:V" },
        { "GET", "/api/fleet/attachments/1/content", "fleet:V;fuel:V" },
        { "GET", "/api/vehicle-expenses", "fuel:V" },
        { "GET", "/api/vehicle-expenses/1", "fuel:V" },
        { "POST", "/api/vehicle-expenses", "fuel:C" },
        { "POST", "/api/vehicle-expenses/1/resubmit", "fuel:C" },
        { "POST", "/api/vehicle-expenses/1/approve", "fuel:A" },
        { "POST", "/api/vehicle-expenses/1/reject", "fuel:A" },
        { "POST", "/api/vehicle-expenses/1/request-evidence", "fuel:A" },
        { "POST", "/api/vehicle-expenses/1/escalate", "fuel:A" },
        { "POST", "/api/vehicle-expenses/1/reverse", "fuel:A" },
        { "POST", "/api/vehicle-expenses/1/refer-to-hr", "fuel:A;fleet:A" },
        { "GET", "/api/vehicle-violations", "fleet:V" },
        { "GET", "/api/vehicle-violations/1", "fleet:V" },
        { "POST", "/api/vehicle-violations", "fleet:C" },
        { "POST", "/api/vehicle-violations/1/review", "fleet:A" },
        { "POST", "/api/vehicle-violations/1/refer-to-hr", "fleet:A" },
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Requests_without_a_token_get_401(string method, string path, string permission)
    {
        _ = permission;
        await using var app = await TestApplication.StartAsync();

        var response = await Send(app, method, path, token: null, method == "GET" ? null : Body(path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("unauthenticated", await CodeOf(response));
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Requests_without_the_permission_get_403(string method, string path, string permission)
    {
        await using var app = await TestApplication.StartAsync();

        // Every other letter of every named module, but none of the letters the endpoint accepts.
        var needs = permission.Split(';').Select(part => (Module: part.Split(':')[0], Letters: part.Split(':')[1])).ToList();
        var permissions = new[] { "fleet", "fuel", "equipment", "payments", "hr" }
            .Select(module => (module, new string("VCEAF".Where(letter => !needs.Any(need => need.Module == module && need.Letters.Contains(letter))).ToArray())))
            .Where(entry => entry.Item2.Length > 0)
            .ToArray();
        var token = TestTokens.Create(role: "staff", permissions: permissions);

        var response = await Send(app, method, path, token, method == "GET" ? null : Body(path));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("forbidden", await CodeOf(response));
    }

    [Theory]
    [InlineData("GET", "/api/vehicles/1")]
    [InlineData("GET", "/api/vehicles/1/custody")]
    [InlineData("GET", "/api/vehicles/1/location")]
    [InlineData("GET", "/api/vehicles/1/trips")]
    [InlineData("GET", "/api/vehicle-expenses/1")]
    [InlineData("GET", "/api/vehicle-violations/1")]
    [InlineData("GET", "/api/fleet/attachments/1/content")]
    public async Task A_record_that_does_not_exist_answers_404(string method, string path)
    {
        var (app, _) = await StartAsync();
        await using var __ = app;
        var token = TestTokens.Create(role: "executive", permissions: [("fleet", "VCEAF"), ("fuel", "VCEAF")]);

        var response = await Send(app, method, path, token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", await CodeOf(response));
    }

    [Fact]
    public async Task With_no_provider_the_gps_status_says_so_and_invents_nothing()
    {
        var (app, _) = await StartAsync();
        await using var _ = app;
        var token = TestTokens.Create(role: "staff", permissions: [("fleet", "V")]);

        var response = await Send(app, "GET", "/api/fleet/gps-status", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(("None", false, 0), (body.GetProperty("provider").GetString(), body.GetProperty("configured").GetBoolean(), body.GetProperty("trackedVehicles").GetInt32()));
        Assert.Contains("No vehicle GPS provider", body.GetProperty("note").GetString());
    }

    [Theory]
    [InlineData("POST", "/api/vehicles", "{\"plate\":\"\",\"type\":\"Boat\",\"fuelType\":\"Coal\",\"modelYear\":1900,\"odometerKm\":-1}", new[] { "assetNo", "fuelType", "modelYear", "nameAr", "nameEn", "odometerKm", "plate", "type" })]
    [InlineData("POST", "/api/vehicles/1/drivers", "{\"startDate\":\"2999-01-01\"}", new[] { "driverEmployeeId", "startDate" })]
    [InlineData("POST", "/api/vehicles/1/odometer", "{}", new[] { "odometerKm", "rowVersion" })]
    [InlineData("POST", "/api/vehicle-expenses", "{\"type\":\"Fuel\",\"expenseDate\":\"2999-01-01\",\"fuelQuantity\":0}", new[] { "expenseDate", "fuelQuantity", "idempotencyKey", "invoiceAttachmentId", "odometerAttachmentId", "unitPrice", "vehicleId" })]
    [InlineData("POST", "/api/vehicle-expenses", "{\"vehicleId\":1,\"type\":\"Toll\",\"expenseDate\":\"2026-10-01\",\"odometerKm\":10,\"fuelQuantity\":5,\"priceItemId\":3,\"odometerAttachmentId\":1,\"invoiceAttachmentId\":2,\"idempotencyKey\":\"7e1e1e1e-1e1e-1e1e-1e1e-1e1e1e1e1e1e\"}", new[] { "amount", "fuelQuantity", "priceItemId" })]
    [InlineData("POST", "/api/vehicle-expenses/1/reject", "{}", new[] { "note", "rowVersion" })]
    [InlineData("POST", "/api/vehicle-expenses/1/reverse", "{}", new[] { "reason", "rowVersion" })]
    [InlineData("POST", "/api/vehicle-violations", "{\"category\":\"Jaywalking\",\"gpsLatitude\":1}", new[] { "category", "description", "gpsLatitude", "vehicleId", "violationDate" })]
    [InlineData("POST", "/api/vehicle-violations/1/review", "{\"outcome\":\"Recorded\"}", new[] { "note", "outcome", "rowVersion" })]
    public async Task A_bad_form_lists_every_failing_field_before_any_database_work(string method, string path, string json, string[] fields)
    {
        var (app, database) = await StartAsync();
        await using var _ = app;
        var token = TestTokens.Create(role: "executive", permissions: [("fleet", "VCEAF"), ("fuel", "VCEAF")]);

        var response = await Send(app, method, path, token, System.Text.Json.JsonDocument.Parse(json).RootElement);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(fields.Order(), (await ErrorFields(response)).Order());
        Assert.Empty(database.Commands);
    }

    [Fact]
    public async Task Claims_without_see_money_or_approve_are_limited_to_the_callers_own_in_sql()
    {
        var (app, database) = await StartAsync();
        await using var _ = app;
        var token = TestTokens.Create(userId: 42, role: "staff", permissions: [("fuel", "VC")]);

        var response = await Send(app, "GET", "/api/vehicle-expenses", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var count = Assert.Single(database.Commands, sql => sql.TrimStart().StartsWith("SELECT COUNT(*)", StringComparison.Ordinal));
        Assert.Contains("[CreatedBy] = @", count);
    }

    [Fact]
    public async Task An_upload_must_be_multipart()
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: "staff", permissions: [("fuel", "VC")]);

        var response = await Send(app, "POST", "/api/fleet/attachments", token, new { purpose = "Odometer" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["file"], await ErrorFields(response));
    }

    private static object Body(string path) => path.EndsWith("/attachments", StringComparison.Ordinal)
        ? new { }
        : new { note = "OK", reason = "Because", details = "Because", outcome = "Resolved", rowVersion = RowVersion };

    private static async Task<(TestApplication App, StandInDatabase Database)> StartAsync()
    {
        var database = new StandInDatabase();
        var app = await TestApplication.StartAsync(configureBuilder: builder => database.AddTo(builder));
        return (app, database);
    }
}
