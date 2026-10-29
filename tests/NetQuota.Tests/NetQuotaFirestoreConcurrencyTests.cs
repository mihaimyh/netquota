using Google.Cloud.Firestore;
using NetQuota;
using NetQuota.Firestore;

namespace NetQuota.Tests;

/// <summary>
/// Testează implementarea Firestore reală (emulator) pentru ciclul consume+refund, singura cale
/// care rulează în producție. Se auto-skip-uiește când nu există emulator.
/// Rulează cu:
///   gcloud emulators firestore start --host-port=localhost:8085
///   $env:FIRESTORE_EMULATOR_HOST='localhost:8085'; $env:GOOGLE_CLOUD_PROJECT='netquota-demo'
/// </summary>
public class NetQuotaFirestoreConcurrencyTests
{
    private const string FirestoreNotConfigured =
        "FIRESTORE_EMULATOR_HOST is not set; Firestore integration tests are skipped.";

    private static readonly TimeZoneInfo Bucharest = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bucharest");

    [Fact]
    public async Task ConcurrentConsumeAndRefund_LeavesTheCounterConsistent()
    {
        var maybeDb = TryCreateEmulatorDb();
        Assert.SkipWhen(maybeDb is null, FirestoreNotConfigured);
        var db = maybeDb!;

        var storage = new FirestoreQuotaStorage(db);
        var now = DateTimeOffset.UtcNow;
        var window = PeriodWindow.ForDaily(now, Bucharest);
        var userId = $"anon:test_{Guid.NewGuid():N}";
        var ct = TestContext.Current.CancellationToken;

        // The emulator serializes read-write transactions on a single document and aborts with
        // "Transaction lock timeout" when too many run at once; cap the in-flight transactions while
        // still exercising contention on the same usage doc.
        using var concurrency = new SemaphoreSlim(4);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(async i =>
        {
            await concurrency.WaitAsync(ct);
            try
            {
                var distinctKey = $"cui:{1000000 + i}";
                var consumed = await WithRetryAsync(() => storage.TryConsumeAsync(
                    new StorageConsumeSpec(
                        UserId: userId,
                        Resource: "consultare",
                        Amount: 1,
                        Window: window,
                        ConfiguredLimit: 10,
                        IdempotencyKey: null,
                        DistinctKey: distinctKey,
                        IdempotencyTtl: TimeSpan.FromHours(1),
                        NowUtc: now),
                    ct));

                if (!consumed.ConsumedNewUnit)
                {
                    return;
                }

                await WithRetryAsync(() => storage.RefundAsync(
                    new RefundRequest(
                        UserId: userId,
                        Resource: "consultare",
                        Amount: 1,
                        Period: PeriodType.Daily,
                        IdempotencyKey: null,
                        RelatedIdempotencyKey: null,
                        DistinctKeyToRemove: distinctKey,
                        Reason: "test"),
                    window,
                    defaultLimit: 10,
                    idempotencyTtl: TimeSpan.FromHours(1),
                    nowUtc: now,
                    cancellationToken: ct));
            }
            finally
            {
                concurrency.Release();
            }
        }));

        var usage = await storage.GetUsageAsync(
            userId,
            "consultare",
            window,
            defaultLimit: 10,
            ct);

