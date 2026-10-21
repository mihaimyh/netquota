using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NetQuota.Billing.RevenueCat;

/// <summary>
/// RevenueCat provider. Verifies the webhook (constant-time bearer token, or an optional base64
/// HMAC-SHA256 of the body), applies entitlement changes, moves entitlements on TRANSFER and
/// reconciles a subscriber from the REST API.
/// </summary>
public sealed class RevenueCatBillingProvider
{
    private readonly RevenueCatOptions _options;
    private readonly IBillingEntitlementStore _store;
    private readonly HttpClient _http;
    private readonly BillingWebhookCallback? _callback;
    private readonly BillingMetrics? _metrics;
    private readonly TimeProvider _time;

    public RevenueCatBillingProvider(
        RevenueCatOptions options,
        IBillingEntitlementStore store,
        HttpClient httpClient,
        BillingWebhookCallback? callback = null,
        BillingMetrics? metrics = null,
        TimeProvider? timeProvider = null)
    {
        _options = options;
        _store = store;
        _http = httpClient;
        _callback = callback;
        _metrics = metrics;
        _time = timeProvider ?? TimeProvider.System;

        if (_http.BaseAddress is null)
        {
            _http.BaseAddress = new Uri(_options.ApiBaseUrl);
        }
    }

    public string Name => "revenuecat";

    public async Task<RevenueCatParseResult> HandleWebhookAsync(
        byte[] body,
        string? authorizationHeader,
        string? signatureHeader,
        CancellationToken cancellationToken = default)
    {
        _options.ValidateForWebhooks();

        var token = ExtractToken(authorizationHeader, signatureHeader);
        if (!Verify(token, body))
        {
            _metrics?.RecordRejected(Name, "invalid_signature");
            throw new BillingSignatureException("Invalid RevenueCat webhook signature.");
        }

        var result = RevenueCatWebhookParser.Parse(body, _options);
        _metrics?.RecordReceived(Name, result.Event?.EventType ?? result.Reason);

        switch (result.Kind)
        {
            case BillingWebhookKind.Transfer:
                await ProcessTransferAsync(result, cancellationToken);
                return result;

            case BillingWebhookKind.OneTimePurchase when result.Event is { } purchase:
            {
                // Credit packs must not rewrite the subscription tier (goquota 1958efa):
                // report the unchanged tier so consumers can grant credits without a downgrade.
                var existing = await _store.GetEntitlementAsync(purchase.UserId, cancellationToken);
                var currentTier = existing is null
                    ? _options.DefaultTier
                    : await _store.GetEffectiveTierAsync(purchase.UserId, cancellationToken);

                if (_callback is not null)
                {
                    await _callback(purchase with
                    {
                        PreviousTier = currentTier,
                        NewTier = currentTier
                    }, cancellationToken);
                }

                return result;
            }

            case BillingWebhookKind.Subscription when result.Event is { } subscription:
                await ApplySubscriptionAsync(subscription, cancellationToken);
                return result;

            default:
                return result;
        }
    }

