using StackExchange.Redis;

namespace NetQuota.Redis;

public sealed partial class RedisQuotaStorage
{
    private static readonly string ConsumeScript = """
        local usageKey = KEYS[1]
        local distinctKey = KEYS[2]
        local consumptionKey = KEYS[3]

        local amount = tonumber(ARGV[1])
        local configuredLimit = tonumber(ARGV[2])
        local tier = ARGV[3]
        local distinctMember = ARGV[4]
        local nowIso = ARGV[5]
        local consumptionTtl = tonumber(ARGV[6])
        local usageTtl = tonumber(ARGV[7])
        local userId = ARGV[8]
        local resource = ARGV[9]
        local periodType = ARGV[10]
        local periodKey = ARGV[11]
        local periodStart = ARGV[12]
        local periodEnd = ARGV[13]

        if amount < 0 then amount = 0 end

        local members = redis.call('SMEMBERS', distinctKey)
        local membersJson = cjson.encode(members)

        if consumptionKey ~= '' and redis.call('EXISTS', consumptionKey) == 1 then
            local record = redis.call('HMGET', consumptionKey, 'newUsed', 'effectiveLimit', 'periodType')
            if record[1] then
                return { 2, tonumber(record[1]), tonumber(record[2] or '0'), record[3] or periodType, membersJson }
            end
        end

        local used = tonumber(redis.call('HGET', usageKey, 'used') or '0')
        local storedLimit = redis.call('HGET', usageKey, 'limit')
        local storedTier = redis.call('HGET', usageKey, 'tier')

        local limit = configuredLimit
        if periodType == 'Forever' then
            limit = tonumber(storedLimit or '0')
        elseif configuredLimit == -1 then
            -- The tier's unlimited quota wins over a stored cap (goquota 4782f75).
            limit = -1
        elseif storedLimit and storedTier and tier ~= '' and string.lower(storedTier) == string.lower(tier) and tonumber(storedLimit) ~= 0 then
            -- Preserve prorated limits written by ApplyTierChange for the current tier.
            limit = tonumber(storedLimit)
        end

        if distinctMember ~= '' and redis.call('SISMEMBER', distinctKey, distinctMember) == 1 then
            if consumptionKey ~= '' then
                redis.call('HSET', consumptionKey,
                    'userId', userId, 'resource', resource, 'amount', amount,
                    'newUsed', used, 'effectiveLimit', limit, 'allowed', 1, 'consumedNewUnit', 0,
                    'periodType', periodType, 'periodKey', periodKey,
                    'periodStartUtc', periodStart, 'periodEndUtc', periodEnd,
                    'updatedAtUtc', nowIso, 'distinctKeys', membersJson)
                if consumptionTtl > 0 then redis.call('EXPIRE', consumptionKey, consumptionTtl) end
            end
            return { 3, used, limit, periodType, membersJson }
        end

        if limit ~= -1 and used + amount > limit then
            return { 0, used, limit, periodType, membersJson }
        end

        local newUsed = used + amount
        redis.call('HSET', usageKey,
            'used', newUsed, 'limit', limit, 'tier', tier,
            'periodType', periodType, 'periodKey', periodKey,
            'periodStartUtc', periodStart, 'periodEndUtc', periodEnd,
            'updatedAtUtc', nowIso)
        if distinctMember ~= '' then redis.call('SADD', distinctKey, distinctMember) end
        if usageTtl > 0 then redis.call('EXPIRE', usageKey, usageTtl) end

        membersJson = cjson.encode(redis.call('SMEMBERS', distinctKey))

        if consumptionKey ~= '' then
            redis.call('HSET', consumptionKey,
                'userId', userId, 'resource', resource, 'amount', amount,
                'newUsed', newUsed, 'effectiveLimit', limit, 'allowed', 1, 'consumedNewUnit', 1,
                'periodType', periodType, 'periodKey', periodKey,
                'periodStartUtc', periodStart, 'periodEndUtc', periodEnd,
                'updatedAtUtc', nowIso, 'distinctKeys', membersJson)
            if consumptionTtl > 0 then redis.call('EXPIRE', consumptionKey, consumptionTtl) end
        end

        return { 1, newUsed, limit, periodType, membersJson }
        """;