        Assert.NotNull(usage);
        Assert.Equal(0, usage!.Used);
        Assert.Empty(usage.DistinctKeys);
    }

    [Fact]
    public async Task ApplyTierChange_StoresProratedLimit_AndConsumePreservesIt()
    {
        var maybeDb = TryCreateEmulatorDb();
        Assert.SkipWhen(maybeDb is null, FirestoreNotConfigured);
        var db = maybeDb!;

        var storage = new FirestoreQuotaStorage(db);
        var userId = $"u_{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var window = PeriodWindow.ForMonthly(now, now.AddDays(-15), TimeZoneInfo.Utc);
        var ct = TestContext.Current.CancellationToken;

        await storage.SetUsageAsync(userId, "api_calls", window, used: 10, limit: 100, distinctKeys: null, ct);

        await storage.ApplyTierChangeAsync(
            new TierChangeRequest(
                UserId: userId,
                Resource: "api_calls",
                OldTier: "free",
                NewTier: "pro",
                Window: window,
                OldLimit: 100,
                NewLimit: 510,
                CurrentUsed: 10,
                DistinctKeys: null),
            ct);

        var usage = await storage.GetUsageAsync(userId, "api_calls", window, defaultLimit: 100, ct);
        Assert.NotNull(usage);
        Assert.Equal(510, usage!.Limit);
        Assert.Equal(10, usage.Used);
        Assert.Equal("pro", usage.Tier);

        // A consume under the current tier must keep the prorated limit instead of resetting it to 1000.
        var outcome = await storage.TryConsumeAsync(
            new StorageConsumeSpec(
                UserId: userId,
                Resource: "api_calls",
                Amount: 1,
                Window: window,
                ConfiguredLimit: 1000,
                IdempotencyKey: null,
                DistinctKey: null,
                IdempotencyTtl: TimeSpan.FromHours(1),
                NowUtc: now,
                EffectiveTier: "pro"),
            ct);

        Assert.True(outcome.Allowed);
        Assert.Equal(11, outcome.NewUsed);
        Assert.Equal(510, outcome.EffectiveLimit);
    }

    [Fact]
    public async Task Promotion_SetAndClear_LeavesBaseFieldsUntouched()
    {
        var maybeDb = TryCreateEmulatorDb();
        Assert.SkipWhen(maybeDb is null, FirestoreNotConfigured);
        var db = maybeDb!;

        var storage = new FirestoreQuotaStorage(db);
        var userId = $"u_{Guid.NewGuid():N}";
        var updatedAt = new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);
        var now = DateTimeOffset.UtcNow;
        var ct = TestContext.Current.CancellationToken;

        await storage.SetEntitlementAsync(
            new QuotaEntitlement(userId, "free", updatedAt, updatedAt, Timezone: "Europe/Bucharest"),
            ct);

        await storage.SetPromotionAsync(
            userId,
            new TierPromotion("pro", now, now.AddDays(3), Source: "test", Reason: "comp", IdempotencyKey: "k1"),
            ct);

        var withPromo = await storage.GetEntitlementAsync(userId, ct);
        Assert.NotNull(withPromo);
        Assert.NotNull(withPromo!.Promotion);
        Assert.Equal("pro", withPromo.Promotion!.Tier);
        Assert.Equal("free", withPromo.Tier);
        Assert.Equal(updatedAt, withPromo.UpdatedAtUtc);
        Assert.Equal("Europe/Bucharest", withPromo.Timezone);

        // FieldValue.Delete must actually remove the overlay (otherwise revoke silently never clears).
        await storage.ClearPromotionAsync(userId, ct);

        var cleared = await storage.GetEntitlementAsync(userId, ct);
        Assert.NotNull(cleared);
        Assert.Null(cleared!.Promotion);
        Assert.Equal(updatedAt, cleared.UpdatedAtUtc);
        Assert.Equal("free", cleared.Tier);
    }

    [Fact]
    public async Task Entitlement_RewriteWithoutTimezone_PreservesStoredTimezone()
    {
        var maybeDb = TryCreateEmulatorDb();
        Assert.SkipWhen(maybeDb is null, FirestoreNotConfigured);
        var db = maybeDb!;

        var storage = new FirestoreQuotaStorage(db);
        var userId = $"u_{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var ct = TestContext.Current.CancellationToken;

        await storage.SetEntitlementAsync(
            new QuotaEntitlement(userId, "free", now, now, Timezone: "Europe/Bucharest"), ct);
        await storage.SetEntitlementAsync(
            new QuotaEntitlement(userId, "pro", now, now.AddMinutes(1)), ct);

        var stored = await storage.GetEntitlementAsync(userId, ct);
        Assert.Equal("Europe/Bucharest", stored!.Timezone);
        Assert.Equal("pro", stored.Tier);
    }

    [Fact]
    public async Task Idempotency_UsesHashedDocIds_AndRoundTripsForeverPeriod()
    {
        var maybeDb = TryCreateEmulatorDb();
        Assert.SkipWhen(maybeDb is null, FirestoreNotConfigured);
        var db = maybeDb!;

        var storage = new FirestoreQuotaStorage(db);
        var userId = $"u_{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var ct = TestContext.Current.CancellationToken;

        await storage.TopUpForeverCreditsAsync(userId, "credits", 5, null, TimeSpan.FromHours(1), now, ct);

        var spec = new StorageConsumeSpec(
            UserId: userId,
            Resource: "credits",
            Amount: 1,
            Window: PeriodWindow.Forever,
            ConfiguredLimit: 0,
            IdempotencyKey: "req_1",
            DistinctKey: null,
            IdempotencyTtl: TimeSpan.FromHours(1),
            NowUtc: now,
            EffectiveTier: "pro");

        var first = await storage.TryConsumeAsync(spec, ct);
        Assert.True(first.Allowed);
        Assert.False(first.WasIdempotencyHit);

        var replay = await storage.TryConsumeAsync(spec, ct);
        Assert.True(replay.WasIdempotencyHit);
        Assert.Equal(first.NewUsed, replay.NewUsed);
        Assert.Equal(PeriodType.Forever, replay.ChargedPeriod);

        var record = await storage.GetConsumptionRecordAsync($"{userId}:req_1", now, ct);
        Assert.NotNull(record);
        Assert.Equal(PeriodType.Forever, record!.Window.PeriodType);
        Assert.Equal(1, record.NewUsed);

        // Caller-controlled keys must not become raw document ids.
        var docs = await db.Collection("quota_idempotency")
            .WhereEqualTo("userId", userId)
            .GetSnapshotAsync(ct);
        var idempotencyDoc = Assert.Single(docs.Documents);
        Assert.Equal(64, idempotencyDoc.Id.Length);
        Assert.All(idempotencyDoc.Id, c => Assert.True(char.IsAsciiHexDigitLower(c)));
    }

    [Fact]
    public async Task Idempotency_ReadsLegacyDocIdDuringRollout()
    {
        var maybeDb = TryCreateEmulatorDb();
        Assert.SkipWhen(maybeDb is null, FirestoreNotConfigured);
        var db = maybeDb!;

        var storage = new FirestoreQuotaStorage(db);
        var userId = $"u_{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var ct = TestContext.Current.CancellationToken;

        // Legacy layout: sanitized logical key used verbatim as the document id.
        var legacyDocId = $"consume_{userId}_req_legacy";
        await db.Collection("quota_idempotency").Document(legacyDocId).SetAsync(
            new Dictionary<string, object>
            {
                ["operation"] = "consume",
                ["userId"] = userId,
                ["resource"] = "credits",
                ["amount"] = 2,
                ["periodType"] = "Forever",
                ["periodKey"] = "forever",
                ["allowed"] = true,
                ["consumedNewUnit"] = true,
                ["newUsed"] = 2,
                ["effectiveLimit"] = 5,
                ["distinctKeys"] = new List<object>(),
                ["expiresAt"] = Timestamp.FromDateTimeOffset(now.AddHours(1)),
                ["updatedAtUtc"] = Timestamp.FromDateTimeOffset(now)
            },
            cancellationToken: ct);

        var record = await storage.GetConsumptionRecordAsync($"{userId}:req_legacy", now, ct);
        Assert.NotNull(record);
        Assert.Equal(PeriodType.Forever, record!.Window.PeriodType);
        Assert.Equal(2, record.NewUsed);
    }

    [Fact]
    public async Task DocumentIds_DoNotCollideAcrossUserAndResourceBoundaries()
    {
        var maybeDb = TryCreateEmulatorDb();
        Assert.SkipWhen(maybeDb is null, FirestoreNotConfigured);
        var db = maybeDb!;

        var storage = new FirestoreQuotaStorage(db);
        var suffix = Guid.NewGuid().ToString("N");
        // The legacy id is "a_<suffix>_b_c_Daily_<key>" for both tuples.
        var leftUser = $"a_{suffix}_b";
        const string leftResource = "c";
        const string rightUser = "a";
        var rightResource = $"{suffix}_b_c";
        var window = PeriodWindow.ForDaily(DateTimeOffset.UtcNow, Bucharest);
        var ct = TestContext.Current.CancellationToken;

        await storage.SetUsageAsync(leftUser, leftResource, window, used: 7, limit: 10, distinctKeys: null, ct);

        var colliding = await storage.GetUsageAsync(rightUser, rightResource, window, defaultLimit: 10, ct);
        Assert.NotNull(colliding);
        Assert.Equal(0, colliding!.Used);
        Assert.Empty(colliding.DistinctKeys);

        var owner = await storage.GetUsageAsync(leftUser, leftResource, window, defaultLimit: 10, ct);
        Assert.Equal(7, owner!.Used);
    }

    [Fact]
    public async Task EntitlementIds_DoNotCollideBetweenColonAndUnderscore()
    {
        var maybeDb = TryCreateEmulatorDb();
        Assert.SkipWhen(maybeDb is null, FirestoreNotConfigured);
        var db = maybeDb!;

        var storage = new FirestoreQuotaStorage(db);
        var suffix = Guid.NewGuid().ToString("N");
        var colonUser = $"user:{suffix}";
        var underscoreUser = $"user_{suffix}";
        var now = DateTimeOffset.UtcNow;
        var ct = TestContext.Current.CancellationToken;

        await storage.SetEntitlementAsync(new QuotaEntitlement(colonUser, "pro", now, now), ct);

        Assert.Null(await storage.GetEntitlementAsync(underscoreUser, ct));
        var stored = await storage.GetEntitlementAsync(colonUser, ct);
        Assert.NotNull(stored);
        Assert.Equal("pro", stored!.Tier);
        Assert.Equal(colonUser, stored.UserId);
    }

    [Fact]
    public async Task LegacyUsageDocument_IsReadUntilTheNextWriteMigratesIt()
    {
        var maybeDb = TryCreateEmulatorDb();
        Assert.SkipWhen(maybeDb is null, FirestoreNotConfigured);
        var db = maybeDb!;

        var storage = new FirestoreQuotaStorage(db);
        var suffix = Guid.NewGuid().ToString("N");
        var userId = $"legacy_{suffix}";
        var window = PeriodWindow.ForDaily(DateTimeOffset.UtcNow, Bucharest);
        var now = DateTimeOffset.UtcNow;
        var legacyId = LegacyDocId($"{userId}_credits_Daily_{window.Key}");
        var ct = TestContext.Current.CancellationToken;

        // A document written by a previous release, under the old (sanitized) id.
        await db.Collection("quota_usage").Document(legacyId).SetAsync(
            new Dictionary<string, object>
            {
                ["userId"] = userId,
                ["resource"] = "credits",
                ["periodType"] = PeriodType.Daily.ToString(),
                ["periodKey"] = window.Key,
                ["used"] = 4,
                ["limit"] = 9,
                ["distinctKeys"] = new List<object> { "k1" },
                ["periodStartUtc"] = Timestamp.FromDateTimeOffset(window.StartUtc!.Value),
                ["periodEndUtc"] = Timestamp.FromDateTimeOffset(window.EndUtc!.Value),
                ["updatedAtUtc"] = Timestamp.FromDateTimeOffset(now)
            },
            cancellationToken: ct);

        var before = await storage.GetUsageAsync(userId, "credits", window, defaultLimit: 10, ct);
        Assert.NotNull(before);
        Assert.Equal(4, before!.Used);
        Assert.Equal(9, before.Limit);
        Assert.Equal(new[] { "k1" }, before.DistinctKeys);

        var outcome = await storage.TryConsumeAsync(
            new StorageConsumeSpec(
                UserId: userId,
                Resource: "credits",
                Amount: 1,
                Window: window,
                ConfiguredLimit: 9,
                IdempotencyKey: null,
                DistinctKey: null,
                IdempotencyTtl: TimeSpan.FromHours(1),
                NowUtc: now),
            ct);
        Assert.True(outcome.Allowed);
        Assert.Equal(5, outcome.NewUsed);

        // The write migrated the record to the hashed id; the legacy document is now stale.
        await db.Collection("quota_usage").Document(legacyId).SetAsync(
            new Dictionary<string, object> { ["used"] = 99 },
            SetOptions.MergeAll,
            ct);

        var after = await storage.GetUsageAsync(userId, "credits", window, defaultLimit: 10, ct);
        Assert.Equal(5, after!.Used);
    }

    [Fact]
    public async Task LegacyEntitlementDocument_SurvivesMigrationToTheHashedId()
    {
        var maybeDb = TryCreateEmulatorDb();
        Assert.SkipWhen(maybeDb is null, FirestoreNotConfigured);
        var db = maybeDb!;

        var storage = new FirestoreQuotaStorage(db);
        var suffix = Guid.NewGuid().ToString("N");
        var userId = $"legacy_ent_{suffix}";
        var start = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var now = DateTimeOffset.UtcNow;
        var ct = TestContext.Current.CancellationToken;

        await db.Collection("quota_entitlements").Document(LegacyDocId(userId)).SetAsync(
            new Dictionary<string, object>
            {
                ["userId"] = userId,
                ["tier"] = "pro",
                ["subscriptionStartDateUtc"] = Timestamp.FromDateTimeOffset(start),
                ["updatedAtUtc"] = Timestamp.FromDateTimeOffset(start),
                ["timezone"] = "Europe/Bucharest"
            },
            cancellationToken: ct);

        var legacy = await storage.GetEntitlementAsync(userId, ct);
        Assert.NotNull(legacy);
        Assert.Equal("pro", legacy!.Tier);

        // The promotion overlay is a partial write; it must carry the legacy base fields over.
        await storage.SetPromotionAsync(
            userId,
            new TierPromotion("enterprise", now, now.AddDays(2), Source: "test", Reason: "comp", IdempotencyKey: "p1"),
            ct);

        var migrated = await storage.GetEntitlementAsync(userId, ct);
        Assert.NotNull(migrated);
        Assert.Equal("pro", migrated!.Tier);
        Assert.Equal(start, migrated.SubscriptionStartDateUtc);
        Assert.Equal("Europe/Bucharest", migrated.Timezone);
        Assert.Equal("enterprise", migrated.Promotion!.Tier);
    }

    [Fact]
    public async Task SetEntitlement_ClearingExpiry_RemovesStoredExpiry()
    {
        var maybeDb = TryCreateEmulatorDb();
        Assert.SkipWhen(maybeDb is null, FirestoreNotConfigured);
        var db = maybeDb!;

        var storage = new FirestoreQuotaStorage(db);
        var userId = $"expiry_{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var ct = TestContext.Current.CancellationToken;

        await storage.SetEntitlementAsync(
            new QuotaEntitlement(userId, "pro", now, now, ExpiresAtUtc: now.AddHours(1)),
            ct);
        var withExpiry = await storage.GetEntitlementAsync(userId, ct);
        Assert.NotNull(withExpiry?.ExpiresAtUtc);
        Assert.InRange(
            withExpiry!.ExpiresAtUtc!.Value,
            now.AddHours(1).AddMilliseconds(-1),
            now.AddHours(1).AddMilliseconds(1));

        // A write without an expiry means "never expires"; it must not keep the old timestamp.
        await storage.SetEntitlementAsync(
            new QuotaEntitlement(userId, "pro", now, now.AddMinutes(1)),
            ct);

        var cleared = await storage.GetEntitlementAsync(userId, ct);
        Assert.NotNull(cleared);
        Assert.Null(cleared!.ExpiresAtUtc);
        Assert.Equal("pro", cleared.Tier);
    }

    [Fact]
    public async Task MergeIdempotentReplay_ReturnsOriginalTransferCounts()
    {
        var maybeDb = TryCreateEmulatorDb();
        Assert.SkipWhen(maybeDb is null, FirestoreNotConfigured);
        var db = maybeDb!;

        var storage = new FirestoreQuotaStorage(db);
        var suffix = Guid.NewGuid().ToString("N");
        var sourceUserId = $"merge_src_{suffix}";
        var targetUserId = $"merge_tgt_{suffix}";
        var now = DateTimeOffset.UtcNow;
        var ct = TestContext.Current.CancellationToken;

        await storage.TopUpForeverCreditsAsync(sourceUserId, "credits", 5, null, TimeSpan.FromHours(1), now, ct);

        var request = new StorageMergeRequest(
            SourceUserId: sourceUserId,
            TargetUserId: targetUserId,
            Items: [new MergeItem("credits", PeriodType.Forever, PeriodWindow.Forever, PeriodWindow.Forever)],
            IdempotencyKey: $"replay-{suffix}",
            SealSource: false,
            NowUtc: now);

        var first = await storage.MergeUserAsync(request, ct);
        Assert.False(first.AlreadyMerged);
        Assert.Equal(5, first.TransferredForeverCredits["credits"]);

        var replay = await storage.MergeUserAsync(request, ct);
        Assert.True(replay.AlreadyMerged);
        Assert.True(replay.TransferredForeverCredits.TryGetValue("credits", out var transferred));
        Assert.Equal(5, transferred);
        Assert.Empty(replay.TransferredUsage);
    }

    [Fact]
    public async Task MergeExpiredIdempotencyRecord_ReexecutesTheMerge()
    {
        var maybeDb = TryCreateEmulatorDb();
        Assert.SkipWhen(maybeDb is null, FirestoreNotConfigured);
        var db = maybeDb!;

        var storage = new FirestoreQuotaStorage(db);
        var suffix = Guid.NewGuid().ToString("N");
        var sourceUserId = $"merge_exp_src_{suffix}";
        var targetUserId = $"merge_exp_tgt_{suffix}";
        var now = DateTimeOffset.UtcNow;
        var ct = TestContext.Current.CancellationToken;

        await storage.TopUpForeverCreditsAsync(sourceUserId, "credits", 5, null, TimeSpan.FromHours(1), now, ct);

        var request = new StorageMergeRequest(
            SourceUserId: sourceUserId,
            TargetUserId: targetUserId,
            Items: [new MergeItem("credits", PeriodType.Forever, PeriodWindow.Forever, PeriodWindow.Forever)],
            IdempotencyKey: $"merge-expiry-{suffix}",
            SealSource: true,
            NowUtc: now,
            SealExpiresAtUtc: now.AddDays(30));

        var first = await storage.MergeUserAsync(request, ct);
        Assert.False(first.AlreadyMerged);
        Assert.Equal(5, first.TransferredForeverCredits["credits"]);

        // After the previous merge the source receives new credits; the 30-day seal has expired.
        var later = now.AddDays(366);
        await storage.TopUpForeverCreditsAsync(sourceUserId, "credits", 5, null, TimeSpan.FromHours(1), later, ct);

        var replay = await storage.MergeUserAsync(request with { NowUtc = later }, ct);
        Assert.False(replay.AlreadyMerged);
        Assert.True(replay.TransferredForeverCredits.TryGetValue("credits", out var transferred));
        Assert.Equal(5, transferred);

        var targetUsage = await storage.GetUsageAsync(targetUserId, "credits", PeriodWindow.Forever, defaultLimit: 0, ct);
        Assert.NotNull(targetUsage);
        Assert.Equal(10, targetUsage!.Limit);
    }

    [Fact]
    public async Task ConsumeAndRefundIdempotencyKeys_DoNotCollideAcrossUsers()
    {
        var maybeDb = TryCreateEmulatorDb();
        Assert.SkipWhen(maybeDb is null, FirestoreNotConfigured);
        var db = maybeDb!;

        var storage = new FirestoreQuotaStorage(db);
        var suffix = Guid.NewGuid().ToString("N");
        // ("tenant:<suffix>", "req-1") and ("tenant", "<suffix>:req-1") both produced
        // "tenant:<suffix>:req-1" under the previous colon join.
        var colonUser = $"tenant:{suffix}";
        var plainUser = "tenant";
        var resource = $"api_{suffix}";
        var now = DateTimeOffset.UtcNow;
        var window = PeriodWindow.ForDaily(now, Bucharest);
        var ct = TestContext.Current.CancellationToken;

        var first = await storage.TryConsumeAsync(
            new StorageConsumeSpec(colonUser, resource, 1, window, ConfiguredLimit: 100,
                IdempotencyKey: "req-1", DistinctKey: null,
                IdempotencyTtl: TimeSpan.FromHours(1), NowUtc: now),
            ct);
        Assert.False(first.WasIdempotencyHit);

        var second = await storage.TryConsumeAsync(
            new StorageConsumeSpec(plainUser, resource, 1, window, ConfiguredLimit: 100,
                IdempotencyKey: $"{suffix}:req-1", DistinctKey: null,
                IdempotencyTtl: TimeSpan.FromHours(1), NowUtc: now),
            ct);
        Assert.False(second.WasIdempotencyHit);

        var plainUsage = await storage.GetUsageAsync(plainUser, resource, window, defaultLimit: 100, ct);
        Assert.Equal(1, plainUsage!.Used);

        var colonRefund = await storage.RefundAsync(
            new RefundRequest(colonUser, resource, 1, PeriodType.Daily, IdempotencyKey: "refund-1"),
            window,
            defaultLimit: 100,
            idempotencyTtl: TimeSpan.FromHours(1),
            nowUtc: now,
            cancellationToken: ct);
        Assert.False(colonRefund.Replayed);

        var plainRefund = await storage.RefundAsync(
            new RefundRequest(plainUser, resource, 1, PeriodType.Daily, IdempotencyKey: $"{suffix}:refund-1"),
            window,
            defaultLimit: 100,
            idempotencyTtl: TimeSpan.FromHours(1),
            nowUtc: now,
            cancellationToken: ct);
        Assert.False(plainRefund.Replayed);
        Assert.Equal(1, plainRefund.Amount);
    }

    [Fact]
    public async Task ConsumptionRecord_DoesNotAnswerForAnotherUserThroughALegacyId()
    {
        var maybeDb = TryCreateEmulatorDb();
        Assert.SkipWhen(maybeDb is null, FirestoreNotConfigured);
        var db = maybeDb!;

        var storage = new FirestoreQuotaStorage(db);
        var suffix = Guid.NewGuid().ToString("N");
        var colonUser = $"tenant:{suffix}";
        var plainUser = "tenant";
        var now = DateTimeOffset.UtcNow;
        var ct = TestContext.Current.CancellationToken;

        // Written by a previous release: the pre-fix id was the sanitized colon join.
        var legacyDocId = LegacyDocId($"consume_{colonUser}_req-1");
        await db.Collection("quota_idempotency").Document(legacyDocId).SetAsync(
            new Dictionary<string, object>
            {
                ["operation"] = "consume",
                ["userId"] = colonUser,
                ["resource"] = "api",
                ["amount"] = 1,
                ["periodType"] = PeriodType.Daily.ToString(),
                ["periodKey"] = "2026-01-01",
                ["allowed"] = true,
                ["consumedNewUnit"] = true,
                ["newUsed"] = 1,
                ["effectiveLimit"] = 100,
                ["distinctKeys"] = new List<object>(),
                ["expiresAt"] = Timestamp.FromDateTimeOffset(now.AddHours(1)),
                ["updatedAtUtc"] = Timestamp.FromDateTimeOffset(now)
            },
            cancellationToken: ct);

        // The other identity's scope maps to the same legacy document, but the record belongs to
        // the colon user and must not be returned to it.
        var scopeForPlain = QuotaManager.ScopedIdempotencyKey(plainUser, $"{suffix}:req-1");
        var record = await storage.GetConsumptionRecordAsync(scopeForPlain, now, ct);
        Assert.Null(record);

        var scopeForOwner = QuotaManager.ScopedIdempotencyKey(colonUser, "req-1");
        var owned = await storage.GetConsumptionRecordAsync(scopeForOwner, now, ct);
        Assert.NotNull(owned);
        Assert.Equal(colonUser, owned!.UserId);
    }

    [Fact]
    public async Task MergeSealingUserWithoutEntitlement_KeepsStableSubscriptionStart()
    {
        var maybeDb = TryCreateEmulatorDb();
        Assert.SkipWhen(maybeDb is null, FirestoreNotConfigured);
        var db = maybeDb!;

        var storage = new FirestoreQuotaStorage(db);
        var suffix = Guid.NewGuid().ToString("N");
        var sourceUserId = $"seal_src_{suffix}";
        var targetUserId = $"seal_tgt_{suffix}";
        var now = DateTimeOffset.UtcNow;
        var ct = TestContext.Current.CancellationToken;

        // Usage but no entitlement document: the seal must materialize the anchor exactly once.
        await storage.SetUsageAsync(sourceUserId, "credits", PeriodWindow.Forever, used: 0, limit: 5, distinctKeys: null, ct);

        var result = await storage.MergeUserAsync(
            new StorageMergeRequest(
                SourceUserId: sourceUserId,
                TargetUserId: targetUserId,
                Items: [new MergeItem("credits", PeriodType.Forever, PeriodWindow.Forever, PeriodWindow.Forever)],
                IdempotencyKey: $"seal-{suffix}",
                SealSource: true,
                NowUtc: now),
            ct);
        Assert.False(result.AlreadyMerged);

        var firstRead = await storage.GetEntitlementAsync(sourceUserId, ct);
        await Task.Delay(50, ct);
        var secondRead = await storage.GetEntitlementAsync(sourceUserId, ct);

        Assert.NotNull(firstRead);
        Assert.NotNull(secondRead);
        Assert.Equal(firstRead!.SubscriptionStartDateUtc, secondRead!.SubscriptionStartDateUtc);
        Assert.InRange(
            firstRead.SubscriptionStartDateUtc,
            now.AddMilliseconds(-1),
            now.AddMilliseconds(1));
        Assert.True(firstRead.Sealed);
        Assert.Equal(targetUserId, firstRead.MigratedTo);
    }

    [Fact]
    public async Task Merge_PreservesTargetUsed_WhenAmountExceedsDistinctCount()
    {
        var maybeDb = TryCreateEmulatorDb();
        Assert.SkipWhen(maybeDb is null, FirestoreNotConfigured);
        var db = maybeDb!;

        var storage = new FirestoreQuotaStorage(db);
        var suffix = Guid.NewGuid().ToString("N");
        var sourceUserId = $"ism_src_{suffix}";
        var targetUserId = $"ism_tgt_{suffix}";
        var now = DateTimeOffset.UtcNow;
        var window = PeriodWindow.ForDaily(now, Bucharest);
        var ct = TestContext.Current.CancellationToken;

        await storage.TryConsumeAsync(
            new StorageConsumeSpec(targetUserId, "docs", 5, window, ConfiguredLimit: 100,
                IdempotencyKey: null, DistinctKey: "doc-1",
                IdempotencyTtl: TimeSpan.FromHours(1), NowUtc: now),
            ct);
        await storage.TryConsumeAsync(
            new StorageConsumeSpec(sourceUserId, "docs", 1, window, ConfiguredLimit: 100,
                IdempotencyKey: null, DistinctKey: "doc-2",
                IdempotencyTtl: TimeSpan.FromHours(1), NowUtc: now),
            ct);

        var result = await storage.MergeUserAsync(
            new StorageMergeRequest(
                SourceUserId: sourceUserId,
                TargetUserId: targetUserId,
                Items: [new MergeItem("docs", PeriodType.Daily, window, window)],
                IdempotencyKey: $"ism-{suffix}",
                SealSource: false,
                NowUtc: now),
            ct);

        var targetUsage = await storage.GetUsageAsync(targetUserId, "docs", window, defaultLimit: 100, ct);
        Assert.NotNull(targetUsage);
        Assert.Equal(6, targetUsage!.Used);
        Assert.Equal(new[] { "doc-1", "doc-2" }, targetUsage.DistinctKeys);
        Assert.True(result.TransferredUsage.TryGetValue("docs:Daily", out var added));
        Assert.Equal(1, added);
    }

    [Fact]
    public void ScopedIdempotencyKey_IsInjective_WhenIdentifiersContainColon()
    {
        Assert.NotEqual(
            QuotaManager.ScopedIdempotencyKey("tenant:1", "req-1"),
            QuotaManager.ScopedIdempotencyKey("tenant", "1:req-1"));
        Assert.NotEqual(
            QuotaManager.ScopedIdempotencyKey("user:1", "req-1"),
            QuotaManager.ScopedIdempotencyKey("user_1", "req-1"));
        Assert.Equal("user_1:req_1", QuotaManager.ScopedIdempotencyKey("user_1", "req_1"));
    }

    private static string LegacyDocId(string raw) =>
        raw.Replace('/', '_').Replace(' ', '_').Replace(':', '_');

    private static async Task<T> WithRetryAsync<T>(Func<Task<T>> action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (global::Grpc.Core.RpcException ex) when (
                attempt < 6 &&
                ex.StatusCode is global::Grpc.Core.StatusCode.Aborted
                    or global::Grpc.Core.StatusCode.Unavailable
                    or global::Grpc.Core.StatusCode.DeadlineExceeded)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)));
            }
        }
    }

    private static FirestoreDb? TryCreateEmulatorDb()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FIRESTORE_EMULATOR_HOST")))
        {
            return null;
        }

        return new FirestoreDbBuilder
        {
            ProjectId = Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT") ?? "netquota-demo",
            EmulatorDetection = Google.Api.Gax.EmulatorDetection.EmulatorOrProduction
        }.Build();
    }
}
