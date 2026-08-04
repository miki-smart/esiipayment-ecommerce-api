using System.Text.Json.Nodes;
using Esiipayment.Core;
using Esiipayment.Core.Flows;
using Esiipayment.Core.Manifests;
using Esiipayment.Core.Native;
using Esiipayment.Core.Persistence;
using Esiipayment.Providers;
using Esiipayment.Providers.Telebirr;

namespace EsiipaymentEcommerce.Api.Payment;

/// <summary>
/// Builds an <see cref="IPaymentClient"/> for one of this demo's configured
/// providers. Nothing downstream of this factory ever branches on which
/// provider was chosen (Invariant I12) — the checkout endpoint only reads
/// PaymentStatus/NextAction/FailureCode/RetryClass off the result, which is
/// why adding Chapa here required no change to any endpoint.
///
/// That holds across both provider kinds, which is the interesting part:
/// mock and Chapa are <b>manifest</b> providers, executed by the SDK's
/// interpreter from a manifest.yaml, while Telebirr is a <b>native</b>
/// provider whose behaviour is hand-written code (it signs a canonicalized
/// parameter string, which the manifest DSL deliberately cannot express — see
/// spec/03-manifest-dsl.md#native-providers). This class is the only place in
/// this repository that knows the difference, because construction is
/// inherently provider-specific: different credentials, different transport
/// wiring. Everything past the return statement is uniform.
///
/// Credentials come from configuration (dotnet user-secrets in development),
/// never from source. See the README for the exact keys.
/// </summary>
public sealed class PaymentGatewayFactory
{
    private readonly Dictionary<string, ManifestDocument> _manifests = new();
    private readonly Dictionary<string, CapabilitiesDocument> _capabilities = new();
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;

    public PaymentGatewayFactory(IHttpClientFactory httpClientFactory, IConfiguration config)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;

