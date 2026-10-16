using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Finance.Bank;

/// <summary>
/// Sends a payment order to the bank. Today only <see cref="MockBankPaymentProvider"/> exists; the real bank (NBE)
/// integration replaces it behind this interface.
/// </summary>
internal interface IBankPaymentProvider
{
    Task<BankSubmission> SubmitAsync(BankOrder order, CancellationToken cancellationToken);
}

/// <summary>What the bank is asked to pay. Payee bank details are not in the system yet (Needs Confirmation).</summary>
internal sealed record BankOrder(string RequestNumber, string Method, decimal Amount, string PayeeType, int PayeeId, DateOnly RequestedDate);

/// <param name="Accepted">False when the bank refused the order at once; <paramref name="Message"/> says why.</param>
internal sealed record BankSubmission(bool Accepted, string? BankReference, string? Message);

/// <summary>
/// Accepts every order and answers with the reference <c>MOCK-&lt;request number&gt;</c>. The payment is still only
/// Paid when a person confirms it, as with the real bank until it sends confirmations itself.
/// </summary>
internal sealed class MockBankPaymentProvider : IBankPaymentProvider
{
    public Task<BankSubmission> SubmitAsync(BankOrder order, CancellationToken cancellationToken) =>
        Task.FromResult(new BankSubmission(true, $"MOCK-{order.RequestNumber}", null));
}

/// <summary>Settings of the bank connection (<c>Finance:Bank</c>).</summary>
internal sealed class BankOptions
{
    public const string SectionName = "Finance:Bank";
    public const string MockProvider = "Mock";

    /// <summary>Which provider sends payment orders. Only <c>Mock</c> exists today.</summary>
    public string Provider { get; set; } = MockProvider;
}

internal sealed class BankOptionsValidator : IValidateOptions<BankOptions>
{
    public ValidateOptionsResult Validate(string? name, BankOptions options) =>
        options.Provider == BankOptions.MockProvider
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"{BankOptions.SectionName}:Provider must be '{BankOptions.MockProvider}' (the only bank provider so far).");
}
