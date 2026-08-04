using System.Security.Cryptography;
using Esiipayment.AspNetCore.Webhooks;
using EsiipaymentEcommerce.Api.Payment;
using EsiipaymentEcommerce.Api.Storage;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<EsiipaymentEcommerceDbContext>(options =>
    options.UseSqlite("Data Source=esiipayment-ecommerce-demo.db"));
builder.Services.AddScoped<EfCorePaymentStore>();
builder.Services.AddHttpClient("chapa", client => client.Timeout = TimeSpan.FromSeconds(30));

// Telebirr's Fabric endpoints are commonly reached at a bare IP address whose
// certificate fails ordinary chain and hostname validation, so a normal
// HttpClient cannot connect at all. The tempting fix — accept any certificate —
// disables transport security for this client entirely, on a payments call,
// which is not a trade worth making even in a demo.
//
// Pinning is the honest version: supply the expected certificate's SHA-256
// fingerprint and this accepts exactly that certificate and nothing else. It
// fails closed if the certificate is rotated (you update the pin), and it gives
// no attacker the blanket acceptance a trust-all callback would. With no pin
// configured, validation is left entirely alone.
//
//   dotnet user-secrets set "Payments:Telebirr:PinnedCertificateSha256" "AB:CD:…"
//   openssl s_client -connect host:port </dev/null 2>/dev/null | openssl x509 -fingerprint -sha256 -noout
builder.Services.AddHttpClient("telebirr", client => client.Timeout = TimeSpan.FromSeconds(5))
    .ConfigurePrimaryHttpMessageHandler(sp =>
    {
        var handler = new HttpClientHandler();
        var pinned = sp.GetRequiredService<IConfiguration>()["Payments:Telebirr:PinnedCertificateSha256"]
            ?.Replace(":", "").Replace(" ", "");

        if (!string.IsNullOrWhiteSpace(pinned))
        {
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null &&
                CryptographicOperations.FixedTimeEquals(
                    System.Security.Cryptography.SHA256.HashData(certificate.RawData),
                    Convert.FromHexString(pinned));
        }

        return handler;
    });
builder.Services.AddSingleton(sp => new PaymentGatewayFactory(
    sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<IConfiguration>()));

// The React dev server runs on its own origin during development.
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins("http://localhost:5173").AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

app.UseCors();

using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<EsiipaymentEcommerceDbContext>().Database.EnsureCreatedAsync();
}

// In development the React app is served by Vite on :5173 and proxies here.
// For a single-origin deployment, `npm run build` in web/ and copy its
// dist/ output into wwwroot/ — these two lines will then serve it.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/products", async (EsiipaymentEcommerceDbContext db) =>
    Results.Ok(await db.Products.AsNoTracking().ToListAsync()));

app.MapGet("/api/products/{id:int}", async (int id, EsiipaymentEcommerceDbContext db) =>
    await db.Products.FindAsync(id) is { } product ? Results.Ok(product) : Results.NotFound());

// Lets the client render the provider picker without hardcoding a roster,
// and say honestly which options can actually complete a charge. `caveat` is
// non-null for a provider that is configured but still cannot be trusted to
// take money — "credentials present" and "will actually work" are different
// facts, and the picker must not conflate them.
//
// Note what this endpoint does NOT expose: whether a provider is
// manifest-driven or native. That is a fact about how the SDK reaches it, and
// nothing in the client should be able to branch on it (Invariant I12).
app.MapGet("/api/providers", (PaymentGatewayFactory gateways) =>
    Results.Ok(PaymentGatewayFactory.SupportedProviders.Select(p => new
    {
        id = p,
        displayName = gateways.DisplayNameFor(p),
        configured = gateways.IsConfigured(p),
        caveat = gateways.Caveat(p),
    })));

