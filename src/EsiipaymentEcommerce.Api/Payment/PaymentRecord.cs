namespace EsiipaymentEcommerce.Api.Payment;

/// <summary>
/// What ESIIPayment persists per idempotency key, independent of this
/// store's own <c>Order</c> table — same shape as
/// esiipayment-dotnet/samples/Esiipayment.Samples.WebApi's
/// PaymentRecord, reimplemented here rather than referenced directly
/// (a sample project isn't a library other apps depend on; this is the
/// pattern every real integrator writes for themselves).
/// </summary>
public sealed class PaymentRecord
{
    public required string IdempotencyKey { get; init; }
    public required string Operation { get; init; }
    public required string PayloadHash { get; init; }

    public string? Status { get; set; }
    public string? NextActionJson { get; set; }
    public string? FailureCode { get; set; }
    public string? RetryClass { get; set; }
    public string StateJson { get; set; } = "{}";
}
