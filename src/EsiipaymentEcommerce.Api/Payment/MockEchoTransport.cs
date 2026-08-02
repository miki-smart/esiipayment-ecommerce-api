using System.Text.Json.Nodes;
using Esiipayment.Core.Flows;
using Esiipayment.Core.Serialization;

namespace EsiipaymentEcommerce.Api.Payment;

/// <summary>
/// Stands in for the <c>mock</c> provider's nonexistent live endpoint (see
/// providers/mock/manifest.yaml) so this demo can complete a real round trip
/// through Esiipayment.Core.PaymentClient without needing any provider
/// credentials. Every checkout in this demo defaults to a fixed "redirect"
/// scenario; a real deployment swaps in HttpProviderTransport instead.
/// </summary>
public sealed class MockEchoTransport : IProviderTransport
{
    public Task<TransportOutcome> SendAsync(ResolvedRequest request, CancellationToken cancellationToken = default)
    {
        var body = (JsonObject)JsonNode.Parse(request.Body)!;
        var scenario = body["scenario"]?.GetValue<string>() ?? "succeeded";
        var reference = body["reference"]?.GetValue<string>() ?? "unknown";

        var data = scenario switch
        {
            "redirect" => new JsonObject { ["scenario"] = "redirect", ["checkout_url"] = $"https://mock.esiipayment.et/checkout/{reference}" },
            "poll" => new JsonObject { ["scenario"] = "poll" },
            "declined" => new JsonObject { ["scenario"] = "declined" },
            _ => new JsonObject { ["scenario"] = "succeeded" },
        };

        var responseBody = RequestBodyJson.Serialize(new JsonObject { ["data"] = data });
        return Task.FromResult<TransportOutcome>(new TransportOutcome.Success(200, responseBody));
    }
}
