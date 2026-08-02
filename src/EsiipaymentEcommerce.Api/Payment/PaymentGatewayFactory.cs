using System.Text.Json.Nodes;
using Esiipayment.Core;
using Esiipayment.Core.Flows;
using Esiipayment.Core.Manifests;
using Esiipayment.Core.Persistence;
using Esiipayment.Providers;

namespace EsiipaymentEcommerce.Api.Payment;

/// <summary>
/// Builds a <see cref="PaymentClient"/> for one of this demo's configured
/// providers. Nothing downstream of this factory ever branches on which
/// provider was chosen (Invariant I12) — the checkout endpoint only reads
/// PaymentStatus/NextAction/FailureCode/RetryClass off the result, which is
/// why adding Chapa here required no change to any endpoint.
///
/// Credentials come from configuration (dotnet user-secrets in development),
/// never from source. See the README for the exact keys.
/// </summary>
public sealed class PaymentGatewayFactory
{
    private readonly Dictionary<string, ManifestDocument> _manifests = new();
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;

    public PaymentGatewayFactory(IHttpClientFactory httpClientFactory, IConfiguration config)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;

        // Manifests come from the Esiipayment.Providers package, so this
        // repository needs no spec-repo checkout or submodule of its own.
        foreach (var provider in SupportedProviders)
        {
            _manifests[provider] = ManifestLoader.Load(BundledProviders.ManifestYaml(provider));
        }
    }

    public static readonly IReadOnlyList<string> SupportedProviders = ["mock", "chapa", "telebirr"];

    /// <summary>Whether this provider has real credentials configured. The UI uses this to explain, honestly, which options can actually complete a charge.</summary>
    public bool IsConfigured(string provider) => provider switch
    {
        "mock" => true,
        "chapa" => !string.IsNullOrWhiteSpace(_config["Payments:Chapa:SecretKey"]),
        // Telebirr's manifest models a simple bearer token, but the real
        // Fabric gateway additionally requires an RSA-PSS signature on every
        // request that the manifest DSL structurally cannot express (see
        // providers/telebirr/manifest.yaml). So even with a real token this
        // cannot complete a live charge; it is wired to demonstrate provider
        // selection and Invariant I4, not to take money.
        "telebirr" => false,
        _ => false,
    };

    /// <param name="orderId">
    /// The order/idempotency key this client will operate on. It is baked
    /// into <c>ctx.webhook_url</c> because the provider's callback must
    /// name the payment it resolves, and this integration correlates on the
    /// URL path (the DSL standardizes no body-field correlator; see
    /// spec/05-webhooks.md and MapEsiipaymentWebhooks).
    /// </param>
    public PaymentClient Create(string provider, IPaymentStore store, string orderId) => provider switch
    {
        "mock" => new PaymentClient(_manifests["mock"], new MockEchoTransport(), store,
            ctx: new JsonObject { ["webhook_url"] = WebhookUrl("mock", orderId) }),

        "chapa" => new PaymentClient(
            _manifests["chapa"],
            new HttpProviderTransport(_httpClientFactory.CreateClient("chapa"), _manifests["chapa"].Environments["sandbox"].BaseUrl),
            store,
            ctx: new JsonObject
            {
                ["webhook_url"] = WebhookUrl("chapa", orderId),
                ["return_url"] = _config["Payments:ReturnUrl"] ?? "https://example.com/thanks",
            },
            credentials: new JsonObject
            {
                ["secret_key"] = _config["Payments:Chapa:SecretKey"] ?? "",
                ["webhook_secret"] = _config["Payments:Chapa:WebhookSecret"] ?? "",
                ["public_key"] = _config["Payments:Chapa:PublicKey"] ?? "",
                ["encryption_key"] = _config["Payments:Chapa:EncryptionKey"] ?? "",
            }),

        "telebirr" => new PaymentClient(
            _manifests["telebirr"],
            new HttpProviderTransport(_httpClientFactory.CreateClient("telebirr"), _manifests["telebirr"].Environments["sandbox"].BaseUrl),
            store,
            ctx: new JsonObject
            {
                ["webhook_url"] = WebhookUrl("telebirr", orderId),
                ["fabric_app_id"] = _config["Payments:Telebirr:FabricAppId"] ?? "",
            },
            credentials: new JsonObject { ["access_token"] = _config["Payments:Telebirr:AccessToken"] ?? "" }),

        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, $"Unknown provider. Supported: {string.Join(", ", SupportedProviders)}."),
    };

    public ManifestDocument ManifestFor(string provider) =>
        _manifests.TryGetValue(provider, out var manifest)
            ? manifest
            : throw new ArgumentOutOfRangeException(nameof(provider), provider, $"Unknown provider. Supported: {string.Join(", ", SupportedProviders)}.");

    private string WebhookUrl(string provider, string orderId) =>
        $"{(_config["Payments:PublicBaseUrl"] ?? "http://localhost:5016").TrimEnd('/')}/esiipayment/webhooks/{provider}/{orderId}";
}
