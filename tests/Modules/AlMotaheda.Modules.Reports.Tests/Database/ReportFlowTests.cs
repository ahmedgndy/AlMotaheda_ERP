using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Projects.Tests.Database;
using AlMotaheda.Modules.Projects.Tests.Database.Core;
using ClosedXML.Excel;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;
using static AlMotaheda.Modules.Projects.Tests.Database.Core.CoreApi;

namespace AlMotaheda.Modules.Reports.Tests.Database;

/// <summary>
/// Reports on SQL Server: their totals are the ledger's and the screens' totals; Excel, print and a background export
/// (announced by a notification) all carry the whole filtered report.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class ReportFlowTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task Reports_reconcile_with_the_ledger_and_the_screens_and_export_in_full()
    {
        // Above one row, an Excel export is built in the background; the worker looks every second.
        await using var api = await ProjectsApi.StartAsync(database, new Dictionary<string, string?>
        {
            ["Reports:ImmediateExportRowLimit"] = "1",
            ["Reports:PollSeconds"] = "1",
        });
        var cast = await api.CastAsync("RPT-001");
        var cemex = await api.CreateSupplierAsync(cast.Executive, "Cemex");
        var first = await api.RecordApprovedAsync(cast.Engineer, cast.Accountant, cast.ProjectId, SupplyBody(cemex, 10m, 100m, "Credit"));
        var second = await api.RecordApprovedAsync(cast.Engineer, cast.Accountant, cast.ProjectId, SupplyBody(cemex, 5m, 100m, "Credit"));
        await api.PaidAsync(cast.Accountant, cast.ProjectId, cemex, 1_000m, (first.Id(), 1_000m));
        await api.PaidAsync(cast.Accountant, cast.ProjectId, cemex, 300m, (second.Id(), 300m));

        // The ledger report's total is the ledger's total.
        const string january = "from=2026-01-01&to=2026-01-31";
        var ledger = await Get(api, cast.Accountant, $"/api/ledger?{january}");
        var ledgerReport = await Get(api, cast.Accountant, $"/api/reports/FIN-LEDGER?{january}");
        Assert.Equal(2, ledgerReport.GetProperty("totalCount").GetInt32());
        Assert.Equal(ledger.GetProperty("totals").Money("net"), ledgerReport.GetProperty("totals").Money("amount"));
        Assert.Equal(-1_300m, ledgerReport.GetProperty("totals").Money("amount"));
        Assert.Equal("Cemex", ledgerReport.GetProperty("rows")[0].GetProperty("cells").GetProperty("counterparty").GetString());

        var cash = await Get(api, cast.Accountant, $"/api/reports/FIN-CASH?{january}&period=Month");
        var month = Assert.Single(cash.GetProperty("rows").EnumerateArray()).GetProperty("cells");
        Assert.Equal(("2026-01", 1_300m, -1_300m), (month.GetProperty("period").GetString(), month.Money("moneyOut"), month.Money("net")));

        var recon = await Get(api, cast.Accountant, $"/api/reports/FIN-RECON?{january}");
        Assert.All(recon.GetProperty("rows").EnumerateArray(), row => Assert.Equal("Matched", row.GetProperty("cells").GetProperty("result").GetString()));
        Assert.Equal(0m, recon.GetProperty("totals").Money("difference"));

        // The supplier account is the supplier balances screen; the cost summary is the cost summary screen.
        var balances = await api.BalancesAsync(cast.Accountant, cast.ProjectId);
        var account = await Get(api, cast.Accountant, $"/api/reports/SUP-ACCOUNT?projectId={cast.ProjectId}");
        Assert.Equal((balances.Money("paid"), balances.Money("outstanding")), (account.GetProperty("totals").Money("paid"), account.GetProperty("totals").Money("outstanding")));
        Assert.Equal((1_300m, 200m), (account.GetProperty("totals").Money("paid"), account.GetProperty("totals").Money("outstanding")));
        var summary = await Get(api, cast.Accountant, $"/api/projects/{cast.ProjectId}/reports/cost-summary");
        var costReport = await Get(api, cast.Accountant, $"/api/reports/PRJ-COST?projectId={cast.ProjectId}");
        Assert.Equal(summary.GetProperty("totals").Money("recognized"), costReport.GetProperty("totals").Money("recognized"));

        // A project the caller can't see answers like a missing report.
        var (_, stranger) = await api.CreateSignedInUserAsync(cast.Executive, "manager");
        Assert.Equal(HttpStatusCode.NotFound, (await api.SendAsync(HttpMethod.Get, $"/api/reports/PRJ-COST?projectId={cast.ProjectId}", stranger)).StatusCode);

        // One row: the Excel file comes back at once, right-to-left, with the header and the totals.
        var excel = await api.SendAsync(HttpMethod.Get, $"/api/reports/FIN-CASH/excel?{january}&period=Month", cast.Accountant);
        await EnsureStatus(excel, HttpStatusCode.OK);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", excel.Content.Headers.ContentType!.MediaType);
        using (var workbook = new XLWorkbook(await excel.Content.ReadAsStreamAsync()))
        {
            var sheet = workbook.Worksheet(1);
            Assert.True(sheet.RightToLeft);
            Assert.Equal("حركة النقدية (يومي / أسبوعي / شهري)", sheet.Cell(1, 1).GetString());
            Assert.Equal(1_300m, sheet.Cell(6, 5).GetValue<decimal>());
        }

        // The print page.
        var print = await api.SendAsync(HttpMethod.Get, $"/api/reports/FIN-LEDGER/print?{january}&lang=en", cast.Accountant);
        await EnsureStatus(print, HttpStatusCode.OK);
        var html = await print.Content.ReadAsStringAsync();
        Assert.Contains("dir=\"ltr\"", html);
        Assert.Contains("Transaction ledger", html);
        Assert.Contains("-1,300.00", html);

        // Two rows: built in the background; the requester is told and downloads it; nobody else can.
        var queued = await api.SendAsync(HttpMethod.Get, $"/api/reports/FIN-LEDGER/excel?{january}", cast.Accountant);
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        var exportId = (await Json(queued)).GetProperty("exportId").GetInt64();
        var ready = await WaitAsync(async () =>
        {
            var runs = await Get(api, cast.Accountant, "/api/report-exports");
            return runs.GetProperty("items").EnumerateArray().FirstOrDefault(run => run.GetProperty("id").GetInt64() == exportId && run.GetProperty("downloadable").GetBoolean());
        });
        Assert.Equal(2, ready.GetProperty("rowCount").GetInt32());

        Assert.Equal(HttpStatusCode.NotFound, (await api.SendAsync(HttpMethod.Get, $"/api/report-exports/{exportId}/file", cast.Executive)).StatusCode);
        var file = await api.SendAsync(HttpMethod.Get, $"/api/report-exports/{exportId}/file", cast.Accountant);
        await EnsureStatus(file, HttpStatusCode.OK);
        using (var workbook = new XLWorkbook(await file.Content.ReadAsStreamAsync()))
        {
            Assert.Equal(-1_300m, workbook.Worksheet(1).Cell(8, 11).GetValue<decimal>()); // the totals row after two rows; amount is the 11th column (after "kind")
        }

        var notice = await WaitAsync(async () =>
            (await Get(api, cast.Accountant, "/api/notifications")).GetProperty("items").EnumerateArray()
                .FirstOrDefault(item => item.GetProperty("type").GetString() == "ExportReady"));
        Assert.Equal("Your report export is ready to download", notice.GetProperty("titleEn").GetString());

        // Every export and print is on record: the immediate file, the print and the background export.
        var history = (await Get(api, cast.Accountant, "/api/report-exports")).GetProperty("items").EnumerateArray().Select(run => run.GetProperty("kind").GetString());
        Assert.Equal(["BackgroundExcel", "Print", "Excel"], history);
    }

    private static async Task<JsonElement> Get(ProjectsApi api, Session session, string path)
    {
        var response = await api.SendAsync(HttpMethod.Get, path, session);
        await EnsureStatus(response, HttpStatusCode.OK);
        return await Json(response);
    }

    private static async Task<JsonElement> WaitAsync(Func<Task<JsonElement>> probe)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var found = await probe();
            if (found.ValueKind != JsonValueKind.Undefined)
            {
                return found;
            }

            await Task.Delay(500);
        }

        throw new TimeoutException("Waited 30 seconds for the background export.");
    }
}
