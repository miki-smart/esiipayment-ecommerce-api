using System.Text.Json.Nodes;
using Esiipayment.Core.Domain;
using Esiipayment.Core.Persistence;
using Esiipayment.Core.Serialization;
using EsiipaymentEcommerce.Api.Storage;
using Microsoft.EntityFrameworkCore;

namespace EsiipaymentEcommerce.Api.Payment;

public sealed class EfCorePaymentStore(EsiipaymentEcommerceDbContext db) : IPaymentStore
{
    public async Task<PersistOutcome> TryPersistNewAsync(string idempotencyKey, Operation operation, string payloadHash, CancellationToken cancellationToken = default)
    {
        var existing = await db.Payments.FindAsync([idempotencyKey], cancellationToken);
        if (existing is null)
        {
            db.Payments.Add(new PaymentRecord { IdempotencyKey = idempotencyKey, Operation = operation.ToWireString(), PayloadHash = payloadHash });
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return new PersistOutcome(PersistOutcomeKind.New);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                existing = await db.Payments.FindAsync([idempotencyKey], cancellationToken);
            }
        }

        if (existing is null)
        {
            throw new InvalidOperationException($"Lost the create-if-absent race for '{idempotencyKey}' but no row was found on re-read.");
        }

        if (existing.Operation != operation.ToWireString() || existing.PayloadHash != payloadHash)
        {
            return new PersistOutcome(PersistOutcomeKind.DuplicateConflict);
        }

        return existing.Status is null
            ? new PersistOutcome(PersistOutcomeKind.New)
            : new PersistOutcome(PersistOutcomeKind.ReplayExisting, ToPaymentResult(existing));
    }

    public async Task<bool> ExistsAsync(string idempotencyKey, CancellationToken cancellationToken = default) =>
        await db.Payments.AnyAsync(p => p.IdempotencyKey == idempotencyKey, cancellationToken);

    public async Task SaveResultAsync(string idempotencyKey, PaymentResult result, CancellationToken cancellationToken = default)
    {
        var record = await db.Payments.FindAsync([idempotencyKey], cancellationToken);
        if (record is null) return;

        record.Status = result.Status.ToString();
        record.NextActionJson = result.NextAction is { } na ? CanonicalJson.Serialize(na.ToJson()) : null;
        record.FailureCode = result.Failure?.Code.ToString();
        record.RetryClass = result.Failure?.RetryClass.ToString();
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<PaymentResult?> GetResultAsync(string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var record = await db.Payments.FindAsync([idempotencyKey], cancellationToken);
        return record?.Status is null ? null : ToPaymentResult(record);
    }

    public async Task SaveFlowStateAsync(string idempotencyKey, JsonObject state, CancellationToken cancellationToken = default)
    {
        var record = await db.Payments.FindAsync([idempotencyKey], cancellationToken);
        if (record is null) return;

        record.StateJson = CanonicalJson.Serialize(state);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<JsonObject?> GetFlowStateAsync(string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var record = await db.Payments.FindAsync([idempotencyKey], cancellationToken);
        return record is null ? null : (JsonObject)JsonNode.Parse(record.StateJson)!;
    }

    private static PaymentResult ToPaymentResult(PaymentRecord record)
    {
        var state = (JsonObject)JsonNode.Parse(record.StateJson)!;
        var stateDict = state.ToDictionary(kv => kv.Key, kv => kv.Value?.DeepClone());

        var nextAction = record.NextActionJson is { } naJson ? NextAction.FromJson((JsonObject)JsonNode.Parse(naJson)!) : null;
        var failure = record.FailureCode is { } code
            ? new FailureDetail(Enum.Parse<FailureCode>(code), Enum.Parse<RetryClass>(record.RetryClass!))
            : null;

        return new PaymentResult(
            record.IdempotencyKey,
            OperationExtensions.ParseWireString(record.Operation),
            Enum.Parse<PaymentStatus>(record.Status!),
            nextAction,
            stateDict,
            failure);
    }
}