    private static readonly string RefundScript = """
        local usageKey = KEYS[1]
        local distinctKey = KEYS[2]
        local refundKey = KEYS[3]

        local amount = tonumber(ARGV[1])
        local distinctMember = ARGV[2]
        local nowIso = ARGV[3]
        local refundTtl = tonumber(ARGV[4])
        local userId = ARGV[5]
        local resource = ARGV[6]
        local periodType = ARGV[7]
        local periodKey = ARGV[8]
        local periodStart = ARGV[9]
        local periodEnd = ARGV[10]
        local reason = ARGV[11]

        if amount < 0 then amount = 0 end

        if refundKey ~= '' and redis.call('EXISTS', refundKey) == 1 then
            local stored = redis.call('HGET', refundKey, 'periodType')
            return { 2, 0, stored or periodType }
        end

        if redis.call('EXISTS', usageKey) == 1 then
            local used = tonumber(redis.call('HGET', usageKey, 'used') or '0')
            local newUsed = used - amount
            if newUsed < 0 then newUsed = 0 end
            redis.call('HSET', usageKey, 'used', newUsed, 'updatedAtUtc', nowIso)
        end

        if distinctMember ~= '' then redis.call('SREM', distinctKey, distinctMember) end

        if refundKey ~= '' then
            redis.call('HSET', refundKey,
                'userId', userId, 'resource', resource, 'amount', amount,
                'periodType', periodType, 'periodKey', periodKey,
                'periodStartUtc', periodStart, 'periodEndUtc', periodEnd,
                'reason', reason, 'updatedAtUtc', nowIso)
            if refundTtl > 0 then redis.call('EXPIRE', refundKey, refundTtl) end
        end

        return { 1, amount, periodType }
        """;

    private static readonly string TopUpScript = """
        local usageKey = KEYS[1]
        local topUpKey = KEYS[2]
        local amount = tonumber(ARGV[1])
        local idempotencyTtl = tonumber(ARGV[2])
        local nowIso = ARGV[3]

        if amount < 0 then amount = 0 end

        if topUpKey ~= '' and redis.call('EXISTS', topUpKey) == 1 then
            return 0
        end

        local limit = tonumber(redis.call('HGET', usageKey, 'limit') or '0')
        redis.call('HSET', usageKey, 'limit', limit + amount, 'updatedAtUtc', nowIso)
        if topUpKey ~= '' and idempotencyTtl > 0 then
            redis.call('SET', topUpKey, '1')
            redis.call('EXPIRE', topUpKey, idempotencyTtl)
        end
        return 1
        """;

    private static readonly string RefundCreditsScript = """
        local usageKey = KEYS[1]
        local refundKey = KEYS[2]
        local amount = tonumber(ARGV[1])
        local idempotencyTtl = tonumber(ARGV[2])
        local nowIso = ARGV[3]

        if amount < 0 then amount = 0 end

        if refundKey ~= '' and redis.call('EXISTS', refundKey) == 1 then
            return 0
        end

        if redis.call('EXISTS', usageKey) == 1 then
            local limit = tonumber(redis.call('HGET', usageKey, 'limit') or '0')
            local nextLimit = limit - amount
            if nextLimit < 0 then nextLimit = 0 end
            redis.call('HSET', usageKey, 'limit', nextLimit, 'updatedAtUtc', nowIso)
        end

        if refundKey ~= '' and idempotencyTtl > 0 then
            redis.call('SET', refundKey, '1')
            redis.call('EXPIRE', refundKey, idempotencyTtl)
        end
        return 1
        """;

    private static readonly string DrainScript = """
        local usageKey = KEYS[1]
        local nowIso = ARGV[1]

        local used = 0
        if redis.call('EXISTS', usageKey) == 1 then
            used = tonumber(redis.call('HGET', usageKey, 'used') or '0')
        end
        if used < 0 then used = 0 end

        redis.call('HSET', usageKey, 'used', used, 'limit', used, 'updatedAtUtc', nowIso)
        return 1
        """;

