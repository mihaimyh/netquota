using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NetQuota.AspNetCore;

public static class NetQuotaHeaderNames
{
    public const string RateLimitLimit = "X-RateLimit-Limit";
    public const string RateLimitRemaining = "X-RateLimit-Remaining";
    public const string RateLimitReset = "X-RateLimit-Reset";
    public const string RetryAfter = "Retry-After";
    public const string QuotaWarningThreshold = "X-Quota-Warning-Threshold";
    public const string QuotaWarningUsed = "X-Quota-Warning-Used";
    public const string QuotaWarningLimit = "X-Quota-Warning-Limit";
    public const string RequestId = "X-Request-ID";
}

public static class NetQuotaHttpHeaders
{
    public static void ApplyRateLimitHeaders(HttpContext context, RateLimitStatus status)
    {
        if (status.Limit < int.MaxValue)
        {
            context.Response.Headers[NetQuotaHeaderNames.RateLimitLimit] =
                status.Limit.ToString(CultureInfo.InvariantCulture);
            context.Response.Headers[NetQuotaHeaderNames.RateLimitRemaining] =
                Math.Max(0, status.Remaining).ToString(CultureInfo.InvariantCulture);
            context.Response.Headers[NetQuotaHeaderNames.RateLimitReset] =
                status.ResetAtUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        }

        if (!status.Allowed && status.RetryAfter > TimeSpan.Zero)
        {
            // Ceil sub-second delays to at least 1 second (goquota 6d3ec80 / MIDDLEWARE-2)
            var retrySeconds = Math.Max(1, (int)Math.Ceiling(status.RetryAfter.TotalSeconds));
            context.Response.Headers[NetQuotaHeaderNames.RetryAfter] =
                retrySeconds.ToString(CultureInfo.InvariantCulture);
        }
    }

    public static void ApplyQuotaHeaders(HttpContext context, ConsumeResult result)
    {
        if (result.Limit == -1)
        {
            context.Response.Headers[NetQuotaHeaderNames.RateLimitLimit] = "-1";
            context.Response.Headers[NetQuotaHeaderNames.RateLimitRemaining] = "-1";
            return;
        }

        context.Response.Headers[NetQuotaHeaderNames.RateLimitLimit] =
            result.Limit.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers[NetQuotaHeaderNames.RateLimitRemaining] =
            Math.Max(0, result.Remaining).ToString(CultureInfo.InvariantCulture);

        if (result.ResetAtUtc is { } reset)
        {
            context.Response.Headers[NetQuotaHeaderNames.RateLimitReset] =
                reset.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        }

        if (!result.Allowed && result.ResetAtUtc is { } retryReset)
        {
            // A quota denial carries Retry-After derived from the window reset, never less than 1 second.
            var retrySeconds = Math.Max(1, (int)Math.Ceiling((retryReset - DateTimeOffset.UtcNow).TotalSeconds));
            context.Response.Headers[NetQuotaHeaderNames.RetryAfter] =
                retrySeconds.ToString(CultureInfo.InvariantCulture);
        }
    }

    public static void ApplyWarningHeaders(HttpContext context, QuotaUsage usage, double threshold)
    {
        context.Response.Headers[NetQuotaHeaderNames.QuotaWarningThreshold] =
            threshold.ToString("0.##", CultureInfo.InvariantCulture);
        context.Response.Headers[NetQuotaHeaderNames.QuotaWarningUsed] =
            usage.Used.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers[NetQuotaHeaderNames.QuotaWarningLimit] =
            usage.Limit.ToString(CultureInfo.InvariantCulture);
    }
}

public static class NetQuotaAmountExtractors
{
    /// <summary>Upper bound applied to any client-supplied consume amount.</summary>
    public const int MaximumAmount = 1_000_000;

    public static Func<HttpContext, ValueTask<int>> Fixed(int amount = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(amount, 1);
        return _ => ValueTask.FromResult(amount);
    }

