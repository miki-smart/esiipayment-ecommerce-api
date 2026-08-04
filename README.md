# esiipayment-ecommerce-api

The backend for a dummy e-commerce demo: an ASP.NET Core API that takes
payments through the [Esiipayment SDK](https://github.com/miki-smart/esiipayment-dotnet),
against Ethiopian PSPs described by the
[ESIIPayment spec](https://github.com/miki-smart/esiipayment).

The React storefront that talks to it lives in a separate repository:
[esiipayment-ecommerce-web](https://github.com/miki-smart/esiipayment-ecommerce-web).

The SDK is consumed as a **NuGet package** from nuget.org, not as source —
there is no submodule, no spec-repo checkout, and no local feed here.
Provider manifests arrive via the `Esiipayment.Providers` package.

## Endpoints

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/api/products` | Catalog |
| `GET` | `/api/products/{id}` | One product |
| `GET` | `/api/providers` | Configured PSPs, and whether each can actually charge |
| `POST` | `/api/checkout` | Create an order and start a payment |
| `POST` | `/api/orders/{id}/sync` | Ask the provider for the current status |
| `GET` | `/api/orders/{id}` | Order + last known payment outcome |
| `POST` | `/esiipayment/webhooks/{provider}/{orderId}` | Inbound provider callback |

Only `Payment/PaymentGatewayFactory.cs` knows providers differ. Every
endpoint reads `PaymentStatus` / `NextAction` / `FailureCode` /
`RetryClass` and nothing else — Invariant I12, which is why adding Chapa
touched no endpoint.

## Running it

Clone and run. The SDK restores from nuget.org, so there is nothing to
build first and no sibling checkout to arrange:

```sh
dotnet run --project src/EsiipaymentEcommerce.Api    # http://localhost:5016
```

SQLite is created on first run; products are seeded automatically.

### Working against an unreleased SDK

The pinned version is a **prerelease** (`2.0.0-preview.1`) while the SDK
settles. To try a local SDK build instead of the published one, pack it and
add that feed for a single restore — don't edit `nuget.config`, so the
committed configuration keeps matching what CI does:

```sh
git clone https://github.com/miki-smart/esiipayment-dotnet.git
cd esiipayment-dotnet && git submodule update --init --recursive
pwsh ./pack-local.ps1        # writes ../esiipayment-local-feed

cd ../esiipayment-ecommerce-api
dotnet restore -s https://api.nuget.org/v3/index.json -s ../esiipayment-local-feed
```

Then point the four `PackageReference` versions at `2.0.0-local`. NuGet
caches by exact version and `2.0.0-local` doesn't change between builds, so
after re-packing you must drop the cached copy:

```sh
dotnet nuget locals http-cache --clear
rm -rf ~/.nuget/packages/esiipayment.*
```

Revert both before committing — a `-local` version cannot restore in CI.

## Providers

This demo deliberately carries one of each kind of provider, because the
interesting property is that you cannot tell them apart from here:

- **`mock`** — *manifest* provider. The spec's deterministic reference
  provider. Fully live, no credentials needed.
- **`chapa`** — *manifest* provider. Live against Chapa's real test API when
  credentials are set: a genuine sandbox transaction and a real
  `checkout.chapa.co` URL.
- **`telebirr`** — *native* provider. Telebirr signs a canonicalized parameter
  string on every request, which the manifest DSL deliberately cannot express,
  so its behaviour is hand-written code in `Esiipayment.Providers.Telebirr`
  rather than data ([the five signals](https://github.com/miki-smart/esiipayment/blob/main/spec/03-manifest-dsl.md#native-providers)).

[`PaymentGatewayFactory`](src/EsiipaymentEcommerce.Api/Payment/PaymentGatewayFactory.cs)
is the only file in this repository that knows which kind each provider is,
because *construction* is inherently provider-specific — different credentials,
different transport wiring. Past its `return` statement everything is one
`IPaymentClient` and every endpoint handles all three identically. That is
[Invariant I12](https://github.com/miki-smart/esiipayment/blob/main/spec/02-invariants.md#i12)
holding across the manifest/native boundary, not just across providers.

### Configuring Telebirr

> **Verified against the sandbox on 2026-08-04**: an order was created and its
> status queried, and the gateway accepted the request signature. What has
> *not* been exercised is a payer completing payment on the H5 page, so the
> settled-status paths are still untested. `/api/providers` returns the current
> caveat as a `caveat` field and the UI shows it verbatim. See
> [providers/telebirr/metadata.yaml](https://github.com/miki-smart/esiipayment/blob/main/providers/telebirr/metadata.yaml)
> for the precise line between what is verified and what is not.

Five values, all required. Telebirr's `capabilities.yaml` calls its shape
`triple_key` as a nearest fit, but the real credential set is: the fabric app
id and secret (which buy a bearer token), the merchant app id and code (which
identify who is collecting), and the private key (which signs every request).
The two "app ids" are different values — swapping them produces a valid
signature over the wrong identity, which the gateway rejects without saying why.

```sh
cd src/EsiipaymentEcommerce.Api
dotnet user-secrets set "Payments:Telebirr:FabricAppId"        "..."
dotnet user-secrets set "Payments:Telebirr:AppSecret"          "..."
dotnet user-secrets set "Payments:Telebirr:MerchantAppId"      "..."
dotnet user-secrets set "Payments:Telebirr:MerchantCode"       "..."
dotnet user-secrets set "Payments:Telebirr:MerchantPrivateKey" "$(cat merchant-key.pem)"   # PEM or base64 DER
```

Endpoint configuration, defaulting to the developer-portal sandbox that the
2026-08-04 run used: `Payments:Telebirr:BaseUrl`
(`https://developerportal.ethiotelebirr.et:38443/apiaccess/payment/gateway`)
and `Payments:Telebirr:WebBaseUrl`
(`https://developerportal.ethiotelebirr.et:38443/payment/web/paygate?`, the
base of the checkout URL the payer is redirected to). Production hosts differ.
Unlike a manifest provider, a native provider's `capabilities.yaml` carries no
`environments` section — endpoints are wire mechanics, so they live in this
application's configuration instead.

**Certificate pinning is available and not needed for the sandbox.** That host
validates normally. If you point this at an endpoint that does not — a bare IP
with a self-signed certificate, say — pin the expected fingerprint rather than
accepting any certificate, which would disable transport security on a payments
call:

```sh
openssl s_client -connect <host>:38443 </dev/null 2>/dev/null \
  | openssl x509 -fingerprint -sha256 -noout
dotnet user-secrets set "Payments:Telebirr:PinnedCertificateSha256" "AB:CD:..."
```

With no pin configured, validation is left completely alone. See the comment
in [Program.cs](src/EsiipaymentEcommerce.Api/Program.cs) for why this is the
only override worth having.

**Two things that bite first**, both recorded in the provider metadata:
`merch_order_id` is rejected unless alphanumeric (this demo uses a
32-character hex order id, which is safe), and the two app ids above being
interchanged — that produces a valid signature over the wrong identity, which
the gateway rejects without explaining why.

With any of the five missing, `/api/providers` reports Telebirr unconfigured,
the UI disables it, and `/api/checkout` returns 503 rather than a crash: unlike
Chapa — which can be attempted with an empty key and comes back `AuthFailed`, a
useful thing for a demo to show — Telebirr cannot even build a request without a
key to sign it with.

### Configuring Chapa

Credentials are read from configuration and **never** committed:

```sh
cd src/EsiipaymentEcommerce.Api
dotnet user-secrets set "Payments:Chapa:SecretKey"     "CHASECK_TEST-..."
dotnet user-secrets set "Payments:Chapa:PublicKey"     "CHAPUBK_TEST-..."
dotnet user-secrets set "Payments:Chapa:WebhookSecret" "..."
dotnet user-secrets set "Payments:Chapa:EncryptionKey" "..."
```

Without them, `/api/providers` reports Chapa unconfigured and the UI
disables it. Nothing else breaks.

`Payments:PublicBaseUrl` sets the base for callback URLs given to providers
(default `http://localhost:5016`). A real Chapa callback needs a publicly
reachable URL — an ngrok tunnel, say.

## What gets persisted

Two tables, so the split is visible:

- `Orders` — this application's own concern (product, quantity, amount,
  chosen provider).
- `Payments` — the SDK's `IPaymentStore` contract: idempotency key, payload
  hash, status, `next_action` JSON, failure code/retry class, and the flow's
  accumulated `state`. The idempotency key is the order id.

`GET /api/orders/{id}` reads status through the payment store rather than a
mirrored column, so a webhook-driven update is visible without a separate
sync call.

## License

Apache 2.0.