    /// <summary>
    /// Reconciles one subscriber from RevenueCat. The subscriber id is path-escaped so anonymous
    /// aliases (<c>$RCAnonymousID:…</c>) and ids containing <c>/</c> or <c>?</c> stay one path
    /// segment (goquota 274468c).
    /// </summary>
    public async Task<string> SyncUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("userId is required.", nameof(userId));
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"v1/subscribers/{Uri.EscapeDataString(userId)}");

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_options.ApiKey}");
        }

        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var tier = ResolveTierFromSubscriber(document.RootElement);

        var existing = await _store.GetEntitlementAsync(userId, cancellationToken);
        var now = _time.GetUtcNow();
        await _store.SetEntitlementAsync(
            new QuotaEntitlement(
                UserId: userId,
                Tier: tier,
                SubscriptionStartDateUtc: existing?.SubscriptionStartDateUtc ?? now,
                UpdatedAtUtc: now,
                ExpiresAtUtc: existing?.ExpiresAtUtc,
                Promotion: existing?.Promotion,
                Timezone: existing?.Timezone,
                Sealed: existing?.Sealed ?? false,
                MigratedTo: existing?.MigratedTo,
                SealExpiresAtUtc: existing?.SealExpiresAtUtc),
            cancellationToken);

        return tier;
    }

    private async Task ApplySubscriptionAsync(BillingWebhookEvent evt, CancellationToken cancellationToken)
    {
        var existing = await _store.GetEntitlementAsync(evt.UserId, cancellationToken);

        // Strictly older events lose; equal timestamps are applied because several RevenueCat
        // events can share a second, and replays rewrite the same values.
        if (existing is not null && evt.EventTimestamp < existing.UpdatedAtUtc)
        {
            return;
        }

        var now = evt.EventTimestamp == default ? _time.GetUtcNow() : evt.EventTimestamp;
        var entitlement = new QuotaEntitlement(
            UserId: evt.UserId,
            Tier: evt.NewTier,
            SubscriptionStartDateUtc: evt.PurchasedAt ?? existing?.SubscriptionStartDateUtc ?? now,
            UpdatedAtUtc: now,
            ExpiresAtUtc: evt.ExpiresAt ?? existing?.ExpiresAtUtc,
            Promotion: existing?.Promotion,
            Timezone: existing?.Timezone,
            Sealed: existing?.Sealed ?? false,
            MigratedTo: existing?.MigratedTo,
            SealExpiresAtUtc: existing?.SealExpiresAtUtc);

        await _store.SetEntitlementAsync(entitlement, cancellationToken);
        _metrics?.RecordApplied(Name, evt.EventType, evt.NewTier);

        if (_callback is not null)
        {
            await _callback(evt with { PreviousTier = existing?.Tier ?? _options.DefaultTier }, cancellationToken);
        }
    }

    private async Task ProcessTransferAsync(RevenueCatParseResult result, CancellationToken cancellationToken)
    {
        var fromIds = result.TransferredFrom ?? [];
        var toIds = result.TransferredTo ?? [];
        if (fromIds.Count == 0 && toIds.Count == 0)
        {
            return;
        }

        var eventTimestamp = result.Event?.EventTimestamp ?? DateTimeOffset.UtcNow;
        var snapshot = await BestTransferSnapshotAsync(fromIds, cancellationToken);

        foreach (var destination in toIds)
        {
            if (snapshot is null)
            {
                continue;
            }

            var existing = await _store.GetEntitlementAsync(destination, cancellationToken);
            if (IsStale(existing, eventTimestamp))
            {
                continue;
            }

            var entitlement = new QuotaEntitlement(
                UserId: destination,
                Tier: snapshot.Tier,
                SubscriptionStartDateUtc: snapshot.SubscriptionStartDateUtc,
                UpdatedAtUtc: TransferUpdatedAt(eventTimestamp, existing),
                ExpiresAtUtc: snapshot.ExpiresAtUtc,
                Promotion: existing?.Promotion,
                Timezone: snapshot.Timezone ?? existing?.Timezone,
                Sealed: existing?.Sealed ?? false,
                MigratedTo: existing?.MigratedTo,
                SealExpiresAtUtc: existing?.SealExpiresAtUtc);

            await _store.SetEntitlementAsync(entitlement, cancellationToken);
            _metrics?.RecordTransfer(Name, "to");

            if (_callback is not null)
            {
                await _callback(BuildTransferEvent(result, destination, entitlement, "to"), cancellationToken);
            }
        }

        foreach (var source in fromIds)
        {
            var existing = await _store.GetEntitlementAsync(source, cancellationToken);
            if (IsStale(existing, eventTimestamp))
            {
                continue;
            }

            var entitlement = new QuotaEntitlement(
                UserId: source,
                Tier: _options.DefaultTier,
                SubscriptionStartDateUtc: existing?.SubscriptionStartDateUtc ?? DateTimeOffset.UnixEpoch,
                UpdatedAtUtc: TransferUpdatedAt(eventTimestamp, existing),
                Promotion: existing?.Promotion,
                Timezone: existing?.Timezone,
                Sealed: existing?.Sealed ?? false,
                MigratedTo: existing?.MigratedTo,
                SealExpiresAtUtc: existing?.SealExpiresAtUtc);

            await _store.SetEntitlementAsync(entitlement, cancellationToken);
            _metrics?.RecordTransfer(Name, "from");

            if (_callback is not null)
            {
                await _callback(
                    BuildTransferEvent(result, source, entitlement, "from") with
                    {
                        PreviousTier = existing?.Tier ?? _options.DefaultTier
                    },
                    cancellationToken);
            }
        }
    }

    /// <summary>
    /// Both transfer callbacks carry the provider event id: without it a consumer that dedupes on
    /// EventID answers with an error, the transfer aborts, and the source stays entitled
    /// (goquota 0ea319c).
    /// </summary>
    private static BillingWebhookEvent BuildTransferEvent(
        RevenueCatParseResult result,
        string userId,
        QuotaEntitlement entitlement,
        string role) =>
        new()
        {
            UserId = userId,
            NewTier = entitlement.Tier,
            Provider = "revenuecat",
            EventType = result.Event?.EventType ?? "TRANSFER",
            EventId = result.Event?.EventId ?? string.Empty,
            EventTimestamp = entitlement.UpdatedAtUtc,
            ExpiresAt = entitlement.ExpiresAtUtc,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["event_type"] = result.Event?.EventType ?? "TRANSFER",
                ["transfer_role"] = role
            }
        };

    private async Task<QuotaEntitlement?> BestTransferSnapshotAsync(
        IReadOnlyList<string> fromIds,
        CancellationToken cancellationToken)
    {
        QuotaEntitlement? best = null;
        foreach (var id in fromIds)
        {
            var entitlement = await _store.GetEntitlementAsync(id, cancellationToken);
            if (entitlement is null)
            {
                continue;
            }

            if (best is null || PaidRank(entitlement.Tier) > PaidRank(best.Tier))
            {
                best = entitlement;
            }
        }

        return best;
    }

    private int PaidRank(string? tier) =>
        string.IsNullOrWhiteSpace(tier) ||
        string.Equals(tier, _options.DefaultTier, StringComparison.OrdinalIgnoreCase)
            ? 0
            : 1;

    private static bool IsStale(QuotaEntitlement? existing, DateTimeOffset eventTimestamp) =>
        existing is not null && eventTimestamp != default && eventTimestamp < existing.UpdatedAtUtc;

    private static DateTimeOffset TransferUpdatedAt(DateTimeOffset eventTimestamp, QuotaEntitlement? existing) =>
        eventTimestamp != default
            ? eventTimestamp
            : existing?.UpdatedAtUtc ?? DateTimeOffset.UtcNow;

    private string ResolveTierFromSubscriber(JsonElement root)
    {
        if (!root.TryGetProperty("subscriber", out var subscriber) ||
            !subscriber.TryGetProperty("entitlements", out var entitlements) ||
            entitlements.ValueKind != JsonValueKind.Object)
        {
            return _options.DefaultTier;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var candidate in _options.EntitlementToTier.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            foreach (var property in entitlements.EnumerateObject())
            {
                if (!string.Equals(property.Name, candidate, StringComparison.OrdinalIgnoreCase) ||
                    !property.Value.TryGetProperty("is_active", out var active) ||
                    active.ValueKind != JsonValueKind.True)
                {
                    continue;
                }

                var expiresRaw = property.Value.TryGetProperty("expires_date", out var exp) && exp.ValueKind == JsonValueKind.String
                    ? exp.GetString()
                    : null;
                if (expiresRaw is null ||
                    !DateTimeOffset.TryParse(
                        expiresRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expires) ||
                    expires > now)
                {
                    return _options.EntitlementToTier[candidate];
                }
            }
        }

        return _options.DefaultTier;
    }

    private bool Verify(string? token, byte[] body)
    {
        if (string.IsNullOrWhiteSpace(_options.WebhookSecret) || string.IsNullOrWhiteSpace(token))
        {
            // An empty key would make HMAC(body, "") computable by anyone.
            return false;
        }

        if (BillingSignatures.FixedTimeEquals(token, _options.WebhookSecret))
        {
            return true;
        }

        if (!_options.AcceptHmacSignature)
        {
            return false;
        }

        try
        {
            var provided = Convert.FromBase64String(token);
            var expected = BillingSignatures.HmacSha256(_options.WebhookSecret, body);
            return BillingSignatures.FixedTimeEquals(expected, provided);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string? ExtractToken(string? authorizationHeader, string? signatureHeader)
    {
        var header = (authorizationHeader ?? string.Empty).Trim();
        if (header.Length == 0)
        {
            return (signatureHeader ?? string.Empty).Trim();
        }

        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return header["Bearer ".Length..].Trim();
        }

        return header;
    }
}
