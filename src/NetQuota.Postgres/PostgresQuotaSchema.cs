using Npgsql;

namespace NetQuota.Postgres;

public sealed record PostgresQuotaOptions
{
    /// <summary>
    /// How long recurring usage rows survive past the end of their window. Forever credit rows and
    /// entitlements are never deleted.
    /// </summary>
    public TimeSpan UsageRetentionPastWindowEnd { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Create the schema on first use. Disable when migrations run out of band.</summary>
    public bool EnsureSchema { get; init; } = true;
}

/// <summary>
/// Idempotent DDL for the NetQuota tables. Safe to run on every startup: every statement is
/// <c>IF NOT EXISTS</c>, so it only creates what is missing.
/// </summary>
public static class PostgresQuotaSchema
{
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS netquota_entitlements (
            user_id text PRIMARY KEY,
            tier text NOT NULL,
            subscription_start_date_utc timestamptz NOT NULL,
            updated_at_utc timestamptz NOT NULL,
            expires_at_utc timestamptz NULL,
            timezone text NULL,
            sealed boolean NOT NULL DEFAULT false,
            migrated_to text NULL,
            sealed_until_utc timestamptz NULL,
            promotion_tier text NULL,
            promotion_granted_at_utc timestamptz NULL,
            promotion_expires_at_utc timestamptz NULL,
            promotion_source text NULL,
            promotion_reason text NULL,
            promotion_idempotency_key text NULL
        );

        CREATE TABLE IF NOT EXISTS netquota_usage (
            user_id text NOT NULL,
            resource text NOT NULL,
            period_type text NOT NULL,
            period_key text NOT NULL,
            used integer NOT NULL DEFAULT 0,
            quota_limit integer NOT NULL DEFAULT 0,
            tier text NULL,
            previous_tier text NULL,
            tier_changed_at_utc timestamptz NULL,
            period_start_utc timestamptz NULL,
            period_end_utc timestamptz NULL,
            updated_at_utc timestamptz NOT NULL,
            PRIMARY KEY (user_id, resource, period_type, period_key)
        );

        CREATE TABLE IF NOT EXISTS netquota_distinct_keys (
            user_id text NOT NULL,
            resource text NOT NULL,
            period_type text NOT NULL,
            period_key text NOT NULL,
            distinct_key text NOT NULL,
            PRIMARY KEY (user_id, resource, period_type, period_key, distinct_key)
        );

        CREATE TABLE IF NOT EXISTS netquota_consumption_records (
            idempotency_key text PRIMARY KEY,
            user_id text NOT NULL,
            resource text NOT NULL,
            amount integer NOT NULL,
            period_type text NOT NULL,
            period_key text NOT NULL,
            period_start_utc timestamptz NULL,
            period_end_utc timestamptz NULL,
            new_used integer NOT NULL,
            effective_limit integer NOT NULL,
            allowed boolean NOT NULL,
            consumed_new_unit boolean NOT NULL,
            distinct_keys jsonb NOT NULL DEFAULT '[]'::jsonb,
            timestamp_utc timestamptz NOT NULL,
            expires_at_utc timestamptz NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_netquota_consumption_expires
            ON netquota_consumption_records (expires_at_utc);

        CREATE INDEX IF NOT EXISTS ix_netquota_usage_period_end
            ON netquota_usage (period_end_utc) WHERE period_type <> 'Forever';

        CREATE TABLE IF NOT EXISTS netquota_refund_records (
            idempotency_key text PRIMARY KEY,
            user_id text NOT NULL,
            resource text NOT NULL,
            amount integer NOT NULL,
            period_type text NOT NULL,
            period_key text NOT NULL,
            period_start_utc timestamptz NULL,
            period_end_utc timestamptz NULL,
            reason text NOT NULL DEFAULT '',
            timestamp_utc timestamptz NOT NULL,
            expires_at_utc timestamptz NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_netquota_refund_expires
            ON netquota_refund_records (expires_at_utc);

        CREATE TABLE IF NOT EXISTS netquota_credit_records (
            idempotency_key text PRIMARY KEY,
            kind text NOT NULL,
            user_id text NOT NULL,
            resource text NOT NULL,
            amount integer NOT NULL,
            timestamp_utc timestamptz NOT NULL,
            expires_at_utc timestamptz NOT NULL
        );

        CREATE TABLE IF NOT EXISTS netquota_merge_records (
            idempotency_key text PRIMARY KEY,
            source_user_id text NOT NULL,
            target_user_id text NOT NULL,
            result jsonb NOT NULL,
            timestamp_utc timestamptz NOT NULL
        );

        CREATE TABLE IF NOT EXISTS netquota_audit_logs (
            id bigserial PRIMARY KEY,
            user_id text NOT NULL,
            resource text NOT NULL,
            action text NOT NULL,
            amount integer NOT NULL,
            actor text NOT NULL,
            reason text NOT NULL,
            timestamp_utc timestamptz NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_netquota_audit_user_time
            ON netquota_audit_logs (user_id, timestamp_utc DESC);
        """;

    public static async Task EnsureCreatedAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Concurrent replica startups run this DDL at the same time; CREATE ... IF NOT EXISTS is
        // not race-free in PostgreSQL and the loser would crash on a duplicate catalog entry.
        await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(8123456789)", connection, transaction))
        {
            await gate.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var ddl = new NpgsqlCommand(Sql, connection, transaction))
        {
            await ddl.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