    private static readonly string SetEntitlementScript = """
        local key = KEYS[1]

        -- An incoming entitlement without a promotion must never erase an active overlay:
        -- clearing a promotion is ClearPromotion's job (goquota 599b63f).
        local sealed = ARGV[7]
        if sealed == '0' and redis.call('HGET', key, 'sealed') == '1' then
            sealed = '1'
        end

        redis.call('HSET', key, 'userId', ARGV[1], 'tier', ARGV[2],
            'subscriptionStartDateUtc', ARGV[3], 'updatedAtUtc', ARGV[4], 'sealed', sealed)

        if ARGV[5] ~= '' then redis.call('HSET', key, 'expiresAtUtc', ARGV[5])
        else redis.call('HDEL', key, 'expiresAtUtc') end

        if ARGV[6] ~= '' then redis.call('HSET', key, 'timezone', ARGV[6]) end

        -- Keep a merge tombstone while the account is still sealed.
        if ARGV[8] ~= '' then redis.call('HSET', key, 'migratedTo', ARGV[8])
        elseif sealed ~= '1' then redis.call('HDEL', key, 'migratedTo') end

        if ARGV[9] ~= '' then redis.call('HSET', key, 'sealedUntilUtc', ARGV[9])
        elseif sealed ~= '1' then redis.call('HDEL', key, 'sealedUntilUtc') end

        if ARGV[10] == '1' then
            redis.call('HSET', key, 'promotionTier', ARGV[11], 'promotionGrantedAtUtc', ARGV[12],
                'promotionExpiresAtUtc', ARGV[13], 'promotionSource', ARGV[14],
                'promotionReason', ARGV[15], 'promotionIdempotencyKey', ARGV[16])
        end

        if tonumber(ARGV[17]) > 0 then redis.call('EXPIRE', key, tonumber(ARGV[17])) end
        return 1
        """;

    private static readonly string SetPromotionScript = """
        local key = KEYS[1]
        if redis.call('EXISTS', key) == 0 then
            return 0
        end
        redis.call('HSET', key, 'promotionTier', ARGV[1], 'promotionGrantedAtUtc', ARGV[2],
            'promotionExpiresAtUtc', ARGV[3], 'promotionSource', ARGV[4],
            'promotionReason', ARGV[5], 'promotionIdempotencyKey', ARGV[6])
        return 1
        """;

    private static readonly string ClearPromotionScript = """
        local key = KEYS[1]
        if redis.call('EXISTS', key) == 0 then
            return 0
        end
        redis.call('HDEL', key, 'promotionTier', 'promotionGrantedAtUtc', 'promotionExpiresAtUtc',
            'promotionSource', 'promotionReason', 'promotionIdempotencyKey')
        return 1
        """;

    private static readonly string ApplyTierChangeScript = """
        local key = KEYS[1]
        local distinctKey = KEYS[2]

        if redis.call('EXISTS', key) == 0 then
            redis.call('HSET', key, 'used', ARGV[6])
        end

        redis.call('HSET', key, 'limit', ARGV[1], 'tier', ARGV[2],
            'previousTier', ARGV[3], 'tierChangedAtUtc', ARGV[4], 'updatedAtUtc', ARGV[4])

        if tonumber(ARGV[5]) > 0 then redis.call('EXPIRE', key, tonumber(ARGV[5])) end

        if ARGV[7] == '1' then
            redis.call('DEL', distinctKey)
            for i = 8, #ARGV do
                redis.call('SADD', distinctKey, ARGV[i])
            end
        end
        return 1
        """;

    private static readonly string SetUsageScript = """
        local key = KEYS[1]
        local distinctKey = KEYS[2]
        redis.call('HSET', key, 'used', ARGV[1], 'limit', ARGV[2], 'updatedAtUtc', ARGV[3])
        if ARGV[4] ~= '' then redis.call('HSET', key, 'tier', ARGV[4]) end
        if tonumber(ARGV[5]) > 0 then redis.call('EXPIRE', key, tonumber(ARGV[5])) end
        redis.call('DEL', distinctKey)
        for i = 6, #ARGV do
            redis.call('SADD', distinctKey, ARGV[i])
        end
        return 1
        """;

