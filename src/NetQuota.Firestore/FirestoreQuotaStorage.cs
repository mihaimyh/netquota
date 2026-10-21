using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Google.Cloud.Firestore;

namespace NetQuota.Firestore;

public sealed record FirestoreQuotaOptions
{
    public string EntitlementsCollection { get; init; } = "quota_entitlements";
    public string UsageCollection { get; init; } = "quota_usage";
    public string RateLimitsCollection { get; init; } = "quota_rate_limits";
    public string IdempotencyCollection { get; init; } = "quota_idempotency";
    public string AuditLogsCollection { get; init; } = "quota_audit_logs";
    public string TimeProbeCollection { get; init; } = "_netquota_temp";
    public TimeSpan UsageRetentionPastWindowEnd { get; init; } = TimeSpan.FromDays(7);
    public bool UseServerTimestampProbe { get; init; } = false;
}

public sealed class FirestoreQuotaStorage(
    FirestoreDb firestoreDb,
    FirestoreQuotaOptions? options = null)
    : IQuotaStorage,
      IQuotaTimeSource,
      IQuotaAuditLogger,
      IPromotionStore,
      IPromotionCapabilityReporter,
      IRemainingDrainer,
      IUserMerger
{
    private readonly FirestoreQuotaOptions _options = options ?? new FirestoreQuotaOptions();
    private readonly LocalRateLimitEngine _localFallback = new();

    public bool PromotionsSupported => true;

    public async Task<DateTimeOffset> GetNowUtcAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.UseServerTimestampProbe)
        {
            return DateTimeOffset.UtcNow;
        }

        try
        {
            var probeDoc = firestoreDb.Collection(_options.TimeProbeCollection).Document("_time_query");
            await probeDoc.SetAsync(
                new Dictionary<string, object> { ["timestamp"] = FieldValue.ServerTimestamp },
                SetOptions.MergeAll,
                cancellationToken);
            var snap = await probeDoc.GetSnapshotAsync(cancellationToken);
            if (snap.Exists)
            {
                return ReadTimestamp(snap.ToDictionary(), "timestamp", DateTimeOffset.UtcNow);
            }
        }
        catch
        {
            // Fallback to local UTC clock if probe fails
        }

        return DateTimeOffset.UtcNow;
    }

    public async Task<QuotaEntitlement?> GetEntitlementAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var doc = await EntitlementDoc(userId).GetSnapshotAsync(cancellationToken);
        if (!doc.Exists)
        {
            return null;
        }

        return ParseEntitlement(userId, doc.ToDictionary());
    }

    public async Task SetEntitlementAsync(
        QuotaEntitlement entitlement,
        CancellationToken cancellationToken = default)
    {
        var docRef = EntitlementDoc(entitlement.UserId);
        await firestoreDb.RunTransactionAsync(async tx =>
        {
            var snap = await tx.GetSnapshotAsync(docRef, cancellationToken);
            var effective = entitlement;
            if (effective.UpdatedAtUtc == default)
            {
                effective = effective with { UpdatedAtUtc = DateTimeOffset.UtcNow };
            }

            if (snap.Exists)
            {
                var existing = ParseEntitlement(entitlement.UserId, snap.ToDictionary());
                if (effective.Promotion is null && existing.Promotion is { } promo)
                {
                    effective = effective with { Promotion = promo };
                }

                if (string.IsNullOrWhiteSpace(effective.Timezone) && !string.IsNullOrWhiteSpace(existing.Timezone))
                {
                    effective = effective with { Timezone = existing.Timezone };
                }

                if (!effective.Sealed && existing.Sealed)
                {
                    effective = effective with
                    {
                        Sealed = existing.Sealed,
                        MigratedTo = existing.MigratedTo,
                        SealExpiresAtUtc = existing.SealExpiresAtUtc
                    };
                }
            }

            tx.Set(docRef, SerializeEntitlement(effective), SetOptions.MergeAll);
        }, cancellationToken: cancellationToken);
    }

    public async Task SetPromotionAsync(
        string userId,
        TierPromotion promotion,
        CancellationToken cancellationToken = default)
    {
        var docRef = EntitlementDoc(userId);
        await firestoreDb.RunTransactionAsync(async tx =>
        {
            var snap = await tx.GetSnapshotAsync(docRef, cancellationToken);
            if (!snap.Exists)
            {
                throw new QuotaException($"Entitlement for user '{userId}' was not found.");
            }

            // Write only the promotion overlay without modifying UpdatedAtUtc or base fields (goquota 599b63f)
            tx.Set(
                docRef,
                new Dictionary<string, object>
                {
                    ["promotion"] = SerializePromotion(promotion)
                },
                SetOptions.MergeAll);
        }, cancellationToken: cancellationToken);
    }

    public async Task ClearPromotionAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var docRef = EntitlementDoc(userId);
        await firestoreDb.RunTransactionAsync(async tx =>
        {
            var snap = await tx.GetSnapshotAsync(docRef, cancellationToken);
            if (!snap.Exists)
            {
                return;
            }

            // Clear only the promotion overlay without modifying UpdatedAtUtc (goquota 599b63f)
            tx.Set(
                docRef,
                new Dictionary<string, object>
                {
                    ["promotion"] = FieldValue.Delete
                },
                SetOptions.MergeAll);
        }, cancellationToken: cancellationToken);
    }

    public async Task<QuotaUsage?> GetUsageAsync(
        string userId,
        string resource,
        PeriodWindow window,
        int defaultLimit,
        CancellationToken cancellationToken = default)
    {
        var doc = await UsageDoc(userId, resource, window).GetSnapshotAsync(cancellationToken);
        if (!doc.Exists)
        {
            return new QuotaUsage(
                userId,
                resource,
                window.PeriodType,
                window.Key,
                Used: 0,
                Limit: defaultLimit,
                window.StartUtc,
                window.EndUtc,
                []);
        }

        var data = doc.ToDictionary();
        var used = ReadInt(data, "used", 0);
        var storedLimit = ReadInt(data, "limit", defaultLimit);
        var storedTier = ReadText(data, "tier");
        var effectiveLimit = window.PeriodType == PeriodType.Forever
            ? storedLimit
            : (storedLimit != 0 ? storedLimit : defaultLimit);
        var distinct = ReadStringList(data, "distinctKeys");

        return new QuotaUsage(
            userId,
            resource,
            window.PeriodType,
            window.Key,
            used,
            effectiveLimit,
            window.StartUtc,
            window.EndUtc,
            distinct,
            Tier: string.IsNullOrWhiteSpace(storedTier) ? null : storedTier);
    }

    public async Task<StorageConsumeOutcome> TryConsumeAsync(
        StorageConsumeSpec spec,
        CancellationToken cancellationToken = default)
    {
        var usageRef = UsageDoc(spec.UserId, spec.Resource, spec.Window);
        var consumeKey = !string.IsNullOrWhiteSpace(spec.IdempotencyKey)
            ? NormalizeConsumeDocKey(spec.UserId, spec.IdempotencyKey)
            : null;
        DocumentReference? idemRef = consumeKey is not null ? IdempotencyDoc(consumeKey) : null;
        DocumentReference? legacyIdemRef = consumeKey is not null ? LegacyIdempotencyDoc(consumeKey) : null;

        return await firestoreDb.RunTransactionAsync(async tx =>
        {
            if (idemRef is not null)
            {
                var idemSnap = await ReadIdempotencyAsync(tx, idemRef, legacyIdemRef!, cancellationToken);
                if (idemSnap is not null)
                {
                    var idemData = idemSnap.ToDictionary();
                    var exp = ReadTimestamp(idemData, "expiresAt", spec.NowUtc);
                    if (exp > spec.NowUtc)
                    {
                        var storedPeriod = ParseStoredPeriodType(
                            ReadText(idemData, "periodType"),
                            ReadNullableTimestamp(idemData, "periodStartUtc"),
                            ReadNullableTimestamp(idemData, "periodEndUtc"),
                            spec.Window.PeriodType);
                        return new StorageConsumeOutcome(
                            Allowed: ReadBool(idemData, "allowed", true),
                            ConsumedNewUnit: false,
                            WasIdempotencyHit: true,
                            NewUsed: ReadInt(idemData, "newUsed", 0),
                            EffectiveLimit: ReadInt(idemData, "effectiveLimit", spec.ConfiguredLimit),
                            DistinctKeys: ReadStringList(idemData, "distinctKeys"),
                            ChargedPeriod: storedPeriod);
                    }
                }
            }

            var usageSnap = await tx.GetSnapshotAsync(usageRef, cancellationToken);
            var used = 0;
            var storedLimit = spec.ConfiguredLimit;
            var storedTier = string.Empty;
            var distinct = new List<string>();

            if (usageSnap.Exists)
            {
                var data = usageSnap.ToDictionary();
                used = ReadInt(data, "used", 0);
                storedLimit = ReadInt(data, "limit", spec.ConfiguredLimit);
                storedTier = ReadText(data, "tier");
                distinct = ReadStringList(data, "distinctKeys").ToList();
            }

            int effectiveLimit;
            if (spec.Window.PeriodType == PeriodType.Forever)
            {
                effectiveLimit = usageSnap.Exists ? storedLimit : 0;
            }
            else if (spec.ConfiguredLimit == -1)
            {
                // The tier's unlimited quota wins over a stored cap (goquota 4782f75).
                effectiveLimit = -1;
            }
            else if (usageSnap.Exists &&
                     !string.IsNullOrWhiteSpace(spec.EffectiveTier) &&
                     !string.IsNullOrWhiteSpace(storedTier) &&
                     string.Equals(storedTier, spec.EffectiveTier, StringComparison.OrdinalIgnoreCase) &&
                     storedLimit != 0)
            {
                // Preserve prorated limit set by ApplyTierChangeAsync when tier matches (goquota 179602a / 20e441a)
                effectiveLimit = storedLimit;
            }
            else
            {
                effectiveLimit = spec.ConfiguredLimit;
            }

            if (!string.IsNullOrWhiteSpace(spec.DistinctKey) &&
                distinct.Contains(spec.DistinctKey, StringComparer.Ordinal))
            {
                var replay = new StorageConsumeOutcome(
                    Allowed: true,
                    ConsumedNewUnit: false,
                    WasIdempotencyHit: false,
                    NewUsed: used,
                    EffectiveLimit: effectiveLimit,
                    DistinctKeys: distinct.ToArray(),
                    ChargedPeriod: spec.Window.PeriodType);

                if (idemRef is not null)
                {
                    tx.Set(idemRef, BuildIdempotencyPayload(spec, replay));
                }

                return replay;
            }

            if (effectiveLimit != -1 && used + spec.Amount > effectiveLimit)
            {
                return new StorageConsumeOutcome(
                    Allowed: false,
                    ConsumedNewUnit: false,
                    WasIdempotencyHit: false,
                    NewUsed: used,
                    EffectiveLimit: effectiveLimit,
                    DistinctKeys: distinct.ToArray(),
                    ChargedPeriod: spec.Window.PeriodType);
            }

            if (!string.IsNullOrWhiteSpace(spec.DistinctKey))
            {
                distinct.Add(spec.DistinctKey);
                distinct.Sort(StringComparer.Ordinal);
            }

            var newUsed = used + spec.Amount;
            tx.Set(
                usageRef,
                BuildUsagePayload(
                    spec.UserId,
                    spec.Resource,
                    spec.Window,
                    newUsed,
                    effectiveLimit,
                    distinct,
                    spec.NowUtc,
                    spec.EffectiveTier ?? storedTier),
                SetOptions.MergeAll);

            var outcome = new StorageConsumeOutcome(
                Allowed: true,
                ConsumedNewUnit: true,
                WasIdempotencyHit: false,
                NewUsed: newUsed,
                EffectiveLimit: effectiveLimit,
                DistinctKeys: distinct.ToArray(),
                ChargedPeriod: spec.Window.PeriodType);

            if (idemRef is not null)
            {
                tx.Set(idemRef, BuildIdempotencyPayload(spec, outcome));
            }

            return outcome;
        }, cancellationToken: cancellationToken);
    }

    public async Task ApplyTierChangeAsync(
        TierChangeRequest request,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var usageRef = UsageDoc(request.UserId, request.Resource, request.Window);
        await firestoreDb.RunTransactionAsync(async tx =>
        {
            var snap = await tx.GetSnapshotAsync(usageRef, cancellationToken);
            var currentUsed = request.CurrentUsed;
            IReadOnlyList<string> distinct = request.DistinctKeys ?? [];

            if (snap.Exists)
            {
                var data = snap.ToDictionary();
                currentUsed = ReadInt(data, "used", request.CurrentUsed);
                if (request.DistinctKeys is null)
                {
                    distinct = ReadStringList(data, "distinctKeys");
                }
            }

            var payload = BuildUsagePayload(
                request.UserId,
                request.Resource,
                request.Window,
                currentUsed,
                request.NewLimit,
                distinct,
                now,
                request.NewTier);
            payload["previousTier"] = request.OldTier;
            payload["tierChangedAtUtc"] = Timestamp.FromDateTimeOffset(now);

            tx.Set(usageRef, payload, SetOptions.MergeAll);
        }, cancellationToken: cancellationToken);
    }

    public async Task SetUsageAsync(
        string userId,
        string resource,
        PeriodWindow window,
        int used,
        int limit,
        IReadOnlyList<string>? distinctKeys = null,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var usageRef = UsageDoc(userId, resource, window);
        var cleanDistinct = distinctKeys is not null
            ? distinctKeys.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray()
            : [];

        await usageRef.SetAsync(
            BuildUsagePayload(userId, resource, window, Math.Max(0, used), limit, cleanDistinct, now),
            SetOptions.MergeAll,
            cancellationToken);
    }

    public async Task<StorageRefundOutcome> RefundAsync(
        RefundRequest request,
        PeriodWindow window,
        int defaultLimit,
        TimeSpan idempotencyTtl,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var usageRef = UsageDoc(request.UserId, request.Resource, window);
        var refundKey = !string.IsNullOrWhiteSpace(request.IdempotencyKey)
            ? NormalizeRefundDocKey(request.UserId, request.IdempotencyKey)
            : null;
        DocumentReference? idemRef = refundKey is not null ? IdempotencyDoc(refundKey) : null;
        DocumentReference? legacyIdemRef = refundKey is not null ? LegacyIdempotencyDoc(refundKey) : null;

        return await firestoreDb.RunTransactionAsync(async tx =>
        {
            if (idemRef is not null)
            {
                var idemSnap = await ReadIdempotencyAsync(tx, idemRef, legacyIdemRef!, cancellationToken);
                if (idemSnap is not null)
                {
                    var idemData = idemSnap.ToDictionary();
                    var exp = ReadTimestamp(idemData, "expiresAt", nowUtc);
                    if (exp > nowUtc)
                    {
                        var storedPeriod = ParseStoredPeriodType(
                            ReadText(idemData, "periodType"),
                            ReadNullableTimestamp(idemData, "periodStartUtc"),
                            ReadNullableTimestamp(idemData, "periodEndUtc"),
                            window.PeriodType);
                        // Return Amount = 0 on idempotent replay (goquota d5073e8)
                        return new StorageRefundOutcome(storedPeriod, Amount: 0, Replayed: true);
                    }
                }
            }

            var snap = await tx.GetSnapshotAsync(usageRef, cancellationToken);
            if (snap.Exists)
            {
                var data = snap.ToDictionary();
                var used = ReadInt(data, "used", 0);
                var limit = ReadInt(data, "limit", defaultLimit);
                var tier = ReadText(data, "tier");
                var distinct = ReadStringList(data, "distinctKeys").ToList();

                if (!string.IsNullOrWhiteSpace(request.DistinctKeyToRemove))
                {
                    distinct.RemoveAll(k => string.Equals(k, request.DistinctKeyToRemove, StringComparison.Ordinal));
                }

                var newUsed = Math.Max(0, used - Math.Max(0, request.Amount));
                tx.Set(
                    usageRef,
                    BuildUsagePayload(request.UserId, request.Resource, window, newUsed, limit, distinct, nowUtc, tier),
                    SetOptions.MergeAll);
            }

            if (idemRef is not null)
            {
                var idemPayload = new Dictionary<string, object>
                {
                    ["operation"] = "refund",
                    ["userId"] = request.UserId,
                    ["resource"] = request.Resource,
                    ["amount"] = request.Amount,
                    ["periodType"] = window.PeriodType.ToString(),
                    ["periodKey"] = window.Key,
                    ["reason"] = request.Reason,
                    ["expiresAt"] = Timestamp.FromDateTimeOffset(nowUtc.Add(idempotencyTtl)),
                    ["updatedAtUtc"] = Timestamp.FromDateTimeOffset(nowUtc)
                };

                if (window.StartUtc is { } s)
                {
                    idemPayload["periodStartUtc"] = Timestamp.FromDateTimeOffset(s);
                }

                if (window.EndUtc is { } e)
                {
                    idemPayload["periodEndUtc"] = Timestamp.FromDateTimeOffset(e);
                }

                tx.Set(idemRef, idemPayload);
            }

            return new StorageRefundOutcome(window.PeriodType, request.Amount, Replayed: false);
        }, cancellationToken: cancellationToken);
    }

    public async Task<ConsumptionRecord?> GetConsumptionRecordAsync(
        string scopedIdempotencyKey,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(scopedIdempotencyKey))
        {
            return null;
        }

        var docKey = scopedIdempotencyKey.StartsWith("consume_", StringComparison.Ordinal)
            ? scopedIdempotencyKey
            : $"consume_{scopedIdempotencyKey}";
        var snap = await ReadIdempotencyAsync(
            IdempotencyDoc(docKey),
            LegacyIdempotencyDoc(docKey),
            cancellationToken);
        if (snap is null)
        {
            return null;
        }

        var data = snap.ToDictionary();
        var exp = ReadTimestamp(data, "expiresAt", nowUtc);
        if (exp <= nowUtc)
        {
            return null;
        }

        var startUtc = ReadNullableTimestamp(data, "periodStartUtc");
        var endUtc = ReadNullableTimestamp(data, "periodEndUtc");
        var periodType = ParseStoredPeriodType(
            ReadText(data, "periodType"),
            startUtc,
            endUtc,
            PeriodType.Monthly);
        var periodKey = ReadText(data, "periodKey", periodType == PeriodType.Forever ? "forever" : string.Empty);

        return new ConsumptionRecord(
            ConsumptionId: docKey,
            UserId: ReadText(data, "userId"),
            Resource: ReadText(data, "resource"),
            Amount: ReadInt(data, "amount", 0),
            Window: new PeriodWindow(periodType, periodKey, startUtc, endUtc),
            NewUsed: ReadInt(data, "newUsed", 0),
            EffectiveLimit: ReadInt(data, "effectiveLimit", 0),
            DistinctKeys: ReadStringList(data, "distinctKeys"),
            TimestampUtc: ReadTimestamp(data, "updatedAtUtc", nowUtc),
            Allowed: ReadBool(data, "allowed", true),
            ConsumedNewUnit: ReadBool(data, "consumedNewUnit", true));
    }

    public async Task<RefundRecord?> GetRefundRecordAsync(
        string scopedIdempotencyKey,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(scopedIdempotencyKey))
        {
            return null;
        }

        var docKey = scopedIdempotencyKey.StartsWith("refund_", StringComparison.Ordinal)
            ? scopedIdempotencyKey
            : $"refund_{scopedIdempotencyKey}";
        var snap = await ReadIdempotencyAsync(
            IdempotencyDoc(docKey),
            LegacyIdempotencyDoc(docKey),
            cancellationToken);
        if (snap is null)
        {
            return null;
        }

        var data = snap.ToDictionary();
        var exp = ReadTimestamp(data, "expiresAt", nowUtc);
        if (exp <= nowUtc)
        {
            return null;
        }

        var startUtc = ReadNullableTimestamp(data, "periodStartUtc");
        var endUtc = ReadNullableTimestamp(data, "periodEndUtc");
        var periodType = ParseStoredPeriodType(
            ReadText(data, "periodType"),
            startUtc,
            endUtc,
            PeriodType.Monthly);
        var periodKey = ReadText(data, "periodKey", periodType == PeriodType.Forever ? "forever" : string.Empty);

        return new RefundRecord(
            RefundId: docKey,
            UserId: ReadText(data, "userId"),
            Resource: ReadText(data, "resource"),
            Amount: ReadInt(data, "amount", 0),
            Window: new PeriodWindow(periodType, periodKey, startUtc, endUtc),
            TimestampUtc: ReadTimestamp(data, "updatedAtUtc", nowUtc),
            Reason: ReadText(data, "reason"));
    }

    public async Task TopUpForeverCreditsAsync(
        string userId,
        string resource,
        int amount,
        string? idempotencyKey,
        TimeSpan idempotencyTtl,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var usageRef = UsageDoc(userId, resource, PeriodWindow.Forever);
        var topUpKey = !string.IsNullOrWhiteSpace(idempotencyKey) ? $"topup_{userId}_{resource}_{idempotencyKey}" : null;
        DocumentReference? idemRef = topUpKey is not null ? IdempotencyDoc(topUpKey) : null;
        DocumentReference? legacyIdemRef = topUpKey is not null ? LegacyIdempotencyDoc(topUpKey) : null;

        await firestoreDb.RunTransactionAsync(async tx =>
        {
            if (idemRef is not null)
            {
                var idemSnap = await ReadIdempotencyAsync(tx, idemRef, legacyIdemRef!, cancellationToken);
                if (idemSnap is not null && ReadTimestamp(idemSnap.ToDictionary(), "expiresAt", nowUtc) > nowUtc)
                {
                    return;
                }
            }

            var snap = await tx.GetSnapshotAsync(usageRef, cancellationToken);
            var used = snap.Exists ? ReadInt(snap.ToDictionary(), "used", 0) : 0;
            var limit = snap.Exists ? ReadInt(snap.ToDictionary(), "limit", 0) : 0;
            var distinct = snap.Exists ? ReadStringList(snap.ToDictionary(), "distinctKeys") : [];

            tx.Set(
                usageRef,
                BuildUsagePayload(userId, resource, PeriodWindow.Forever, used, limit + Math.Max(0, amount), distinct, nowUtc),
                SetOptions.MergeAll);

            if (idemRef is not null)
            {
                tx.Set(idemRef, new Dictionary<string, object>
                {
                    ["operation"] = "topup",
                    ["userId"] = userId,
                    ["expiresAt"] = Timestamp.FromDateTimeOffset(nowUtc.Add(idempotencyTtl)),
                    ["updatedAtUtc"] = Timestamp.FromDateTimeOffset(nowUtc)
                });
            }
        }, cancellationToken: cancellationToken);
    }

    public async Task RefundForeverCreditsAsync(
        string userId,
        string resource,
        int amount,
        string? idempotencyKey,
        TimeSpan idempotencyTtl,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var usageRef = UsageDoc(userId, resource, PeriodWindow.Forever);
        var refundKey = !string.IsNullOrWhiteSpace(idempotencyKey) ? $"refund_credits_{userId}_{resource}_{idempotencyKey}" : null;
        DocumentReference? idemRef = refundKey is not null ? IdempotencyDoc(refundKey) : null;
        DocumentReference? legacyIdemRef = refundKey is not null ? LegacyIdempotencyDoc(refundKey) : null;

        await firestoreDb.RunTransactionAsync(async tx =>
        {
            if (idemRef is not null)
            {
                var idemSnap = await ReadIdempotencyAsync(tx, idemRef, legacyIdemRef!, cancellationToken);
                if (idemSnap is not null && ReadTimestamp(idemSnap.ToDictionary(), "expiresAt", nowUtc) > nowUtc)
                {
                    return;
                }
            }

            var snap = await tx.GetSnapshotAsync(usageRef, cancellationToken);
            if (!snap.Exists)
            {
                return;
            }

            var data = snap.ToDictionary();
            var used = ReadInt(data, "used", 0);
            var limit = ReadInt(data, "limit", 0);
            var distinct = ReadStringList(data, "distinctKeys");

            tx.Set(
                usageRef,
                BuildUsagePayload(userId, resource, PeriodWindow.Forever, used, Math.Max(0, limit - Math.Max(0, amount)), distinct, nowUtc),
                SetOptions.MergeAll);

            if (idemRef is not null)
            {
                tx.Set(idemRef, new Dictionary<string, object>
                {
                    ["operation"] = "refund_credits",
                    ["userId"] = userId,
                    ["expiresAt"] = Timestamp.FromDateTimeOffset(nowUtc.Add(idempotencyTtl)),
                    ["updatedAtUtc"] = Timestamp.FromDateTimeOffset(nowUtc)
                });
            }
        }, cancellationToken: cancellationToken);
    }

    public async Task DrainRemainingAsync(
        string userId,
        string resource,
        PeriodWindow window,
        int defaultLimit,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var usageRef = UsageDoc(userId, resource, window);
        await firestoreDb.RunTransactionAsync(async tx =>
        {
            var snap = await tx.GetSnapshotAsync(usageRef, cancellationToken);
            var used = snap.Exists ? Math.Max(0, ReadInt(snap.ToDictionary(), "used", 0)) : 0;
            var distinct = snap.Exists ? ReadStringList(snap.ToDictionary(), "distinctKeys") : [];
            tx.Set(
                usageRef,
                BuildUsagePayload(userId, resource, window, used, used, distinct, now),
                SetOptions.MergeAll);
        }, cancellationToken: cancellationToken);
    }

    public Task<MergeUserResult> MergeUserAsync(
        MergeUserRequest request,
        Func<PeriodType, PeriodWindow> windowResolver,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var items = new List<MergeItem>(request.Resources.Count * request.Periods.Count);
        foreach (var res in request.Resources)
        {
            foreach (var per in request.Periods)
            {
                var win = windowResolver(per);
                items.Add(new MergeItem(res, per, win, win));
            }
        }

        var sealExpiresAt = request.SealSource
            ? nowUtc.Add(request.SealTtl is { } ttl && ttl > TimeSpan.Zero ? ttl : TimeSpan.FromDays(30))
            : (DateTimeOffset?)null;

        return MergeUserAsync(
            new StorageMergeRequest(
                SourceUserId: request.SourceUserId,
                TargetUserId: request.TargetUserId,
                Items: items,
                IdempotencyKey: request.IdempotencyKey,
                SealSource: request.SealSource,
                NowUtc: nowUtc,
                SealExpiresAtUtc: sealExpiresAt),
            cancellationToken);
    }

    public async Task<MergeUserResult> MergeUserAsync(
        StorageMergeRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(request.SourceUserId, request.TargetUserId, StringComparison.Ordinal))
        {
            throw new SameUserMergeException(request.SourceUserId);
        }

        var mergeKey = !string.IsNullOrWhiteSpace(request.IdempotencyKey) ? $"merge_{request.IdempotencyKey}" : null;
        DocumentReference? idemRef = mergeKey is not null ? IdempotencyDoc(mergeKey) : null;
        DocumentReference? legacyIdemRef = mergeKey is not null ? LegacyIdempotencyDoc(mergeKey) : null;

        var srcEntRef = EntitlementDoc(request.SourceUserId);
        var tgtEntRef = EntitlementDoc(request.TargetUserId);

        return await firestoreDb.RunTransactionAsync(async tx =>
        {
            if (idemRef is not null)
            {
                var idemSnap = await ReadIdempotencyAsync(tx, idemRef, legacyIdemRef!, cancellationToken);
                if (idemSnap is not null)
                {
                    return new MergeUserResult(
                        request.SourceUserId,
                        request.TargetUserId,
                        AlreadyMerged: true,
                        new Dictionary<string, int>(),
                        new Dictionary<string, int>());
                }
            }

            var srcEntSnap = await tx.GetSnapshotAsync(srcEntRef, cancellationToken);
            var tgtEntSnap = await tx.GetSnapshotAsync(tgtEntRef, cancellationToken);

            if (srcEntSnap.Exists && IsSnapshotSealed(srcEntSnap.ToDictionary(), request.NowUtc))
            {
                throw new UserSealedException(request.SourceUserId);
            }

            if (tgtEntSnap.Exists && IsSnapshotSealed(tgtEntSnap.ToDictionary(), request.NowUtc))
            {
                throw new UserSealedException(request.TargetUserId);
            }

            // Read all snapshots first (Firestore transaction requirement: reads before writes)
            var snaps = new List<(MergeItem Item, DocumentReference SrcRef, DocumentReference TgtRef, DocumentSnapshot SrcSnap, DocumentSnapshot TgtSnap)>(request.Items.Count);
            foreach (var item in request.Items)
            {
                var srcRef = UsageDoc(request.SourceUserId, item.Resource, item.SourceWindow);
                var tgtRef = UsageDoc(request.TargetUserId, item.Resource, item.TargetWindow);
                var srcSnap = await tx.GetSnapshotAsync(srcRef, cancellationToken);
                var tgtSnap = await tx.GetSnapshotAsync(tgtRef, cancellationToken);
                snaps.Add((item, srcRef, tgtRef, srcSnap, tgtSnap));
            }

            var transferredUsage = new Dictionary<string, int>(StringComparer.Ordinal);
            var transferredForever = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var entry in snaps)
            {
                if (!entry.SrcSnap.Exists)
                {
                    continue;
                }

                var srcData = entry.SrcSnap.ToDictionary();
                var srcUsed = ReadInt(srcData, "used", 0);
                var srcLimit = ReadInt(srcData, "limit", 0);
                var srcDistinct = ReadStringList(srcData, "distinctKeys");

                var tgtData = entry.TgtSnap.Exists ? entry.TgtSnap.ToDictionary() : new Dictionary<string, object>();
                var tgtUsed = entry.TgtSnap.Exists ? ReadInt(tgtData, "used", 0) : 0;
                // Forever credits are additive: a missing target row starts at 0, not at the source limit.
                var tgtLimit = entry.TgtSnap.Exists
                    ? ReadInt(tgtData, "limit", srcLimit)
                    : (entry.Item.PeriodType == PeriodType.Forever ? 0 : srcLimit);
                var tgtDistinct = entry.TgtSnap.Exists ? ReadStringList(tgtData, "distinctKeys") : [];

                if (entry.Item.PeriodType == PeriodType.Forever)
                {
                    var remaining = Math.Max(0, srcLimit - srcUsed);
                    if (remaining > 0)
                    {
                        tx.Set(
                            entry.TgtRef,
                            BuildUsagePayload(request.TargetUserId, entry.Item.Resource, entry.Item.TargetWindow, tgtUsed, tgtLimit + remaining, tgtDistinct, request.NowUtc),
                            SetOptions.MergeAll);
                        tx.Set(
                            entry.SrcRef,
                            BuildUsagePayload(request.SourceUserId, entry.Item.Resource, entry.Item.SourceWindow, srcUsed, srcUsed, srcDistinct, request.NowUtc),
                            SetOptions.MergeAll);
                        transferredForever[entry.Item.Resource] = remaining;
                    }
                }
                else
                {
                    var mergedDistinct = tgtDistinct
                        .Concat(srcDistinct)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(x => x, StringComparer.Ordinal)
                        .ToArray();

                    var newTgtUsed = mergedDistinct.Length > 0
                        ? mergedDistinct.Length
                        : tgtUsed + srcUsed;
                    var added = Math.Max(0, newTgtUsed - tgtUsed);

                    tx.Set(
                        entry.TgtRef,
                        BuildUsagePayload(request.TargetUserId, entry.Item.Resource, entry.Item.TargetWindow, newTgtUsed, tgtLimit, mergedDistinct, request.NowUtc),
                        SetOptions.MergeAll);

                    if (request.SealSource)
                    {
                        tx.Set(
                            entry.SrcRef,
                            BuildUsagePayload(request.SourceUserId, entry.Item.Resource, entry.Item.SourceWindow, 0, srcLimit, [], request.NowUtc),
                            SetOptions.MergeAll);
                    }

                    transferredUsage[$"{entry.Item.Resource}:{entry.Item.PeriodType}"] = added;
                }
            }

            if (request.SealSource)
            {
                var sealUntil = request.SealExpiresAtUtc ?? request.NowUtc.AddDays(30);
                tx.Set(
                    srcEntRef,
                    new Dictionary<string, object>
                    {
                        ["userId"] = request.SourceUserId,
                        ["sealed"] = true,
                        ["migratedTo"] = request.TargetUserId,
                        ["sealedUntilUtc"] = Timestamp.FromDateTimeOffset(sealUntil),
                        ["updatedAtUtc"] = Timestamp.FromDateTimeOffset(request.NowUtc)
                    },
                    SetOptions.MergeAll);
            }

            if (idemRef is not null)
            {
                tx.Set(idemRef, new Dictionary<string, object>
                {
                    ["operation"] = "merge_user",
                    ["sourceUserId"] = request.SourceUserId,
                    ["targetUserId"] = request.TargetUserId,
                    ["expiresAt"] = Timestamp.FromDateTimeOffset(request.NowUtc.AddDays(365)),
                    ["updatedAtUtc"] = Timestamp.FromDateTimeOffset(request.NowUtc)
                });
            }

            return new MergeUserResult(
                request.SourceUserId,
                request.TargetUserId,
                AlreadyMerged: false,
                transferredUsage,
                transferredForever);
        }, cancellationToken: cancellationToken);
    }

    public async Task<bool> IsUserSealedAsync(
        string userId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var snap = await EntitlementDoc(userId).GetSnapshotAsync(cancellationToken);
        if (!snap.Exists)
        {
            return false;
        }

        return IsSnapshotSealed(snap.ToDictionary(), nowUtc);
    }

    public async Task<RateLimitStatus> CheckRateLimitAsync(
        string userId,
        string resource,
        RateLimitConfig config,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (config.Backend == RateLimitBackend.InMemory)
        {
            return _localFallback.Evaluate(userId, resource, config, nowUtc);
        }

        var clientRef = RateLimitDoc(resource, userId);
        DocumentReference? globalRef = config.GlobalLimit > 0
            ? RateLimitDoc(resource, "__global__")
            : null;

        return await firestoreDb.RunTransactionAsync(async tx =>
        {
            var cutoffTicks = (nowUtc - config.Window).UtcTicks;
            var expiresAt = Timestamp.FromDateTimeOffset(nowUtc.Add(config.Window).AddMinutes(5));

            List<long> globalTicks = [];
            if (globalRef is not null)
            {
                var globalSnap = await tx.GetSnapshotAsync(globalRef, cancellationToken);
                if (globalSnap.Exists)
                {
                    globalTicks = ReadLongList(globalSnap.ToDictionary(), "ticks")
                        .Where(t => t > cutoffTicks)
                        .OrderBy(t => t)
                        .ToList();
                }

                if (globalTicks.Count >= config.GlobalLimit)
                {
                    var oldestGlobal = globalTicks[0];
                    var resetGlobal = new DateTimeOffset(oldestGlobal + config.Window.Ticks, TimeSpan.Zero);
                    var retryGlobal = resetGlobal > nowUtc ? resetGlobal - nowUtc : TimeSpan.FromSeconds(1);
                    return new RateLimitStatus(false, config.GlobalLimit, 0, resetGlobal, retryGlobal);
                }
            }

            var clientSnap = await tx.GetSnapshotAsync(clientRef, cancellationToken);
            RateLimitStatus status = config.Algorithm == RateLimitAlgorithm.TokenBucket
                ? EvaluateFirestoreTokenBucket(tx, clientRef, clientSnap, resource, userId, config, nowUtc, expiresAt)
                : EvaluateFirestoreSlidingWindow(tx, clientRef, clientSnap, resource, userId, config, nowUtc, cutoffTicks, expiresAt);

            if (status.Allowed && globalRef is not null)
            {
                globalTicks.Add(nowUtc.UtcTicks);
                tx.Set(globalRef, new Dictionary<string, object>
                {
                    ["resource"] = resource,
                    ["userId"] = "__global__",
                    ["ticks"] = globalTicks.Select(t => (object)t).ToList(),
                    ["expiresAt"] = expiresAt,
                    ["updatedAtUtc"] = Timestamp.FromDateTimeOffset(nowUtc)
                });
            }

            return status;
        }, cancellationToken: cancellationToken);
    }

    private static RateLimitStatus EvaluateFirestoreTokenBucket(
        Transaction tx,
        DocumentReference clientRef,
        DocumentSnapshot clientSnap,
        string resource,
        string userId,
        RateLimitConfig config,
        DateTimeOffset nowUtc,
        Timestamp expiresAt)
    {
        var capacity = config.Burst > 0 ? config.Burst : config.Rate;
        if (config.Rate <= 0 || config.Window <= TimeSpan.Zero || capacity <= 0)
        {
            // Zero-rate guard (goquota dd6d13a / STORAGE-3-FIRESTORE)
            var fallbackWindow = config.Window > TimeSpan.Zero ? config.Window : TimeSpan.FromSeconds(1);
            return new RateLimitStatus(false, Math.Max(0, capacity), 0, nowUtc.Add(fallbackWindow), fallbackWindow);
        }

        var refillPerSecond = config.Rate / config.Window.TotalSeconds;
        var nowTicks = nowUtc.UtcTicks;
        double tokens = capacity;
        long lastRefillTicks = nowTicks;

        if (clientSnap.Exists)
        {
            var data = clientSnap.ToDictionary();
            tokens = data.TryGetValue("tokens", out var rawTokens) && rawTokens is not null
                ? Convert.ToDouble(rawTokens, CultureInfo.InvariantCulture)
                : capacity;
            lastRefillTicks = data.TryGetValue("lastRefillTicks", out var rawTicks) && rawTicks is not null
                ? Convert.ToInt64(rawTicks, CultureInfo.InvariantCulture)
                : nowTicks;

            var elapsedSeconds = Math.Max(0, TimeSpan.FromTicks(nowTicks - lastRefillTicks).TotalSeconds);
            tokens = Math.Min(capacity, tokens + elapsedSeconds * refillPerSecond);
            lastRefillTicks = nowTicks;
        }

        bool allowed;
        int remaining;
        DateTimeOffset resetAtUtc;
        TimeSpan retryAfter;

        if (tokens >= 1.0)
        {
            tokens -= 1.0;
            allowed = true;
            remaining = (int)Math.Floor(tokens);
            var secondsToFull = refillPerSecond > 0 ? (capacity - tokens) / refillPerSecond : 0;
            resetAtUtc = nowUtc.AddSeconds(Math.Ceiling(secondsToFull));
            retryAfter = TimeSpan.Zero;
        }
        else
        {
            allowed = false;
            remaining = 0;
            var missing = 1.0 - tokens;
            var waitSeconds = refillPerSecond > 0 ? Math.Max(1.0, Math.Ceiling(missing / refillPerSecond)) : 1.0;
            retryAfter = TimeSpan.FromSeconds(waitSeconds);
            resetAtUtc = nowUtc.Add(retryAfter);
        }

        tx.Set(clientRef, new Dictionary<string, object>
        {
            ["resource"] = resource,
            ["userId"] = userId,
            ["algorithm"] = "token_bucket",
            ["tokens"] = tokens,
            ["lastRefillTicks"] = lastRefillTicks,
            ["expiresAt"] = expiresAt,
            ["updatedAtUtc"] = Timestamp.FromDateTimeOffset(nowUtc)
        }, SetOptions.MergeAll);

        return new RateLimitStatus(allowed, capacity, remaining, resetAtUtc, retryAfter);
    }

    private static RateLimitStatus EvaluateFirestoreSlidingWindow(
        Transaction tx,
        DocumentReference clientRef,
        DocumentSnapshot clientSnap,
        string resource,
        string userId,
        RateLimitConfig config,
        DateTimeOffset nowUtc,
        long cutoffTicks,
        Timestamp expiresAt)
    {
        if (config.Rate <= 0 || config.Window <= TimeSpan.Zero)
        {
            var fallbackWindow = config.Window > TimeSpan.Zero ? config.Window : TimeSpan.FromSeconds(1);
            return new RateLimitStatus(false, 0, 0, nowUtc.Add(fallbackWindow), fallbackWindow);
        }

        // Prune expired sliding-window timestamps in-place (goquota 9b3a8e2 / FIRESTORE-TTL)
        var clientTicks = clientSnap.Exists
            ? ReadLongList(clientSnap.ToDictionary(), "ticks")
                .Where(t => t > cutoffTicks)
                .OrderBy(t => t)
                .ToList()
            : [];

        if (clientTicks.Count >= config.Rate)
        {
            var oldest = clientTicks[0];
            var resetUtc = new DateTimeOffset(oldest + config.Window.Ticks, TimeSpan.Zero);
            var retry = resetUtc > nowUtc ? resetUtc - nowUtc : TimeSpan.FromSeconds(1);
            return new RateLimitStatus(false, config.Rate, 0, resetUtc, retry);
        }

        clientTicks.Add(nowUtc.UtcTicks);
        tx.Set(clientRef, new Dictionary<string, object>
        {
            ["resource"] = resource,
            ["userId"] = userId,
            ["algorithm"] = "sliding_window",
            ["ticks"] = clientTicks.Select(t => (object)t).ToList(),
            ["expiresAt"] = expiresAt,
            ["updatedAtUtc"] = Timestamp.FromDateTimeOffset(nowUtc)
        });

        var remaining = Math.Max(0, config.Rate - clientTicks.Count);
        var resetWindow = new DateTimeOffset(clientTicks[0] + config.Window.Ticks, TimeSpan.Zero);
        return new RateLimitStatus(true, config.Rate, remaining, resetWindow, TimeSpan.Zero);
    }

    public async Task LogAuditEntryAsync(AuditLogEntry entry, CancellationToken cancellationToken = default)
    {
        var docId = $"{entry.TimestampUtc:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}";
        await firestoreDb.Collection(_options.AuditLogsCollection).Document(docId).SetAsync(
            new Dictionary<string, object>
            {
                ["userId"] = entry.UserId,
                ["resource"] = entry.Resource,
                ["action"] = entry.Action,
                ["amount"] = entry.Amount,
                ["actor"] = entry.Actor,
                ["reason"] = entry.Reason,
                ["timestampUtc"] = Timestamp.FromDateTimeOffset(entry.TimestampUtc)
            },
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Reads a bounded, newest-first page and applies the remaining filters in memory. Audit
    /// document ids are timestamp-prefixed, so ordering by document id gives chronological order
    /// without requiring a composite Firestore index (equality + orderBy on another field).
    /// Raise <see cref="AuditLogFilter.Limit"/> for deeper history.
    /// </summary>
    public async Task<IReadOnlyList<AuditLogEntry>> GetAuditLogsAsync(
        AuditLogFilter filter,
        CancellationToken cancellationToken = default)
    {
        var limit = Math.Max(1, filter.Limit);
        var pageSize = Math.Clamp(limit * 10, 200, 2_000);

        var snap = await firestoreDb.Collection(_options.AuditLogsCollection)
            .OrderByDescending(FieldPath.DocumentId)
            .Limit(pageSize)
            .GetSnapshotAsync(cancellationToken);

        var entries = snap.Documents.Select(d =>
        {
            var dict = d.ToDictionary();
            return new AuditLogEntry(
                UserId: ReadText(dict, "userId"),
                Resource: ReadText(dict, "resource"),
                Action: ReadText(dict, "action"),
                Amount: ReadInt(dict, "amount", 0),
                TimestampUtc: ReadTimestamp(dict, "timestampUtc", DateTimeOffset.UtcNow),
                Actor: ReadText(dict, "actor", "system"),
                Reason: ReadText(dict, "reason"));
        });

        if (!string.IsNullOrWhiteSpace(filter.UserId))
        {
            entries = entries.Where(e => string.Equals(e.UserId, filter.UserId, StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(filter.Resource))
        {
            entries = entries.Where(e => string.Equals(e.Resource, filter.Resource, StringComparison.Ordinal));
        }

        if (filter.StartTimeUtc is { } start)
        {
            entries = entries.Where(e => e.TimestampUtc >= start);
        }

        return entries.OrderByDescending(e => e.TimestampUtc).Take(limit).ToArray();
    }

    private DocumentReference EntitlementDoc(string userId) =>
        firestoreDb.Collection(_options.EntitlementsCollection).Document(SanitizeDocId(userId));

    private DocumentReference UsageDoc(string userId, string resource, PeriodWindow window) =>
        firestoreDb.Collection(_options.UsageCollection)
            .Document(SanitizeDocId($"{userId}_{resource}_{window.PeriodType}_{window.Key}"));

    private DocumentReference RateLimitDoc(string resource, string userId) =>
        firestoreDb.Collection(_options.RateLimitsCollection)
            .Document(SanitizeDocId($"{resource}_{userId}"));

    private DocumentReference IdempotencyDoc(string logicalKey) =>
        firestoreDb.Collection(_options.IdempotencyCollection).Document(HashDocId(logicalKey));

    /// <summary>
    /// Legacy id format (unsafe characters replaced by <c>_</c>). Read as a fallback for one TTL
    /// window after the hashing rollout so retries issued before the deploy still replay instead
    /// of charging twice; the documents disappear through the <c>expiresAt</c> TTL policy.
    /// </summary>
    private DocumentReference LegacyIdempotencyDoc(string logicalKey) =>
        firestoreDb.Collection(_options.IdempotencyCollection).Document(SanitizeDocId(logicalKey));

    private static async Task<DocumentSnapshot?> ReadIdempotencyAsync(
        Transaction tx,
        DocumentReference primary,
        DocumentReference legacy,
        CancellationToken cancellationToken)
    {
        var primarySnap = await tx.GetSnapshotAsync(primary, cancellationToken);
        if (primarySnap.Exists)
        {
            return primarySnap;
        }

        var legacySnap = await tx.GetSnapshotAsync(legacy, cancellationToken);
        return legacySnap.Exists ? legacySnap : null;
    }

    private static async Task<DocumentSnapshot?> ReadIdempotencyAsync(
        DocumentReference primary,
        DocumentReference legacy,
        CancellationToken cancellationToken)
    {
        var primarySnap = await primary.GetSnapshotAsync(cancellationToken);
        if (primarySnap.Exists)
        {
            return primarySnap;
        }

        var legacySnap = await legacy.GetSnapshotAsync(cancellationToken);
        return legacySnap.Exists ? legacySnap : null;
    }

    /// <summary>
    /// Stable, bounded document id for caller-controlled keys (<c>X-Request-ID</c>): SHA-256 hex,
    /// so over-long or exotic keys cannot fail the write and <c>a:b</c> / <c>a_b</c> cannot collide.
    /// </summary>
    private static string HashDocId(string rawKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant();

    private static string SanitizeDocId(string raw) =>
        raw.Replace('/', '_').Replace(' ', '_').Replace(':', '_');

    private static string NormalizeConsumeDocKey(string userId, string idempotencyKey)
    {
        var scoped = idempotencyKey.StartsWith($"{userId}:", StringComparison.Ordinal)
            ? idempotencyKey
            : $"{userId}:{idempotencyKey}";
        return $"consume_{scoped}";
    }

    private static string NormalizeRefundDocKey(string userId, string idempotencyKey)
    {
        var scoped = idempotencyKey.StartsWith($"{userId}:", StringComparison.Ordinal)
            ? idempotencyKey
            : $"{userId}:{idempotencyKey}";
        return $"refund_{scoped}";
    }

    private Dictionary<string, object> BuildUsagePayload(
        string userId,
        string resource,
        PeriodWindow window,
        int used,
        int limit,
        IReadOnlyList<string> distinctKeys,
        DateTimeOffset nowUtc,
        string? tier = null)
    {
        var payload = new Dictionary<string, object>
        {
            ["userId"] = userId,
            ["resource"] = resource,
            ["periodType"] = window.PeriodType.ToString(),
            ["periodKey"] = window.Key,
            ["used"] = used,
            ["limit"] = limit,
            ["distinctKeys"] = distinctKeys.Select(k => (object)k).ToList(),
            ["updatedAtUtc"] = Timestamp.FromDateTimeOffset(nowUtc)
        };

        if (!string.IsNullOrWhiteSpace(tier))
        {
            payload["tier"] = tier;
        }

        if (window.StartUtc is { } s)
        {
            payload["periodStartUtc"] = Timestamp.FromDateTimeOffset(s);
        }

        if (window.EndUtc is { } e)
        {
            payload["periodEndUtc"] = Timestamp.FromDateTimeOffset(e);
            payload["expiresAt"] = Timestamp.FromDateTimeOffset(e.Add(_options.UsageRetentionPastWindowEnd));
        }

        return payload;
    }

    private static Dictionary<string, object> BuildIdempotencyPayload(
        StorageConsumeSpec spec,
        StorageConsumeOutcome outcome)
    {
        var payload = new Dictionary<string, object>
        {
            ["operation"] = "consume",
            ["userId"] = spec.UserId,
            ["resource"] = spec.Resource,
            ["amount"] = spec.Amount,
            ["periodType"] = spec.Window.PeriodType.ToString(),
            ["periodKey"] = spec.Window.Key,
            ["allowed"] = outcome.Allowed,
            ["consumedNewUnit"] = outcome.ConsumedNewUnit,
            ["newUsed"] = outcome.NewUsed,
            ["effectiveLimit"] = outcome.EffectiveLimit,
            ["distinctKeys"] = outcome.DistinctKeys.Select(k => (object)k).ToList(),
            ["expiresAt"] = Timestamp.FromDateTimeOffset(spec.NowUtc.Add(spec.IdempotencyTtl)),
            ["updatedAtUtc"] = Timestamp.FromDateTimeOffset(spec.NowUtc)
        };

        if (spec.Window.StartUtc is { } s)
        {
            payload["periodStartUtc"] = Timestamp.FromDateTimeOffset(s);
        }

        if (spec.Window.EndUtc is { } e)
        {
            payload["periodEndUtc"] = Timestamp.FromDateTimeOffset(e);
        }

        return payload;
    }

    private static PeriodType ParseStoredPeriodType(
        string stored,
        DateTimeOffset? startUtc,
        DateTimeOffset? endUtc,
        PeriodType fallback)
    {
        // Explicitly parse Daily / Monthly / Forever rather than assuming Monthly (goquota 9828c99)
        if (Enum.TryParse<PeriodType>(stored, ignoreCase: true, out var parsed) && parsed != PeriodType.Auto)
        {
            return parsed;
        }

        if (startUtc is { } s && endUtc is { } e)
        {
            var span = e - s;
            if (span <= TimeSpan.FromHours(36))
            {
                return PeriodType.Daily;
            }

            if (span > TimeSpan.FromDays(3650))
            {
                return PeriodType.Forever;
            }

            return PeriodType.Monthly;
        }

        return fallback;
    }

    private static Dictionary<string, object> SerializePromotion(TierPromotion promo) =>
        new()
        {
            ["tier"] = promo.Tier,
            ["grantedAtUtc"] = Timestamp.FromDateTimeOffset(promo.GrantedAtUtc),
            ["expiresAtUtc"] = Timestamp.FromDateTimeOffset(promo.ExpiresAtUtc),
            ["source"] = promo.Source,
            ["reason"] = promo.Reason,
            ["idempotencyKey"] = promo.IdempotencyKey ?? string.Empty
        };

    private static Dictionary<string, object> SerializeEntitlement(QuotaEntitlement ent)
    {
        var dict = new Dictionary<string, object>
        {
            ["userId"] = ent.UserId,
            ["tier"] = ent.Tier,
            ["subscriptionStartDateUtc"] = Timestamp.FromDateTimeOffset(ent.SubscriptionStartDateUtc),
            ["updatedAtUtc"] = Timestamp.FromDateTimeOffset(ent.UpdatedAtUtc)
        };

        if (!string.IsNullOrWhiteSpace(ent.Timezone))
        {
            dict["timezone"] = ent.Timezone;
        }

        if (ent.ExpiresAtUtc is { } exp)
        {
            dict["expiresAtUtc"] = Timestamp.FromDateTimeOffset(exp);
        }

        if (ent.Sealed)
        {
            dict["sealed"] = true;
            if (!string.IsNullOrWhiteSpace(ent.MigratedTo))
            {
                dict["migratedTo"] = ent.MigratedTo;
            }

            if (ent.SealExpiresAtUtc is { } sealExp)
            {
                dict["sealedUntilUtc"] = Timestamp.FromDateTimeOffset(sealExp);
            }
        }

        if (ent.Promotion is { } promo)
        {
            dict["promotion"] = SerializePromotion(promo);
        }
        else
        {
            dict["promotion"] = FieldValue.Delete;
        }

        return dict;
    }

    private static QuotaEntitlement ParseEntitlement(string userId, IDictionary<string, object> data)
    {
        var tier = ReadText(data, "tier", "free");
        var subStart = ReadTimestamp(data, "subscriptionStartDateUtc", DateTimeOffset.UtcNow);
        var updated = ReadTimestamp(data, "updatedAtUtc", DateTimeOffset.UtcNow);
        DateTimeOffset? expires = data.ContainsKey("expiresAtUtc")
            ? ReadTimestamp(data, "expiresAtUtc", DateTimeOffset.UtcNow)
            : null;

        TierPromotion? promo = null;
        if (data.TryGetValue("promotion", out var rawPromo) && rawPromo is IDictionary<string, object> promoMap)
        {
            var idem = ReadText(promoMap, "idempotencyKey");
            promo = new TierPromotion(
                Tier: ReadText(promoMap, "tier", tier),
                GrantedAtUtc: ReadTimestamp(promoMap, "grantedAtUtc", updated),
                ExpiresAtUtc: ReadTimestamp(promoMap, "expiresAtUtc", updated),
                Source: ReadText(promoMap, "source", "admin"),
                Reason: ReadText(promoMap, "reason"),
                IdempotencyKey: string.IsNullOrWhiteSpace(idem) ? null : idem);
        }

        var timezone = ReadText(data, "timezone");
        var isSealed = ReadBool(data, "sealed", false) || data.ContainsKey("sealedUntilUtc");
        var migratedTo = ReadText(data, "migratedTo");
        DateTimeOffset? sealUntil = data.ContainsKey("sealedUntilUtc")
            ? ReadTimestamp(data, "sealedUntilUtc", DateTimeOffset.MinValue)
            : null;

        return new QuotaEntitlement(
            userId,
            tier,
            subStart,
            updated,
            expires,
            promo,
            string.IsNullOrWhiteSpace(timezone) ? null : timezone,
            Sealed: isSealed,
            MigratedTo: string.IsNullOrWhiteSpace(migratedTo) ? null : migratedTo,
            SealExpiresAtUtc: sealUntil);
    }

    private static bool IsSnapshotSealed(IDictionary<string, object> data, DateTimeOffset nowUtc)
    {
        if (data.ContainsKey("sealedUntilUtc"))
        {
            var until = ReadTimestamp(data, "sealedUntilUtc", DateTimeOffset.MinValue);
            return until > nowUtc;
        }

        return ReadBool(data, "sealed", false);
    }

    private static string ReadText(IDictionary<string, object> data, string key, string fallback = "") =>
        data.TryGetValue(key, out var raw) && raw is not null ? Convert.ToString(raw, CultureInfo.InvariantCulture) ?? fallback : fallback;

    private static int ReadInt(IDictionary<string, object> data, string key, int fallback) =>
        data.TryGetValue(key, out var raw) && raw is not null ? Convert.ToInt32(raw, CultureInfo.InvariantCulture) : fallback;

    private static bool ReadBool(IDictionary<string, object> data, string key, bool fallback) =>
        data.TryGetValue(key, out var raw) && raw is not null ? Convert.ToBoolean(raw, CultureInfo.InvariantCulture) : fallback;

    private static DateTimeOffset? ReadNullableTimestamp(IDictionary<string, object> data, string key) =>
        data.ContainsKey(key) ? ReadTimestamp(data, key, DateTimeOffset.MinValue) : null;

    private static DateTimeOffset ReadTimestamp(IDictionary<string, object> data, string key, DateTimeOffset fallback)
    {
        if (!data.TryGetValue(key, out var raw) || raw is null)
        {
            return fallback;
        }

        return raw switch
        {
            Timestamp ts => ts.ToDateTimeOffset(),
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            DateTimeOffset dto => dto,
            string s when DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) => parsed,
            _ => fallback
        };
    }

    private static IReadOnlyList<string> ReadStringList(IDictionary<string, object> data, string key)
    {
        if (!data.TryGetValue(key, out var raw) || raw is not IEnumerable<object> list)
        {
            return [];
        }

        return list
            .Select(v => Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<long> ReadLongList(IDictionary<string, object> data, string key)
    {
        if (!data.TryGetValue(key, out var raw) || raw is not IEnumerable<object> list)
        {
            return [];
        }

        return list
            .Select(v => Convert.ToInt64(v, CultureInfo.InvariantCulture))
            .ToArray();
    }
}
