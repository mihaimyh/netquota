using Npgsql;

namespace NetQuota.Postgres;

public sealed partial class PostgresQuotaStorage
{
    public async Task LogAuditEntryAsync(AuditLogEntry entry, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, null, """
            INSERT INTO netquota_audit_logs (user_id, resource, action, amount, actor, reason, timestamp_utc)
            VALUES (@user, @resource, @action, @amount, @actor, @reason, @timestamp)
            """,
            ("user", entry.UserId),
            ("resource", entry.Resource),
            ("action", entry.Action),
            ("amount", entry.Amount),
            ("actor", entry.Actor),
            ("reason", entry.Reason),
            ("timestamp", entry.TimestampUtc));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AuditLogEntry>> GetAuditLogsAsync(
        AuditLogFilter filter,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Filters are pushed into SQL and LIMIT is applied last, so a filtered query is never
        // truncated by unrelated rows.
        var where = new List<string>();
        var args = new List<(string, object?)>();

        if (!string.IsNullOrWhiteSpace(filter.UserId))
        {
            where.Add("user_id = @user");
            args.Add(("user", filter.UserId));
        }

        if (!string.IsNullOrWhiteSpace(filter.Resource))
        {
            where.Add("resource = @resource");
            args.Add(("resource", filter.Resource));
        }

        if (filter.StartTimeUtc is { } start)
        {
            where.Add("timestamp_utc >= @start");
            args.Add(("start", start));
        }

        var sql = $"""
            SELECT user_id, resource, action, amount, actor, reason, timestamp_utc
            FROM netquota_audit_logs
            {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : string.Empty)}
            ORDER BY timestamp_utc DESC, id DESC
            LIMIT @limit
            """;

        args.Add(("limit", Math.Max(1, filter.Limit)));

        await using var command = Command(connection, null, sql, [.. args]);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var entries = new List<AuditLogEntry>();
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new AuditLogEntry(
                UserId: reader.GetString(0),
                Resource: reader.GetString(1),
                Action: reader.GetString(2),
                Amount: reader.GetInt32(3),
                TimestampUtc: ReadDate(reader, 6) ?? DateTimeOffset.UtcNow,
                Actor: reader.GetString(4),
                Reason: reader.GetString(5)));
        }

        return entries;
    }
}
