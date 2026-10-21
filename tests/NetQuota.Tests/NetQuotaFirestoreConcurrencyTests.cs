using Google.Cloud.Firestore;
using NetQuota;
using NetQuota.Firestore;
using NetQuota;

namespace NetQuota.Tests;

/// <summary>
/// Testează implementarea Firestore reală (emulator) pentru ciclul consume+refund, singura cale
/// care rulează în producție. Se auto-skip-uiește când nu există emulator.
/// Rulează cu:
///   gcloud emulators firestore start --host-port=localhost:8085
///   $env:FIRESTORE_EMULATOR_HOST='localhost:8085'; $env:GOOGLE_CLOUD_PROJECT='demo-verifio'
///   dotnet test services/Verifio.slnx -c Release
/// </summary>
public class NetQuotaFirestoreConcurrencyTests
{
    private static readonly TimeZoneInfo Bucharest = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bucharest");

    [Fact]
    public async Task ConcurrentConsumeAndRefund_LeavesTheCounterConsistent()
    {
        var db = TryCreateEmulatorDb();
        if (db is null)
        {
            return;
        }

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
        var db = TryCreateEmulatorDb();
        if (db is null)
        {
            return;
        }

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
        var db = TryCreateEmulatorDb();
        if (db is null)
        {
            return;
        }

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
    public async Task Idempotency_UsesHashedDocIds_AndRoundTripsForeverPeriod()
    {
        var db = TryCreateEmulatorDb();
        if (db is null)
        {
            return;
        }

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
        var db = TryCreateEmulatorDb();
        if (db is null)
        {
            return;
        }

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

    private static async Task<T> WithRetryAsync<T>(Func<Task<T>> action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Grpc.Core.RpcException ex) when (
                attempt < 6 &&
                ex.StatusCode is Grpc.Core.StatusCode.Aborted
                    or Grpc.Core.StatusCode.Unavailable
                    or Grpc.Core.StatusCode.DeadlineExceeded)
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
            ProjectId = Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT") ?? "demo-verifio",
            EmulatorDetection = Google.Api.Gax.EmulatorDetection.EmulatorOrProduction
        }.Build();
    }
}
