using AlMotaheda.Modules.Projects.Data;

namespace AlMotaheda.Modules.Projects.Tests.Unit.Core;

/// <summary>
/// The workflow records change status only through their methods, and the methods refuse moves the flow doesn't
/// allow, even if an endpoint forgot to check (refactor plan M5).
/// </summary>
public sealed class StatusMethodsTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_reviewed_expense_records_who_and_when_and_an_approved_one_cant_be_reviewed_or_resubmitted()
    {
        var expense = new ProjectExpense { CreatedBy = 1 };

        expense.Review(ExpenseStatus.Approved, reviewerId: 2, Now, "OK");

        Assert.Equal((ExpenseStatus.Approved, 2, "OK", Now), (expense.Status, expense.ReviewedBy!.Value, expense.ReviewNote, expense.ReviewedAt!.Value));
        Assert.Throws<InvalidOperationException>(() => expense.Review(ExpenseStatus.Rejected, 2, Now, "late"));
        Assert.Throws<InvalidOperationException>(() => expense.Resubmit(1, Now));
    }

    [Fact]
    public void A_returned_expense_is_resubmitted_with_its_old_review_cleared()
    {
        var expense = new ProjectExpense { CreatedBy = 1 };
        expense.Review(ExpenseStatus.Returned, 2, Now, "Attach the delivery note");

        expense.Resubmit(1, Now.AddHours(1));

        Assert.Equal(ExpenseStatus.Recorded, expense.Status);
        Assert.Null(expense.ReviewedBy);
        Assert.Null(expense.ReviewNote);
    }

    [Fact]
    public void A_budget_is_approved_once_and_only_an_approved_one_is_superseded()
    {
        var draft = new Budget();
        Assert.Throws<InvalidOperationException>(() => draft.Supersede(1, Now));

        draft.Approve(1, Now);
        Assert.Throws<InvalidOperationException>(() => draft.Approve(1, Now));

        draft.Supersede(1, Now);
        Assert.Equal(BudgetStatus.Superseded, draft.Status);
    }

    [Fact]
    public void A_price_change_is_decided_once()
    {
        var price = new ItemPrice();

        price.Decide(ItemPriceStatus.Rejected, 3, Now, "Too high");

        Assert.Equal(ItemPriceStatus.Rejected, price.Status);
        Assert.Throws<InvalidOperationException>(() => price.Decide(ItemPriceStatus.Approved, 3, Now, null));
    }

    [Fact]
    public void A_report_request_follows_its_flow()
    {
        var request = new ReportRequest { Title = "Weekly" };

        request.MoveTo(ReportRequestStatus.InProgress);
        request.MoveTo(ReportRequestStatus.Submitted);
        Assert.Throws<InvalidOperationException>(() => request.MoveTo(ReportRequestStatus.InProgress));
        request.MoveTo(ReportRequestStatus.Completed);
        Assert.Throws<InvalidOperationException>(() => request.MoveTo(ReportRequestStatus.Cancelled));
    }
}
