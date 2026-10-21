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
    public async Task FromJsonBodyInt_ClampsIntMaxValue()
    {
        var context = JsonContext("""{"units":2147483647}""");

        var amount = await NetQuotaAmountExtractors.FromJsonBodyInt("units")(context);

        Assert.Equal(NetQuotaAmountExtractors.MaximumAmount, amount);
    }

    [Fact]
    public async Task FromJsonBodyInt_EmptyBodyWithDeclaredLength_FailsClosed()
    {
        // Endpoint filters run after model binding: a body-bound request arrives here at EOF even
        // though Content-Length still advertises bytes. The extractor must not charge the fallback.
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream();
        context.Request.ContentLength = 11;
        context.Request.ContentType = "application/json";

        await Assert.ThrowsAsync<NetQuotaAmountResolutionException>(
            () => NetQuotaAmountExtractors.FromJsonBodyInt("units")(context).AsTask());
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

    [Fact]
    public async Task Endpoint_IntMaxJsonAmount_CannotOverflowQuota()
    {
        var executions = 0;
        await using var app = await StartJsonAmountAppAsync(quotaPerDay: 5, () => Interlocked.Increment(ref executions));
        using var client = new HttpClient { BaseAddress = new Uri(Address(app)) };

        var first = await PostJsonAsync(client, """{"units":1}""");
        var oversized = await PostJsonAsync(client, """{"units":2147483647}""");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, oversized.StatusCode);
        Assert.Equal(1, executions);

        // The rejected request must not have wrapped the counter negative.
        var usage = await app.Services.GetRequiredService<QuotaManager>()
            .GetQuotaAsync("user-1", "api", PeriodType.Daily, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, usage.Used);
    }

    [Fact]
    public async Task Endpoint_BodyBoundParameter_FailsClosedInsteadOfChargingFallbackAmount()
    {
        var executions = 0;
        await using var app = await StartBodyBoundAppAsync(
            quotaPerDay: 10,
            useBoundArgument: false,
            () => Interlocked.Increment(ref executions));
        using var client = new HttpClient { BaseAddress = new Uri(Address(app)) };

        var response = await PostJsonAsync(client, """{"units":4}""");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("quota_amount_unresolved", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(0, executions);

        var usage = await app.Services.GetRequiredService<QuotaManager>()
            .GetQuotaAsync("user-1", "api", PeriodType.Daily, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, usage.Used);
    }

    [Fact]
    public async Task Endpoint_BodyBoundParameter_ChargesDeclaredUnitsFromBoundArgument()
    {
        var executions = 0;
        await using var app = await StartBodyBoundAppAsync(
            quotaPerDay: 10,
            useBoundArgument: true,
            () => Interlocked.Increment(ref executions));
        using var client = new HttpClient { BaseAddress = new Uri(Address(app)) };

        var response = await PostJsonAsync(client, """{"units":4}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, executions);
        Assert.Equal("6", Assert.Single(response.Headers.GetValues(NetQuotaHeaderNames.RateLimitRemaining)));

        var usage = await app.Services.GetRequiredService<QuotaManager>()
            .GetQuotaAsync("user-1", "api", PeriodType.Daily, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(4, usage.Used);
    }

    [Fact]
    public async Task Endpoint_QuotaDenial_IncludesRetryAfter()
    {
        await using var app = await StartAppAsync(quotaPerDay: 1, () => { });
        using var client = new HttpClient { BaseAddress = new Uri(Address(app)) };

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client)).StatusCode);
        var denied = await PostAsync(client);

        Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);
        var retryAfter = Assert.Single(denied.Headers.GetValues(NetQuotaHeaderNames.RetryAfter));
        Assert.True(int.TryParse(retryAfter, out var seconds));
        Assert.InRange(seconds, 1, 86_400);
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

    private static Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string json) =>
        client.PostAsync("/work", new StringContent(json, Encoding.UTF8, "application/json"));

    private static string Address(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses;
        return Assert.Single(addresses!);
    }

    private static QuotaConfig CreateConfig(int quotaPerDay) => new()
    {
        DefaultTier = "free",
        Tiers = new Dictionary<string, TierConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["free"] = new TierConfig
            {
                DailyQuotas = new Dictionary<string, int>(StringComparer.Ordinal) { ["api"] = quotaPerDay }
            }
        }
    };

    private static async Task<WebApplication> StartJsonAmountAppAsync(int quotaPerDay, Action onExecute)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddNetQuota(CreateConfig(quotaPerDay));

        var app = builder.Build();
        app.MapPost("/work", (HttpContext _) =>
        {
            onExecute();
            return Results.Ok(new { ok = true });
        }).WithNetQuota(new NetQuotaEnforcementOptions
        {
            ResolveUserIdAsync = _ => ValueTask.FromResult<string?>("user-1"),
            ResolveResourceAsync = _ => ValueTask.FromResult("api"),
            ResolveAmountAsync = NetQuotaAmountExtractors.FromJsonBodyInt("units")
        });

        await app.StartAsync();
        return app;
    }

    private static async Task<WebApplication> StartBodyBoundAppAsync(
        int quotaPerDay,
        bool useBoundArgument,
        Action onExecute)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddNetQuota(CreateConfig(quotaPerDay));

        var app = builder.Build();
        app.MapPost("/work", (WorkItem item) =>
        {
            onExecute();
            return Results.Ok(new { item.Units });
        }).WithNetQuota(new NetQuotaEnforcementOptions
        {
            ResolveUserIdAsync = _ => ValueTask.FromResult<string?>("user-1"),
            ResolveResourceAsync = _ => ValueTask.FromResult("api"),
            ResolveAmountAsync = useBoundArgument
                ? NetQuotaAmountExtractors.Fixed(1)
                : NetQuotaAmountExtractors.FromJsonBodyInt("units"),
            ResolveAmountFromArgumentsAsync = useBoundArgument
                ? NetQuotaAmountExtractors.FromBoundArgument<WorkItem>(item => item.Units)
                : null
        });

        await app.StartAsync();
        return app;
    }

    public sealed record WorkItem(int Units);

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
