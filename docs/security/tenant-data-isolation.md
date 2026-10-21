# Tenant data isolation in MongoDB

Every health plan (tenant) shares the same MongoDB collections; their records
are separated by a `TenantId` field. (The optional database-per-tenant mode,
`MongoDb:UseTenantScoping`, adds a second wall but is not the default.) This
page describes how a query is kept inside one tenant, and how that is
enforced rather than left to convention.

## How the tenant reaches a query

1. **From the token, never the client.** `TenantMiddleware` reads the tenant
   from the authenticated token: no claim is 401, a disagreeing
   `X-Tenant-ID` header is 403. There is no default tenant.
2. **Into the repository.** Repositories take the tenant from the request
   context (or the adjudication context for pipeline work) and put
   `TenantId == tenant` in every filter, alongside the id or criteria the
   caller asked for. A lookup by id alone is the classic cross-tenant bug.
3. **Background jobs span tenants on purpose,** and then work under each
   record's own tenant: an outbox dispatcher finds due entries across
   tenants and processes each in a scope for `record.TenantId`.

## Enforcement: the tenant-filter architecture test

`tests/CloudHealthOffice.Architecture.Tests/TenantFilterArchitectureTests.cs`
parses every C# file under `src/` and finds each MongoDB read, count, update,
replace, delete, aggregate or watch on a collection whose document type has a
`TenantId` (461 document types, 785 queries as of October 2026). Each one must
either

- be scoped: its method refers to the tenant (a `TenantId` filter, the
  request's tenant id, a tenant-scoped filter helper), directly or through
  another method of the same class that does; a private helper that runs a
  caller-built filter is as scoped as its callers; or
- be marked `[CrossTenantQuery("why")]`
  (`CloudHealthOffice.Infrastructure.Data`), with the reason it spans tenants
  and how the result stays tenant-safe.

The test runs on every PR (the required **Tests Passed** check), so a new
unscoped query fails the build until it is scoped or the cross-tenant
decision is written down and reviewed.

It is a source check, so it is a floor, not a proof: a method that mentions
the tenant without filtering on it passes. Code review, the synthetic
tenant-isolation security labs and the per-service tenant tests cover the
rest.

## Marked cross-tenant queries (October 2026)

31 queries carry `[CrossTenantQuery]`. They fall into four groups:

| Group | Examples | Why it is tenant-safe |
|---|---|---|
| Background jobs and outboxes | claims accumulator/event outboxes, coverage status sweep, plan-year scheduler, encounter deadline sweep, ID card reconciliation, unmatched-275 matcher, inbound-attachment and remittance outbox publishers | No request reaches them; each record is processed under its own `TenantId`. |
| Keys that embed the tenant | NCCI load ledger (`{tenant}:{quarter}:…`), batch eligibility jobs (`{tenant}::{id}`), inbound attachment idempotency keys | The key cannot name another tenant's record. |
| Server-generated ids, checked after the read | remittance receipts (poster compares `TenantId`), status inquiries, attachment transmissions, invitations | Ids are GUIDs the server created; request paths compare the tenant before use. |
| Tenant resolution and clearinghouse ingress | SMART identity links (issuer + subject), 277CA/835 matching by gateway identifiers | These lookups *decide* the tenant; a mixed-tenant match fails. |

## Fixed while adding the check

- **Development 835 injection** (`POST api/dev/gateway/remittance`,
  Development only): an injected 835 could match another tenant's claim
  transmission by id or control number, and a chosen remittance/event id
  could replay another tenant's receipt. Injected 835s now match only the
  caller's tenant (`GatewayRemittance.RestrictToTenantId`), their ids are
  namespaced by tenant, and an unmatched claim no longer lists the receipt
  under a transmission it did not match. The 277CA injection namespaces
  caller-chosen acknowledgment ids the same way, and the dev history
  endpoint answers 404, not 400, for another tenant's transmission.
- **Terminology:** a global map reload deactivated previous versions by map
  name only, which also reached a tenant's override of the same map; it now
  touches global versions only.
- **Encounter submissions:** the deadline-warning update also filters on the
  submission's tenant.