    public static Func<HttpContext, ValueTask<int>> FromHeader(string headerName, int fallback = 1) =>
        ctx =>
        {
            if (ctx.Request.Headers.TryGetValue(headerName, out var raw) &&
                int.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
                parsed >= 1)
            {
                // Clamp client-supplied amounts so one request cannot consume an absurd budget.
                return ValueTask.FromResult(Math.Min(parsed, MaximumAmount));
            }

            return ValueTask.FromResult(Math.Max(1, fallback));
        };

    public static Func<HttpContext, ValueTask<int>> FromBodyByteLength(int bytesPerUnit = 1, int minimumUnits = 1) =>
        ctx =>
        {
            var len = ctx.Request.ContentLength ?? 0L;
            if (len <= 0)
            {
                return ValueTask.FromResult(Math.Max(1, minimumUnits));
            }

            var divisor = Math.Max(1, bytesPerUnit);
            var units = (int)Math.Min(1_000_000L, (long)Math.Ceiling((double)len / divisor));
            return ValueTask.FromResult(Math.Max(Math.Max(1, minimumUnits), units));
        };

    /// <summary>
    /// Reads an integer amount from the JSON request body. Endpoint filters run after minimal-API
    /// argument binding, so this extractor can only see the body when the endpoint does not declare a
    /// body-bound parameter. When the body has already been consumed it fails closed with a
    /// <see cref="NetQuotaAmountResolutionException"/> instead of silently charging the fallback;
    /// use <see cref="FromBoundArgument{T}"/> with
    /// <see cref="NetQuotaEnforcementOptions.ResolveAmountFromArgumentsAsync"/> on body-bound endpoints.
    /// </summary>
    public static Func<HttpContext, ValueTask<int>> FromJsonBodyInt(string propertyName, int fallback = 1) =>
        async ctx =>
        {
            ctx.Request.EnableBuffering();
            var body = ctx.Request.Body;
            if (body.CanSeek)
            {
                body.Position = 0;
            }

            try
            {
                if (body.CanSeek)
                {
                    var probe = new byte[1];
                    var read = await body.ReadAsync(probe.AsMemory(), ctx.RequestAborted);
                    body.Position = 0;
                    if (read == 0)
                    {
                        if (ctx.Request.ContentLength is > 0)
                        {
                            throw BodyAlreadyConsumed(propertyName);
                        }

                        return Math.Max(1, fallback);
                    }
                }

                using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ctx.RequestAborted);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty(propertyName, out var prop) &&
                    prop.TryGetInt32(out var val) &&
                    val >= 1)
                {
                    // Clamp client-supplied amounts so one request cannot consume an absurd budget.
                    return Math.Min(val, MaximumAmount);
                }

                return Math.Max(1, fallback);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (JsonException ex)
            {
                throw new NetQuotaAmountResolutionException(
                    $"The request body is not valid JSON while resolving amount property '{propertyName}'.",
                    ex);
            }
            finally
            {
                if (body.CanSeek)
                {
                    body.Position = 0;
                }
            }
        };

    /// <summary>
    /// Reads the amount from an argument the minimal API has already bound (for example a JSON body
    /// parameter). Use it as <see cref="NetQuotaEnforcementOptions.ResolveAmountFromArgumentsAsync"/>
    /// on endpoints that bind the request body, where <see cref="FromJsonBodyInt"/> cannot re-read it.
    /// </summary>
    public static Func<EndpointFilterInvocationContext, ValueTask<int>> FromBoundArgument<T>(
        Func<T, int> selector,
        int fallback = 1)
    {
        ArgumentNullException.ThrowIfNull(selector);

        return ctx =>
        {
            if (ctx.Arguments.FirstOrDefault(argument => argument is T) is not T value)
            {
                throw new NetQuotaAmountResolutionException(
                    $"The endpoint has no bound argument of type '{typeof(T).Name}' to resolve the consume amount from.");
            }

            var amount = selector(value);
            return ValueTask.FromResult(amount >= 1 ? Math.Min(amount, MaximumAmount) : Math.Max(1, fallback));
        };
    }

    private static NetQuotaAmountResolutionException BodyAlreadyConsumed(string propertyName) =>
        new(
            $"The request body has already been consumed, so amount property '{propertyName}' cannot be read. " +
            "Endpoint filters run after minimal-API argument binding: when the endpoint declares a body-bound " +
            "parameter the body stream is at EOF and cannot be rewound. Use " +
            "NetQuotaAmountExtractors.FromBoundArgument<T> together with " +
            "NetQuotaEnforcementOptions.ResolveAmountFromArgumentsAsync on body-bound endpoints. " +
            "Refusing to charge the fallback amount silently.");
}