    private static readonly string SlidingWindowScript = """
        local clientKey = KEYS[1]
        local globalKey = KEYS[2]
        local nowMs = tonumber(ARGV[1])
        local limit = tonumber(ARGV[2])
        local windowMs = tonumber(ARGV[3])
        local ttl = tonumber(ARGV[4])
        local member = ARGV[5]
        local globalLimit = tonumber(ARGV[6])

        if limit <= 0 or windowMs <= 0 then
            return { 0, 0, nowMs + windowMs }
        end

        local cutoff = nowMs - windowMs

        if globalKey ~= '' and globalLimit > 0 then
            redis.call('ZREMRANGEBYSCORE', globalKey, '-inf', cutoff)
            if redis.call('ZCARD', globalKey) >= globalLimit then
                local oldest = redis.call('ZRANGE', globalKey, 0, 0, 'WITHSCORES')
                local reset = nowMs + windowMs
                if oldest and #oldest >= 2 then reset = tonumber(oldest[2]) + windowMs end
                if ttl > 0 then redis.call('EXPIRE', globalKey, ttl) end
                return { 0, 0, reset }
            end
        end

        redis.call('ZREMRANGEBYSCORE', clientKey, '-inf', cutoff)
        local count = redis.call('ZCARD', clientKey)
        if count >= limit then
            local oldest = redis.call('ZRANGE', clientKey, 0, 0, 'WITHSCORES')
            local reset = nowMs + windowMs
            if oldest and #oldest >= 2 then reset = tonumber(oldest[2]) + windowMs end
            if ttl > 0 then redis.call('EXPIRE', clientKey, ttl) end
            return { 0, 0, reset }
        end

        redis.call('ZADD', clientKey, nowMs, member)
        if ttl > 0 then redis.call('EXPIRE', clientKey, ttl) end
        if globalKey ~= '' and globalLimit > 0 then
            redis.call('ZADD', globalKey, nowMs, member)
            if ttl > 0 then redis.call('EXPIRE', globalKey, ttl) end
        end

        local oldest = redis.call('ZRANGE', clientKey, 0, 0, 'WITHSCORES')
        local reset = nowMs + windowMs
        if oldest and #oldest >= 2 then reset = tonumber(oldest[2]) + windowMs end
        return { 1, limit - count - 1, reset }
        """;

    private static readonly string TokenBucketScript = """
        local key = KEYS[1]
        local nowMs = tonumber(ARGV[1])
        local rate = tonumber(ARGV[2])
        local windowMs = tonumber(ARGV[3])
        local burst = tonumber(ARGV[4])
        local ttl = tonumber(ARGV[5])

        if rate <= 0 or windowMs <= 0 or burst <= 0 then
            return { 0, 0, nowMs + windowMs }
        end

        local state = redis.call('HMGET', key, 'tokens', 'lastRefillMs')
        local tokens = burst
        local lastRefill = nowMs
        if state[1] and state[2] then
            tokens = tonumber(state[1]) or burst
            lastRefill = tonumber(state[2]) or nowMs
        end

        local elapsed = nowMs - lastRefill
        if elapsed > 0 then
            tokens = math.min(burst, tokens + (elapsed * rate / windowMs))
            lastRefill = nowMs
        end

        local allowed = 0
        local remaining = 0
        local reset = nowMs + windowMs
        if tokens >= 1 then
            tokens = tokens - 1
            allowed = 1
            remaining = math.floor(tokens)
            reset = nowMs + math.ceil((burst - tokens) * windowMs / rate)
        else
            reset = nowMs + math.ceil((1 - tokens) * windowMs / rate)
        end

        redis.call('HSET', key, 'tokens', tostring(tokens), 'lastRefillMs', lastRefill)
        if ttl > 0 then redis.call('EXPIRE', key, ttl) end
        return { allowed, remaining, reset }
        """;
}
