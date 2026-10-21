using Npgsql;

namespace NetQuota.Postgres;

public sealed partial class PostgresQuotaStorage
{
    public async Task<QuotaEntitlement?> GetEntitlementAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, null, """
            SELECT tier, subscription_start_date_utc, updated_at_utc, expires_at_utc, timezone,
                   sealed, migrated_to, sealed_until_utc,
                   promotion_tier, promotion_granted_at_utc, promotion_expires_at_utc,
                   promotion_source, promotion_reason, promotion_idempotency_key
            FROM netquota_entitlements WHERE user_id = @user
            """, ("user", userId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        TierPromotion? promotion = null;
        if (!reader.IsDBNull(8))
        {
            promotion = new TierPromotion(
                Tier: reader.GetString(8),
                GrantedAtUtc: ReadDate(reader, 9) ?? DateTimeOffset.UtcNow,
                ExpiresAtUtc: ReadDate(reader, 10) ?? DateTimeOffset.UtcNow,
                Source: reader.IsDBNull(11) ? "admin" : reader.GetString(11),
                Reason: reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
                IdempotencyKey: reader.IsDBNull(13) ? null : reader.GetString(13));
        }

        return new QuotaEntitlement(
            UserId: userId,
            Tier: reader.GetString(0),
            SubscriptionStartDateUtc: ReadDate(reader, 1) ?? DateTimeOffset.UtcNow,
            UpdatedAtUtc: ReadDate(reader, 2) ?? DateTimeOffset.UtcNow,
            ExpiresAtUtc: ReadDate(reader, 3),
            Promotion: promotion,
            Timezone: reader.IsDBNull(4) ? null : reader.GetString(4),
            Sealed: !reader.IsDBNull(5) && reader.GetBoolean(5),
            MigratedTo: reader.IsDBNull(6) ? null : reader.GetString(6),
            SealExpiresAtUtc: ReadDate(reader, 7));
    }

    public async Task SetEntitlementAsync(
        QuotaEntitlement entitlement,
        CancellationToken cancellationToken = default)
    {
        var effective = entitlement.UpdatedAtUtc == default
            ? entitlement with { UpdatedAtUtc = DateTimeOffset.UtcNow }
            : entitlement;

        // A base-entitlement write never erases an active promotion or a merge tombstone
        // (goquota 599b63f); clearing is ClearPromotion's job and unsealing is explicit.
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, null, """
            INSERT INTO netquota_entitlements (
                user_id, tier, subscription_start_date_utc, updated_at_utc, expires_at_utc, timezone,
                sealed, migrated_to, sealed_until_utc,
                promotion_tier, promotion_granted_at_utc, promotion_expires_at_utc,
                promotion_source, promotion_reason, promotion_idempotency_key)
            VALUES (
                @user, @tier, @subStart, @updated, @expires, @timezone,
                @sealed, @migratedTo, @sealedUntil,
                @promoTier, @promoGranted, @promoExpires, @promoSource, @promoReason, @promoKey)
            ON CONFLICT (user_id) DO UPDATE SET
                tier = EXCLUDED.tier,
                subscription_start_date_utc = EXCLUDED.subscription_start_date_utc,
                updated_at_utc = EXCLUDED.updated_at_utc,
                expires_at_utc = EXCLUDED.expires_at_utc,
                timezone = EXCLUDED.timezone,
                sealed = CASE WHEN EXCLUDED.sealed THEN true ELSE netquota_entitlements.sealed END,
                migrated_to = CASE WHEN netquota_entitlements.sealed THEN netquota_entitlements.migrated_to
                                   ELSE EXCLUDED.migrated_to END,
                sealed_until_utc = CASE WHEN netquota_entitlements.sealed THEN netquota_entitlements.sealed_until_utc
                                        ELSE EXCLUDED.sealed_until_utc END,
                promotion_tier = COALESCE(EXCLUDED.promotion_tier, netquota_entitlements.promotion_tier),
                promotion_granted_at_utc = COALESCE(EXCLUDED.promotion_granted_at_utc, netquota_entitlements.promotion_granted_at_utc),
                promotion_expires_at_utc = COALESCE(EXCLUDED.promotion_expires_at_utc, netquota_entitlements.promotion_expires_at_utc),
                promotion_source = COALESCE(EXCLUDED.promotion_source, netquota_entitlements.promotion_source),
                promotion_reason = COALESCE(EXCLUDED.promotion_reason, netquota_entitlements.promotion_reason),
                promotion_idempotency_key = COALESCE(EXCLUDED.promotion_idempotency_key, netquota_entitlements.promotion_idempotency_key)
            """,
            ("user", effective.UserId),
            ("tier", effective.Tier),
            ("subStart", effective.SubscriptionStartDateUtc),
            ("updated", effective.UpdatedAtUtc),
            ("expires", effective.ExpiresAtUtc),
            ("timezone", effective.Timezone),
            ("sealed", effective.Sealed),
            ("migratedTo", effective.MigratedTo),
            ("sealedUntil", effective.SealExpiresAtUtc),
            ("promoTier", effective.Promotion?.Tier),
            ("promoGranted", effective.Promotion?.GrantedAtUtc),
            ("promoExpires", effective.Promotion?.ExpiresAtUtc),
            ("promoSource", effective.Promotion?.Source),
            ("promoReason", effective.Promotion?.Reason),
            ("promoKey", effective.Promotion?.IdempotencyKey));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetPromotionAsync(
        string userId,
        TierPromotion promotion,
        CancellationToken cancellationToken = default)
    {
        // Deliberately does not touch updated_at_utc: billing-provider webhooks rely on that
        // timestamp for their own idempotency (goquota 599b63f).
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, null, """
            UPDATE netquota_entitlements SET
                promotion_tier = @tier,
                promotion_granted_at_utc = @granted,
                promotion_expires_at_utc = @expires,
                promotion_source = @source,
                promotion_reason = @reason,
                promotion_idempotency_key = @key
            WHERE user_id = @user
            """,
            ("user", userId),
            ("tier", promotion.Tier),
            ("granted", promotion.GrantedAtUtc),
            ("expires", promotion.ExpiresAtUtc),
            ("source", promotion.Source),
            ("reason", promotion.Reason),
            ("key", promotion.IdempotencyKey));

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected == 0)
        {
            throw new QuotaException($"Entitlement for user '{userId}' was not found.");
        }
    }

    public async Task ClearPromotionAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, null, """
            UPDATE netquota_entitlements SET
                promotion_tier = NULL,
                promotion_granted_at_utc = NULL,
                promotion_expires_at_utc = NULL,
                promotion_source = NULL,
                promotion_reason = NULL,
                promotion_idempotency_key = NULL
            WHERE user_id = @user
            """, ("user", userId));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<bool> IsSealedCoreAsync(
        NpgsqlConnection? connection,
        NpgsqlTransaction? tx,
        string userId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var owned = connection is null;
        connection ??= await _dataSource.OpenConnectionAsync(cancellationToken);

        try
        {
            await using var command = Command(connection, tx,
                "SELECT sealed, sealed_until_utc FROM netquota_entitlements WHERE user_id = @user",
                ("user", userId));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0) || !reader.GetBoolean(0))
            {
                return false;
            }

            var until = ReadDate(reader, 1);
            return until is null || until > nowUtc;
        }
        finally
        {
            if (owned)
            {
                await connection.DisposeAsync();
            }
        }
    }
}
