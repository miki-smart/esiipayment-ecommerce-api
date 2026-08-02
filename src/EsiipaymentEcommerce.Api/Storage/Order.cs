namespace EsiipaymentEcommerce.Api.Storage;

/// <summary>
/// One checkout attempt. <see cref="Id"/> doubles as the ESIIPayment
/// idempotency key for every operation (collect/sync) against this order —
/// the same value threads through <see cref="Payment.PaymentRecord"/> in
/// EsiipaymentEcommerceDbContext.
/// </summary>
public sealed class Order
{
    public required string Id { get; init; }
    public required int ProductId { get; init; }
    public required string ProductName { get; init; }
    public required int Quantity { get; init; }
    public required long AmountMinorUnits { get; init; }
    public required string Currency { get; init; }
    public required string Provider { get; init; }

    /// <summary>Mirrors the last-known Esiipayment.Core.Domain.PaymentStatus, plus "Pending" before the first collect call completes.</summary>
    public string Status { get; set; } = "Pending";

    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
