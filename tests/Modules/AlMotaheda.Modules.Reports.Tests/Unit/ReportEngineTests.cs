using AlMotaheda.BuildingBlocks.Validation;
using AlMotaheda.Modules.Reports.Contracts;
using AlMotaheda.Modules.Reports.Engine;
using ClosedXML.Excel;

namespace AlMotaheda.Modules.Reports.Tests.Unit;

/// <summary>Filters, totals, the Excel file and the print page, with no database.</summary>
public sealed class ReportEngineTests
{
    private static readonly ReportDefinition Definition = new(
        "TST-ONE", "تقرير تجريبي", "Test report", ReportCategories.Finance, [new("finance", ReportActions.SeeMoney)],
        [
            new("from", ReportFilterTypes.Date, true, "من", "From"),
            new("to", ReportFilterTypes.Date, false, "إلى", "To"),
            new("projectId", ReportFilterTypes.Id, false, "المشروع", "Project"),
            new("month", ReportFilterTypes.Month, false, "الشهر", "Month"),
            new("period", ReportFilterTypes.Option, false, "الفترة", "Period", ["Day", "Week"]),
        ],
        [
            new("name", "الاسم", "Name", ReportColumnTypes.Text),
            new("date", "التاريخ", "Date", ReportColumnTypes.Date),
            new("amount", "المبلغ", "Amount", ReportColumnTypes.Money, Total: true),
            new("count", "العدد", "Count", ReportColumnTypes.Integer, Total: true),
        ]);

    private static readonly ReportData Data = new(
    [
        new ReportRow(new Dictionary<string, object?> { ["name"] = "<b>شركة</b>", ["date"] = new DateOnly(2026, 10, 1), ["amount"] = 1_500.25m, ["count"] = 2 }),
        new ReportRow(new Dictionary<string, object?> { ["name"] = "00123", ["date"] = null, ["amount"] = -500m, ["count"] = 1 }),
    ], "A note");

    [Fact]
    public void Filters_are_read_by_type_and_every_problem_is_reported()
    {
        var errors = new ValidationErrors();
        var query = ReportFilters.Parse(Definition, new Dictionary<string, string?>
        {
            ["from"] = "2026-10-10", ["to"] = "2026-10-01", ["projectId"] = "x", ["month"] = "2026-13", ["period"] = "Year",
        }, new DateOnly(2026, 10, 16), errors);

        Assert.Null(query);
        Assert.Equal(["month", "period", "projectId", "to"], errors.ToDictionary().Keys.Order());
    }

    [Fact]
    public void Valid_filters_become_typed_values_and_unknown_ones_are_ignored()
    {
        var errors = new ValidationErrors();
        var query = ReportFilters.Parse(Definition, new Dictionary<string, string?>
        {
            ["from"] = "2026-10-01", ["projectId"] = "7", ["month"] = "2026-09", ["period"] = "week", ["other"] = "anything",
        }, new DateOnly(2026, 10, 16), errors);

        Assert.False(errors.HasErrors);
        Assert.Equal((new DateOnly(2026, 10, 1), 7, new DateOnly(2026, 9, 1), "Week"), (query!.Date("from")!.Value, query.Id("projectId")!.Value, query.Date("month")!.Value, query.Text("period")));
        Assert.Null(query.Date("to"));
    }

    [Fact]
    public void A_required_filter_is_required()
    {
        var errors = new ValidationErrors();

        Assert.Null(ReportFilters.Parse(Definition, new Dictionary<string, string?>(), new DateOnly(2026, 10, 16), errors));
        Assert.Equal(["from"], errors.ToDictionary().Keys);
    }

    [Fact]
    public void Totals_add_only_the_totaled_columns()
    {
        var totals = ReportTotals.Of(Definition, Data.Rows);

        Assert.Equal(["amount", "count"], totals.Keys.Order());
        Assert.Equal((1_000.25m, 3m), ((decimal)totals["amount"]!, Convert.ToDecimal(totals["count"])));
    }

    [Fact]
    public void The_excel_file_is_right_to_left_with_a_header_typed_cells_and_a_totals_row()
    {
        var document = new ReportDocument(Definition, "ar", new Dictionary<string, string> { ["from"] = "2026-10-01" }, "مدير", new DateTime(2026, 10, 16, 8, 0, 0, DateTimeKind.Utc),
            TimeZoneInfo.FindSystemTimeZoneById("Africa/Cairo"), Data);

        using var workbook = new XLWorkbook(new MemoryStream(ExcelReport.Write(document)));
        var sheet = workbook.Worksheet(1);

        Assert.True(sheet.RightToLeft);
        Assert.Equal("تقرير تجريبي", sheet.Cell(1, 1).GetString());
        Assert.Contains("من: 2026-10-01", sheet.Cell(2, 1).GetString());
        Assert.Contains("مدير", sheet.Cell(3, 1).GetString());
        Assert.Equal("المبلغ", sheet.Cell(5, 3).GetString());
        Assert.Equal(1_500.25m, sheet.Cell(6, 3).GetValue<decimal>());
        Assert.Equal(new DateTime(2026, 10, 1), sheet.Cell(6, 2).GetDateTime());
        Assert.Equal("00123", sheet.Cell(7, 1).GetString()); // text keeps its leading zeros
        Assert.Equal(("الإجمالي", 1_000.25m), (sheet.Cell(8, 1).GetString(), sheet.Cell(8, 3).GetValue<decimal>()));
        Assert.Equal("A note", sheet.Cell(10, 1).GetString());
        Assert.Equal("TST-ONE_20261016_1100.xlsx", document.FileName); // company time (Cairo, UTC+3 in October)
    }

    [Fact]
    public void The_print_page_is_rtl_escaped_and_has_the_totals()
    {
        var document = new ReportDocument(Definition, "ar", new Dictionary<string, string>(), "مدير", DateTime.UtcNow, TimeZoneInfo.Utc, Data);

        var html = PrintReport.Write(document);

        Assert.Contains("dir=\"rtl\"", html);
        Assert.Contains("&lt;b&gt;شركة&lt;/b&gt;", html);
        Assert.DoesNotContain("<b>شركة</b>", html);
        Assert.Contains("1,000.25", html);
        Assert.Contains("بدون عوامل تصفية", html);
    }
}
