using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetQuota.AspNetCore;

namespace NetQuota.Tests;

/// <summary>
/// Regression coverage for ASP-01: a client-supplied request id must never become the consume
/// idempotency key, because a replayed consumption is reported as allowed and the filter would
/// execute the protected endpoint again without charging quota.
/// </summary>
public class NetQuotaAspNetCoreReplayTests
{
    private const string RequestId = "replayed-client-request-id";

    [Fact]
    public async Task ClientRequestIdHeader_IsNotUsedAsIdempotencyKey()
    {
        var executions = 0;
        await using var app = await StartAppAsync(quotaPerDay: 1, () => Interlocked.Increment(ref executions));
        using var client = new HttpClient { BaseAddress = new Uri(Address(app)) };

        var first = await PostAsync(client);
        var replay = await PostAsync(client);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, replay.StatusCode);
        Assert.Equal(1, executions);
    }

    [Fact]
    public async Task ConcurrentReplays_DoNotExecuteHandlerAfterQuotaIsExhausted()
    {
        var executions = 0;
        await using var app = await StartAppAsync(quotaPerDay: 1, () => Interlocked.Increment(ref executions));
        using var client = new HttpClient { BaseAddress = new Uri(Address(app)) };

        // Seed the idempotency record so every subsequent request is a pure replay pre-fix.
        var seed = await PostAsync(client);
        Assert.Equal(HttpStatusCode.OK, seed.StatusCode);

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 50).Select(_ => PostAsync(client)));

        var allowed = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        var denied = responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests);

        Assert.Equal(0, allowed);
        Assert.Equal(50, denied);
        Assert.Equal(1, executions);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/work");
        request.Headers.TryAddWithoutValidation(NetQuotaHeaderNames.RequestId, RequestId);
        return client.SendAsync(request);
    }

    private static string Address(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses;
        return Assert.Single(addresses!);
    }

    private static async Task<WebApplication> StartAppAsync(int quotaPerDay, Action onExecute)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddNetQuota(new QuotaConfig
        {
            DefaultTier = "free",
            Tiers = new Dictionary<string, TierConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["free"] = new TierConfig
                {
                    DailyQuotas = new Dictionary<string, int>(StringComparer.Ordinal) { ["api"] = quotaPerDay }
                }
            }
        });

        var app = builder.Build();
        app.MapPost("/work", (HttpContext _) =>
        {
            onExecute();
            return Results.Ok(new { ok = true });
        }).WithNetQuota(new NetQuotaEnforcementOptions
        {
            ResolveUserIdAsync = _ => ValueTask.FromResult<string?>("user-1"),
            ResolveResourceAsync = _ => ValueTask.FromResult("api")
        });

        await app.StartAsync();
        return app;
    }
}
