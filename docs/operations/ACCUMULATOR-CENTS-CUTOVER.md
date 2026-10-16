# Redis accumulators: cut-over to integer cents

**Applies to:** benefit-plan-service, the only process that reads or writes the
`accum:*` hashes (`RedisAccumulatorService`).
**Change:** hash values move from `HINCRBYFLOAT` dollars (`"750.00000000000011"`)
to integer hundredths (`"75000"`). Every hash written by the new code carries
`__v` = `2`.

## How existing data is handled

No migration script is run, and nothing is converted. Each hash is upgraded
lazily, the first time the new code touches it:

| First touch | What happens |
|---|---|
| Read (`GetAccumulatorsAsync`) | The hash has no `__v`, so it is a cache miss. It is rebuilt from claims-service, the system of record, with the existing rebuild path. The old amounts and journals are dropped, its fences (`__fence:*`) are kept, and `__v=2` is set. |
| Commit (`CommitAsync`) | Old amounts and journals are dropped and fences kept. The hash is now cold, so the commit adds nothing and journals the claim. The next read rebuilds the hash, and claims-service counts the claim, which is Approved before it commits. |
| Increment (`ApplyUpdatesAsync`, Production execution mode) | Old amounts and journals are dropped and nothing is added. A warning is logged. The next read rebuilds the hash. |
| Reversal / invalidation | The same as before: amounts are dropped and fences kept. |

Old float values are never read as cents. A claim's journal in an old hash is
not returned as that claim's contribution (`GetClaimUpdatesAsync`).

**Known gap, Production execution mode only.** An increment that lands on an
old hash is not added. If claims-service has not yet recorded that claim as
Approved when the next read rebuilds the hash, the rebuild misses the claim's
amounts until the hash is next invalidated or expires. The prepared-commit
path, which the claims pipeline uses, is not affected.

## Load

After the deploy, the first read of every member and family hash for a plan
year calls `GET /api/claims/accumulator-totals` on claims-service once per
hash. Deploy off-peak and watch claims-service latency and benefit-plan-service
`Accumulator rebuild` logs.

## Deploy: old and new code must not run at the same time

The two versions read each other's values wrongly:

- An old pod reads `75000` as $75,000, and the deductible reads as met.
- An old pod adds float dollars to a cents field, for example `75000` + `10.5`.

The default `RollingUpdate` strategy overlaps the two versions, so it **must
not** be used for this release:

1. Scale benefit-plan-service to 0:
   `kubectl scale deployment/benefit-plan-service --replicas=0`.
   Claims-service's accumulator outbox keeps the commits that fail while it is
   down and retries them.
2. Wait until no benefit-plan-service pod remains.
3. Roll out the new image, then scale back to the usual replica count.
4. Check the outbox drains. claims-service logs `Accumulator commit` outcomes,
   and `GET api/claims/admin/accumulators` shows pending items.

**Rollback.** The old code cannot read `__v=2` hashes. A rollback needs the
same scale-to-zero, plus deleting the `accum:*` hashes before the old pods
start. That deletes the fences of voided and denied claims, so a commit of one
of those claims that is still in flight would no longer be refused. Treat
rollback as a deliberate operation and review it before running it.

## Optional check after the deploy (read-only)

```
redis-cli --scan --pattern 'accum:*' | while read k; do
  [ "$(redis-cli HGET "$k" __v)" = "2" ] && echo v2 || echo legacy
done | sort | uniq -c
```

Legacy hashes go away as they are touched or as they expire; the TTL is 425
days. Nothing has to delete them.

**Do not run anything against a production Redis without approval.**