        // Both kinds of declaration come from the Esiipayment.Providers
        // package, so this repository needs no spec-repo checkout or
        // submodule. A manifest provider carries a manifest.yaml the SDK
        // interprets; a native one carries a capabilities.yaml that declares
        // only what it can do, with the behaviour in its own package.
        foreach (var provider in SupportedProviders)
        {
            if (BundledProviders.NativeNames.Contains(provider))
            {
                _capabilities[provider] = CapabilitiesLoader.Load(BundledProviders.CapabilitiesYaml(provider));
            }
            else
            {
                _manifests[provider] = ManifestLoader.Load(BundledProviders.ManifestYaml(provider));
            }
        }
    }

    public static readonly IReadOnlyList<string> SupportedProviders = ["mock", "chapa", "telebirr"];

    /// <summary>The provider's display name, from whichever declaration it carries.</summary>
    public string DisplayNameFor(string provider) =>
        _manifests.TryGetValue(provider, out var manifest) ? manifest.DisplayName
        : _capabilities.TryGetValue(provider, out var capabilities) ? capabilities.DisplayName
        : throw new ArgumentOutOfRangeException(nameof(provider), provider, $"Unknown provider. Supported: {string.Join(", ", SupportedProviders)}.");

    /// <summary>Whether this provider has credentials configured. The UI uses this to say which options can be attempted at all.</summary>
    public bool IsConfigured(string provider) => provider switch
    {
        "mock" => true,
        "chapa" => !string.IsNullOrWhiteSpace(_config["Payments:Chapa:SecretKey"]),

        // All five credential values. Telebirr's capabilities.yaml calls its
        // shape triple_key as a nearest fit, but the real set is five: the
        // fabric app id and secret buy a bearer token, the merchant app id and
        // code identify who is collecting, and the private key signs every
        // request. Missing any one means no request can even be built.
        "telebirr" => !string.IsNullOrWhiteSpace(_config["Payments:Telebirr:FabricAppId"])
                      && !string.IsNullOrWhiteSpace(_config["Payments:Telebirr:AppSecret"])
                      && !string.IsNullOrWhiteSpace(_config["Payments:Telebirr:MerchantAppId"])
                      && !string.IsNullOrWhiteSpace(_config["Payments:Telebirr:MerchantCode"])
                      && !string.IsNullOrWhiteSpace(_config["Payments:Telebirr:MerchantPrivateKey"]),

        _ => false,
    };

    /// <summary>
    /// A caveat the UI must show alongside a provider that is configured but
    /// still cannot be trusted to complete a charge. This exists because
    /// "credentials present" and "will actually work" are different facts, and
    /// conflating them is how a demo quietly implies more than it can do.
    /// </summary>
    public string? Caveat(string provider) => provider switch
    {
        "telebirr" =>
            "Verified against the Telebirr developer-portal sandbox on 2026-08-04: an order was created and its " +
            "status queried successfully. What has not been exercised is a payer actually completing payment on the " +
            "H5 page, so the settled-status handling beyond WAIT_PAY is still untested, and these sandbox " +
            "credentials move no real money. See providers/telebirr/metadata.yaml in the spec repository.",

        _ => null,
    };

    /// <param name="orderId">
    /// The order/idempotency key this client will operate on. It is baked
    /// into the provider's callback URL because that callback must name the
    /// payment it resolves, and this integration correlates on the URL path
    /// (the DSL standardizes no body-field correlator; see
    /// spec/05-webhooks.md and MapEsiipaymentWebhooks).
    /// </param>
    public IPaymentClient Create(string provider, IPaymentStore store, string orderId) => provider switch
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
                ["return_url"] = ReturnUrl(orderId),
            },
            credentials: new JsonObject
            {
                ["secret_key"] = _config["Payments:Chapa:SecretKey"] ?? "",
                ["webhook_secret"] = _config["Payments:Chapa:WebhookSecret"] ?? "",
                ["public_key"] = _config["Payments:Chapa:PublicKey"] ?? "",
                ["encryption_key"] = _config["Payments:Chapa:EncryptionKey"] ?? "",
            }),

        "telebirr" => CreateTelebirr(store, orderId),

        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, $"Unknown provider. Supported: {string.Join(", ", SupportedProviders)}."),
    };

    /// <summary>
    /// The native provider, for contrast with the two manifest ones above.
    /// Note what differs: credentials go into a typed options object rather
    /// than a <c>credentials</c> JSON namespace the interpreter reads, and the
    /// base URL comes from this application's configuration rather than from
    /// the provider declaration — a native provider's capabilities.yaml
    /// carries no <c>environments</c> section, because endpoints are wire
    /// mechanics and those live in the implementation, not in shared data.
    ///
    /// Note also what does not differ: the return type.
    /// </summary>
    private IPaymentClient CreateTelebirr(IPaymentStore store, string orderId)
    {
        if (!IsConfigured("telebirr"))
        {
            // Unlike Chapa — which can be constructed with an empty key and
            // will simply come back AuthFailed, a useful thing for a demo to
            // show — Telebirr cannot even build a request without a private
            // key to sign it with. Saying so beats a cryptography error from
            // deep inside the constructor.
            throw new InvalidOperationException(
                "Telebirr is not configured. Set Payments:Telebirr:FabricAppId, :AppSecret, :MerchantAppId, " +
                ":MerchantCode and :MerchantPrivateKey (see the README). All five are required: the app id and " +
                "secret buy a bearer token, the merchant app id and code identify who is collecting, and the " +
                "private key signs every request.");
        }

        // A native provider's capabilities.yaml carries no `environments`
        // section — endpoints are wire mechanics, so the host is this
        // application's configuration. The default is the developer-portal
        // sandbox, which is reachable with ordinary certificate validation;
        // production differs.
        var baseUrl = _config["Payments:Telebirr:BaseUrl"]
                      ?? "https://developerportal.ethiotelebirr.et:38443/apiaccess/payment/gateway";

        return new TelebirrPaymentClient(
            _capabilities["telebirr"],
            new HttpProviderTransport(_httpClientFactory.CreateClient("telebirr"), baseUrl),
            store,
            new TelebirrOptions
            {
                FabricAppId = _config["Payments:Telebirr:FabricAppId"]!,
                AppSecret = _config["Payments:Telebirr:AppSecret"]!,
                MerchantAppId = _config["Payments:Telebirr:MerchantAppId"]!,
                MerchantCode = _config["Payments:Telebirr:MerchantCode"]!,
                MerchantPrivateKey = _config["Payments:Telebirr:MerchantPrivateKey"]!,
                WebBaseUrl = _config["Payments:Telebirr:WebBaseUrl"]
                             ?? "https://developerportal.ethiotelebirr.et:38443/payment/web/paygate?",
                NotifyUrl = WebhookUrl("telebirr", orderId),
                RedirectUrl = ReturnUrl(orderId),
            });
    }

    /// <summary>
    /// Where the provider sends the payer's browser once they finish (or
    /// abandon) payment on its hosted page. Configured by
    /// <c>Payments:ReturnUrl</c>; the order id is appended so the landing
    /// page can look up and show that payment's real outcome rather than
    /// assuming the payer arriving back means success — the provider
    /// redirects on cancel too, and the authoritative status only comes
    /// from a sync or a webhook.
    /// </summary>
    private string ReturnUrl(string orderId)
    {
        var configured = _config["Payments:ReturnUrl"] ?? "http://localhost:5173/thanks";
        var separator = configured.Contains('?') ? '&' : '?';
        return $"{configured}{separator}orderId={Uri.EscapeDataString(orderId)}";
    }

    private string WebhookUrl(string provider, string orderId) =>
        $"{(_config["Payments:PublicBaseUrl"] ?? "http://localhost:5016").TrimEnd('/')}/esiipayment/webhooks/{provider}/{orderId}";
}
