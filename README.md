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

Then point the three `PackageReference` versions at `2.0.0-local`. NuGet
caches by exact version and `2.0.0-local` doesn't change between builds, so
after re-packing you must drop the cached copy:

```sh
dotnet nuget locals http-cache --clear
rm -rf ~/.nuget/packages/esiipayment.*
```

Revert both before committing — a `-local` version cannot restore in CI.

## Providers

- **`mock`** — the spec's deterministic reference provider. Fully live, no
  credentials needed.
- **`chapa`** — live against Chapa's real test API when credentials are set:
  a genuine sandbox transaction and a real `checkout.chapa.co` URL.
- **`telebirr`** — always reported unconfigured, deliberately. Telebirr's
  Fabric gateway requires an RSA-PSS signature on every request that the
  manifest DSL structurally cannot express, so it cannot complete a charge
  no matter what credentials you supply. It's wired to demonstrate provider
  selection.

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
