/**
 * Shared rate limiting helpers for Patient Access API.
 */

export interface RateLimitDocument {
  id: string;
  memberId: string;
  count: number;
  ttl: number;
  expiresAt: string;
  _ttl?: number;
}

export interface RateLimitEvaluation {
  document: RateLimitDocument;
  limit: number;
  remaining: number;
  breached: boolean;
  reset: string;
}

export function evaluateRateLimit(
  key: string,
  memberId: string,
  existing: Partial<RateLimitDocument> | null,
  limit: number,
  ttlSeconds: number,
  now: Date = new Date()
): RateLimitEvaluation {
  const hasExistingCount = existing && typeof existing.count === 'number';
  const existingExpiry = existing?.expiresAt ? new Date(existing.expiresAt) : null;
  const withinWindow = Boolean(existingExpiry && !isNaN(existingExpiry.getTime()) && existingExpiry > now);

  const count = withinWindow && hasExistingCount ? (existing?.count ?? 0) + 1 : 1;
  const expiresAt = withinWindow && existingExpiry
    ? existingExpiry.toISOString()
    : new Date(now.getTime() + ttlSeconds * 1000).toISOString();
  const remaining = Math.max(limit - count, 0);

  return {
    document: {
      id: key,
      memberId,
      count,
      ttl: ttlSeconds,
      expiresAt,
      _ttl: ttlSeconds
    },
    limit,
    remaining,
    breached: count > limit,
    reset: expiresAt
  };
}
