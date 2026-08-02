using Esiipayment.AspNetCore.Webhooks;
using EsiipaymentEcommerce.Api.Payment;
using EsiipaymentEcommerce.Api.Storage;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<EsiipaymentEcommerceDbContext>(options =>
    options.UseSqlite("Data Source=esiipayment-ecommerce-demo.db"));
builder.Services.AddScoped<EfCorePaymentStore>();
builder.Services.AddHttpClient("chapa", client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient("telebirr", client => client.Timeout = TimeSpan.FromSeconds(5));
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
// and say honestly which options can actually complete a charge.
app.MapGet("/api/providers", (PaymentGatewayFactory gateways) =>
    Results.Ok(PaymentGatewayFactory.SupportedProviders.Select(p => new
    {
        id = p,
        displayName = gateways.ManifestFor(p).DisplayName,
        configured = gateways.IsConfigured(p),
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

    var client = gateways.Create(request.Provider, new EfCorePaymentStore(db), orderId);

    // One intent shape covers every provider. Each manifest reads only the
    // fields it declares: mock dispatches on `method`, Chapa additionally
    // requires payer email/first_name/last_name on its initialize call,
    // telebirr needs only amount/currency. Sending the union is fine —
    // an unread field is simply never interpolated.
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
});

app.MapPost("/api/orders/{id}/sync", async (string id, EsiipaymentEcommerceDbContext db, PaymentGatewayFactory gateways) =>
{
    var order = await db.Orders.FindAsync(id);
    if (order is null)
    {
        return Results.NotFound();
    }

    var client = gateways.Create(order.Provider, new EfCorePaymentStore(db), id);
    var result = await client.SyncAsync(id);
    return Results.Ok(OrderResponse.From(order, result));
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