app.MapPost("/api/checkout", async (CheckoutRequest request, EsiipaymentEcommerceDbContext db, PaymentGatewayFactory gateways) =>
{
    var product = await db.Products.FindAsync(request.ProductId);
    if (product is null)
    {
        return Results.NotFound(new { error = $"Unknown product {request.ProductId}." });
    }

    if (!PaymentGatewayFactory.SupportedProviders.Contains(request.Provider))
    {
        return Results.BadRequest(new { error = $"Unknown provider '{request.Provider}'. Supported: {string.Join(", ", PaymentGatewayFactory.SupportedProviders)}." });
    }

    var orderId = Guid.NewGuid().ToString("N");
    var amount = product.PriceMinorUnits * request.Quantity;

    var order = new Order
    {
        Id = orderId,
        ProductId = product.Id,
        ProductName = product.Name,
        Quantity = request.Quantity,
        AmountMinorUnits = amount,
        Currency = product.Currency,
        Provider = request.Provider,
    };
    db.Orders.Add(order);
    await db.SaveChangesAsync();

    Esiipayment.Core.IPaymentClient client;
    try
    {
        client = gateways.Create(request.Provider, new EfCorePaymentStore(db), orderId);
    }
    catch (InvalidOperationException ex)
    {
        // A provider this deployment has not configured well enough to build a
        // request for at all. 503 rather than 500: nothing is broken, it is
        // just not set up.
        return Results.Problem(title: $"Provider '{request.Provider}' is not configured", detail: ex.Message, statusCode: 503);
    }

    // One intent shape covers every provider, and every provider kind. Each
    // reads only the fields it needs: mock dispatches on `method`, Chapa
    // additionally requires payer email/first_name/last_name on its initialize
    // call, telebirr needs only amount/currency. Sending the union is fine —
    // an unread field is simply never interpolated by a manifest, and never
    // looked at by a native implementation.
    var intent = new System.Text.Json.Nodes.JsonObject
    {
        ["amount"] = amount,
        ["currency"] = product.Currency,
        ["method"] = "redirect",
        ["email"] = request.Email,
        ["first_name"] = request.FirstName,
        ["last_name"] = request.LastName,
    };

    try
    {
        var result = await client.CollectAsync(orderId, intent);
        return Results.Ok(OrderResponse.From(order, result));
    }
    catch (Esiipayment.Core.Flows.FlowExecutionException ex)
    {
        // A provider response the manifest cannot route (an unmapped
        // status_map value, say). Surfaced rather than swallowed: it means
        // the manifest and the live API disagree, which is exactly the
        // signal an adapter author needs.
        return Results.Problem(title: "Provider response did not match the manifest", detail: ex.Message, statusCode: 502);
    }
    catch (Esiipayment.Core.Flows.UnparsableProviderResponseException ex)
    {
        // The provider answered with something that isn't JSON at all — an
        // HTML error page or a WAF interstitial. The payment's outcome is
        // genuinely unknown, so this must not be presented as a failure: the
        // order stays open and a sync establishes the truth. Both provider
        // kinds raise this identically.
        return Results.Problem(title: "Provider response was unreadable; outcome unresolved", detail: ex.Message, statusCode: 502);
    }
});

app.MapPost("/api/orders/{id}/sync", async (string id, EsiipaymentEcommerceDbContext db, PaymentGatewayFactory gateways) =>
{
    var order = await db.Orders.FindAsync(id);
    if (order is null)
    {
        return Results.NotFound();
    }

    try
    {
        var client = gateways.Create(order.Provider, new EfCorePaymentStore(db), id);
        var result = await client.SyncAsync(id);
        return Results.Ok(OrderResponse.From(order, result));
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(title: $"Provider '{order.Provider}' is not configured", detail: ex.Message, statusCode: 503);
    }
});

// The store (PaymentRecord), not the Order row, is the authoritative
// status: a webhook updates the store directly, so reading through it here
// (rather than a separately-mirrored Order.Status column) is what lets the
// product/checkout pages see a webhook-driven update without extra wiring.
app.MapGet("/api/orders/{id}", async (string id, EsiipaymentEcommerceDbContext db) =>
{
    var order = await db.Orders.FindAsync(id);
    if (order is null)
    {
        return Results.NotFound();
    }

    var store = new EfCorePaymentStore(db);
    var result = await store.GetResultAsync(id);
    return result is null ? Results.Ok(OrderResponse.Pending(order)) : Results.Ok(OrderResponse.From(order, result));
});

app.MapEsiipaymentWebhooks((httpContext, provider) =>
{
    if (!PaymentGatewayFactory.SupportedProviders.Contains(provider))
    {
        return null;
    }

    var idempotencyKey = httpContext.Request.RouteValues["idempotencyKey"] as string ?? "";
    var db = httpContext.RequestServices.GetRequiredService<EsiipaymentEcommerceDbContext>();
    var gateways = httpContext.RequestServices.GetRequiredService<PaymentGatewayFactory>();
    return gateways.Create(provider, new EfCorePaymentStore(db), idempotencyKey);
});

app.Run();

internal sealed record CheckoutRequest(
    int ProductId, int Quantity, string Provider, string Email, string FirstName, string LastName);

internal sealed record OrderResponse(
    string OrderId, string ProductName, int Quantity, long AmountMinorUnits, string Currency, string Provider,
    string Status, string? NextAction, object? Failure)
{
    public static OrderResponse From(Order order, Esiipayment.Core.Domain.PaymentResult result) => new(
        order.Id, order.ProductName, order.Quantity, order.AmountMinorUnits, order.Currency, order.Provider,
        result.Status.ToString(),
        result.NextAction?.ToJson()?.ToJsonString(),
        result.Failure is { } f ? new { failure_code = f.Code.ToString(), retry_class = f.RetryClass.ToString() } : null);

    public static OrderResponse Pending(Order order) => new(
        order.Id, order.ProductName, order.Quantity, order.AmountMinorUnits, order.Currency, order.Provider,
        order.Status, null, null);
}
