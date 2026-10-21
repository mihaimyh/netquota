using System.Net;
using System.Text;
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
/// Regression coverage for ASP-02: a zero (or negative) consume amount must never bypass quota or
/// rate limiting, whoever supplies it — the client header, the JSON body, or an in-process caller.
/// </summary>
public class NetQuotaAspNetCoreAmountTests
{
    [Fact]
    public void Fixed_RejectsNonPositiveAmount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NetQuotaAmountExtractors.Fixed(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => NetQuotaAmountExtractors.Fixed(-1));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public async Task FromHeader_NonPositiveAmountFallsBackToUnitAmount(string raw)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Units"] = raw;

        var amount = await NetQuotaAmountExtractors.FromHeader("X-Units")(context);

        Assert.Equal(1, amount);
    }

    [Fact]
    public async Task FromHeader_PositiveAmountIsHonoured()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Units"] = "3";

        var amount = await NetQuotaAmountExtractors.FromHeader("X-Units")(context);

        Assert.Equal(3, amount);
    }

    [Theory]
    [InlineData("""{"units":0}""")]
    [InlineData("""{"units":-2}""")]
    public async Task FromJsonBodyInt_NonPositiveAmountFallsBackToUnitAmount(string json)
    {
        var context = JsonContext(json);

        var amount = await NetQuotaAmountExtractors.FromJsonBodyInt("units")(context);

        Assert.Equal(1, amount);
    }

    [Fact]
    public async Task FromJsonBodyInt_PositiveAmountIsHonoured()
    {
        var context = JsonContext("""{"units":7}""");

        var amount = await NetQuotaAmountExtractors.FromJsonBodyInt("units")(context);

        Assert.Equal(7, amount);
    }

    [Fact]
    public async Task FromBodyByteLength_ClampsOverflowingContentLength()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentLength = long.MaxValue;

        var amount = await NetQuotaAmountExtractors.FromBodyByteLength()(context);

        Assert.InRange(amount, 1, 1_000_000);
    }

    [Fact]
    public async Task Manager_RejectsZeroAmount()
    {
        var manager = new QuotaManager(
            new InMemoryQuotaStorage(),
            new QuotaConfig
            {
                DefaultTier = "free",
                Tiers = new Dictionary<string, TierConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["free"] = new TierConfig
                    {
                        DailyQuotas = new Dictionary<string, int>(StringComparer.Ordinal) { ["api"] = 1 }
                    }
                }
            });

        await Assert.ThrowsAsync<QuotaConfigValidationException>(
            () => manager.ConsumeWithResultAsync("u1", "api", 0, PeriodType.Daily));
    }

    [Fact]
    public async Task Endpoint_ZeroAmountHeader_CannotBypassExhaustedQuota()
    {
        var executions = 0;
        await using var app = await StartAppAsync(quotaPerDay: 1, () => Interlocked.Increment(ref executions));
        using var client = new HttpClient { BaseAddress = new Uri(Address(app)) };

        var first = await PostAsync(client);
        var second = await PostAsync(client);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal(1, executions);
    }

    private static DefaultHttpContext JsonContext(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = body.Length;
        context.Request.ContentType = "application/json";
        return context;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/work");
        request.Headers.TryAddWithoutValidation("X-Units", "0");
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
            ResolveResourceAsync = _ => ValueTask.FromResult("api"),
            ResolveAmountAsync = NetQuotaAmountExtractors.FromHeader("X-Units")
        });

        await app.StartAsync();
        return app;
    }
}