/// <summary>
/// Raised when the consume amount for a request cannot be resolved from the source the endpoint
/// configured (for example a JSON body that model binding already consumed).
/// </summary>
public sealed class NetQuotaAmountResolutionException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed record NetQuotaEnforcementOptions
{
    public required Func<HttpContext, ValueTask<string?>> ResolveUserIdAsync { get; init; }
    public required Func<HttpContext, ValueTask<string>> ResolveResourceAsync { get; init; }
    public Func<HttpContext, ValueTask<int>> ResolveAmountAsync { get; init; } = NetQuotaAmountExtractors.Fixed(1);
    /// <summary>
    /// Amount resolver that can read arguments minimal APIs have already bound. Endpoint filters run
    /// after argument binding, so on an endpoint that declares a JSON body parameter the request body
    /// is already consumed and <see cref="NetQuotaAmountExtractors.FromJsonBodyInt"/> cannot re-read
    /// it. Use <see cref="NetQuotaAmountExtractors.FromBoundArgument{T}"/> here for those endpoints.
    /// When set, this takes precedence over <see cref="ResolveAmountAsync"/>.
    /// </summary>
    public Func<EndpointFilterInvocationContext, ValueTask<int>>? ResolveAmountFromArgumentsAsync { get; init; }
    /// <summary>
    /// Optional idempotency key for the consume call. The default is <c>null</c>: a header supplied
    /// by the caller is not a trust boundary, because a replayed consumption is reported as allowed
    /// and the filter would execute the protected endpoint again without charging quota. Only return
    /// a key derived from authoritative server state, and only when replays are safe for the endpoint.
    /// </summary>
    public Func<HttpContext, ValueTask<string?>> ResolveIdempotencyKeyAsync { get; init; } =
        _ => ValueTask.FromResult<string?>(null);
    public Func<HttpContext, ValueTask<string?>>? ResolveDistinctKeyAsync { get; init; }
    public Func<HttpContext, ValueTask<string?>>? ResolveExplicitTierAsync { get; init; }
    public PeriodType PeriodType { get; init; } = PeriodType.Auto;
    public int QuotaExceededStatusCode { get; init; } = StatusCodes.Status429TooManyRequests;
    public Func<HttpContext, ConsumeResult, Task>? OnQuotaExceededAsync { get; init; }
    public Func<HttpContext, RateLimitExceededException, Task>? OnRateLimitExceededAsync { get; init; }
}

public sealed record NetQuotaUsageApiOptions(
    Func<HttpContext, ValueTask<string?>> ResolveUserIdAsync,
    IReadOnlyList<string> KnownResources,
    Func<HttpContext, ValueTask<string?>>? ResolveExplicitTierAsync = null,
    Func<IReadOnlyList<string>, HttpContext, IReadOnlyList<string>>? ResourceFilter = null,
    bool FilterInactiveResources = false);

public static class NetQuotaServiceCollectionExtensions
{
    private static ILogger GetLogger(HttpContext httpContext) =>
        httpContext.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(typeof(NetQuotaServiceCollectionExtensions))
        ?? NullLogger.Instance;

    private sealed class HttpContextQuotaWarningHandler(HttpContext httpContext) : IQuotaWarningHandler
    {
        public Task OnWarningAsync(QuotaUsage usage, double threshold, CancellationToken cancellationToken = default)
        {
            NetQuotaHttpHeaders.ApplyWarningHeaders(httpContext, usage, threshold);
            return Task.CompletedTask;
        }
    }

    public static IServiceCollection AddNetQuota(
        this IServiceCollection services,
        QuotaConfig config,
        Func<IServiceProvider, IQuotaStorage>? storageFactory = null)
    {
        config.Validate();
        services.TryAddSingleton(config);

        if (storageFactory is not null)
        {
            services.TryAddSingleton(storageFactory);
        }
        else
        {
            services.TryAddSingleton<IQuotaStorage, InMemoryQuotaStorage>();
        }

        services.TryAddSingleton(sp =>
            new QuotaManager(
                sp.GetRequiredService<IQuotaStorage>(),
                sp.GetRequiredService<QuotaConfig>()));

        return services;
    }

    public static TBuilder WithNetQuota<TBuilder>(
        this TBuilder builder,
        NetQuotaEnforcementOptions options)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        builder.AddEndpointFilter(async (invocationContext, next) =>
        {
            var httpContext = invocationContext.HttpContext;
            var manager = httpContext.RequestServices.GetRequiredService<QuotaManager>();
            var userId = await options.ResolveUserIdAsync(httpContext);
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Results.Unauthorized();
            }

            var resource = await options.ResolveResourceAsync(httpContext);
            int amount;
            try
            {
                amount = options.ResolveAmountFromArgumentsAsync is not null
                    ? await options.ResolveAmountFromArgumentsAsync(invocationContext)
                    : await options.ResolveAmountAsync(httpContext);
            }
            catch (NetQuotaAmountResolutionException amountEx)
            {
                GetLogger(httpContext).LogError(
                    amountEx,
                    "NetQuota could not resolve the consume amount for user {UserId}.",
                    userId);
                return Results.Json(
                    new { error = "quota_amount_unresolved", detail = amountEx.Message },
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            var idempotencyKey = await options.ResolveIdempotencyKeyAsync(httpContext);
            var distinctKey = options.ResolveDistinctKeyAsync is not null
                ? await options.ResolveDistinctKeyAsync(httpContext)
                : null;
            var explicitTier = options.ResolveExplicitTierAsync is not null
                ? await options.ResolveExplicitTierAsync(httpContext)
                : null;

            var warningHandler = new HttpContextQuotaWarningHandler(httpContext);
            try
            {
                var result = await manager.ConsumeWithResultAsync(
                    userId,
                    resource,
                    amount,
                    options.PeriodType,
                    new ConsumeOptions(
                        IdempotencyKey: idempotencyKey,
                        DistinctKey: distinctKey,
                        WarningHandler: warningHandler),
                    explicitTier,
                    httpContext.RequestAborted);

                NetQuotaHttpHeaders.ApplyQuotaHeaders(httpContext, result);

                if (!result.Allowed)
                {
                    if (options.OnQuotaExceededAsync is not null)
                    {
                        await options.OnQuotaExceededAsync(httpContext, result);
                        return Results.Empty;
                    }

                    return Results.Json(
                        new
                        {
                            error = "quota_exceeded",
                            resource,
                            period = result.Period.ToString().ToLowerInvariant(),
                            used = result.NewUsed,
                            limit = result.Limit,
                            remaining = result.Remaining,
                            reset_at = result.ResetAtUtc
                        },
                        statusCode: options.QuotaExceededStatusCode);
                }

                return await next(invocationContext);
            }
            catch (RateLimitExceededException rlEx)
            {
                NetQuotaHttpHeaders.ApplyRateLimitHeaders(httpContext, rlEx.Status);
                if (options.OnRateLimitExceededAsync is not null)
                {
                    await options.OnRateLimitExceededAsync(httpContext, rlEx);
                    return Results.Empty;
                }

                return Results.Json(
                    new
                    {
                        error = "rate_limit_exceeded",
                        resource = rlEx.Resource,
                        retry_after_seconds = Math.Max(1, (int)Math.Ceiling(rlEx.RetryAfter.TotalSeconds))
                    },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }
        });

        return builder;
    }

    public static RouteHandlerBuilder MapNetQuotaUsageEndpoint(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        NetQuotaUsageApiOptions options)
    {
        return endpoints.MapGet(pattern, async (
            HttpContext httpContext,
            QuotaManager manager,
            CancellationToken cancellationToken) =>
        {
            var userId = await options.ResolveUserIdAsync(httpContext);
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Results.Unauthorized();
            }

            if (userId.Length > 255)
            {
                return Results.BadRequest(new { error = "invalid user ID format" });
            }

            var explicitTier = options.ResolveExplicitTierAsync is not null
                ? await options.ResolveExplicitTierAsync(httpContext)
                : null;

            var now = await manager.GetNowUtcAsync(cancellationToken);
            QuotaEntitlement? ent;
            try
            {
                ent = await manager.GetEntitlementAsync(userId, cancellationToken);
            }
            catch (Exception ex)
            {
                cancellationToken.ThrowIfCancellationRequested();
                GetLogger(httpContext).LogError(ex, "NetQuota usage endpoint failed to read the entitlement for user {UserId}.", userId);
                return Results.Json(
                    new { error = "quota_unavailable" },
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            var effectiveTier = explicitTier
                ?? ent?.ResolveEffectiveTier(now, manager.Config.DefaultTier)
                ?? manager.Config.DefaultTier;
            if (!manager.Config.Tiers.ContainsKey(effectiveTier))
            {
                effectiveTier = manager.Config.DefaultTier;
            }

            // Compute entitlement status accurately (goquota pkg/api/handler.go:108-140)
            var status = "default";
            object? promoDto = null;
            if (ent is not null)
            {
                if (ent.ActivePromotion(now) is { } promo)
                {
                    status = "active";
                    promoDto = new
                    {
                        tier = promo.Tier,
                        granted_at = promo.GrantedAtUtc,
                        expires_at = promo.ExpiresAtUtc,
                        source = promo.Source,
                        reason = promo.Reason
                    };
                }
                else if (ent.ExpiresAtUtc is { } exp && exp <= now)
                {
                    status = "expired";
                }
                else
                {
                    status = "active";
                }
            }
            else if (!string.IsNullOrWhiteSpace(explicitTier))
            {
                status = "active";
            }

            // Pre-filter candidate resources before querying storage (goquota 6f84387 / b962266)
            var candidates = options.ResourceFilter is not null
                ? options.ResourceFilter(options.KnownResources, httpContext)
                : options.KnownResources;

            var resourcesDict = new Dictionary<string, object>(StringComparer.Ordinal);
            Exception? firstError = null;

            foreach (var res in candidates)
            {
                try
                {
                    var meter = await manager.GetMeterQuotaAsync(userId, res, effectiveTier, cancellationToken);
                    if (options.FilterInactiveResources &&
                        meter.Limit == 0 &&
                        meter.Used == 0 &&
                        meter.Breakdown.Count == 0)
                    {
                        continue;
                    }

                    resourcesDict[res] = new
                    {
                        limit = meter.Limit,
                        used = meter.Used,
                        remaining = meter.Remaining,
                        reset_at = meter.ResetAtUtc,
                        breakdown = meter.Breakdown.Select(b => new
                        {
                            source = b.Source.ToString().ToLowerInvariant(),
                            limit = b.Limit,
                            used = b.Used,
                            balance = b.Balance,
                            reset_at = b.ResetAtUtc,
                            distinct_keys = b.DistinctKeys
                        }).ToArray()
                    };
                }
                catch (Exception ex)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    firstError ??= ex;
                }
            }

            // Surface storage outage with HTTP 500 when all quota reads fail (goquota 61bc452 / API-2)
            if (firstError is not null && resourcesDict.Count == 0 && candidates.Count > 0)
            {
                GetLogger(httpContext).LogError(firstError, "NetQuota usage endpoint failed to read quota usage for user {UserId}.", userId);
                return Results.Json(
                    new { error = "quota_unavailable" },
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            httpContext.Response.Headers.CacheControl = "private, no-store";
            return Results.Ok(new
            {
                user_id = userId,
                tier = effectiveTier,
                status,
                promotion = promoDto,
                resources = resourcesDict
            });
        });
    }
}
