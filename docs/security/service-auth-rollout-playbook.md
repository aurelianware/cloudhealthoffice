# Service authentication rollout playbook

This is how each CHO backend service moves to the shared authentication in
`src/services/shared/CloudHealthOffice.Infrastructure/Security`. It covers review
items P6, P7 and §8.10 in `docs/reviews/claims-director-portal-review.md`.
`member-service` (commit "feat(member-service): require CHO tokens") is the
worked example.

## What the shared layer does

- `builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment, auth => { auth.DefaultReadPermission = "..."; auth.DefaultWritePermission = "..."; });`
  - Validates JWTs from the issuers in the `ChoAuth` section: signature, issuer,
    audience `cho-api` and lifetime. Startup fails if no issuers are configured.
    Symmetric keys are allowed only in the Development or Testing environment.
  - Default deny. Any controller action without `[RequirePermission]` or
    `[AllowAnonymous]` gets the default read permission (GET/HEAD) or the
    default write permission (all other methods). With no default set, the
    action is denied.
  - Registers `ICurrentActor`. The acting user is read from the token.
  - Every `IHttpClientFactory` client gets `ChoOutboundTokenHandler`. Calls to
    CHO hosts forward the caller's token. When there is no caller (a message
    consumer or a hosted service), the handler mints a service token for the
    tenant named in the outbound request's `X-Tenant-ID`. Tokens are never sent
    to external hosts.
- `app.UseChoAuthentication();` runs authentication, then
  `TenantMiddleware` (tenant from the token claim only; a header that disagrees
  is rejected with 403; a token without a tenant is rejected with 401), then
  authorization. `/health*`, `/ready`, `/live` and `/metrics` need no token.

## Per-service steps

1. **Baseline.** Run the service's test projects before changing anything and
   record the pass/fail counts. If a test already fails, keep a note of it.
2. **Program.cs**
   - Add `using CloudHealthOffice.Infrastructure.Security;` and call
     `AddChoAuthentication` with the defaults from the table below.
   - Remove any existing `AddAuthentication`, `AddMicrosoftIdentityWebApi`,
     `AddJwtBearer` or `AddAuthorization` used for CHO callers, along with the
     Entra (`AzureAd`) configuration they read. Any external-caller scheme
     (provider JWTs, API keys) stays, and you report it.
   - Replace the local tenant middleware registration and any
     `UseAuthentication()`/`UseAuthorization()` with one
     `app.UseChoAuthentication();`. It goes after `UseCors` and before
     `MapControllers`.
   - Remove any `new TenantMiddlewareOptions { RequireTenantId/DefaultTenantId }`.
3. **Local `Middleware/TenantMiddleware.cs`.** Delete the middleware class.
   Keep only the public extension helpers that other code still uses (for
   example `GetTenantId`), reading `context.Items["TenantId"]`. Delete the local
   unit tests that targeted the deleted class (the shared tests cover it now),
   and list them in your report.
4. **Tenant sources.** Inbound request code must not read the tenant from
   `X-Tenant-ID`, `[FromHeader]`, the query string or the request body. Use
   `HttpContext.GetTenantId()` or `ICurrentActor.TenantId`. Remove every
   `?? "default-tenant"` fallback. A missing tenant is an error, never a
   default.
   - Outbound clients that set `X-Tenant-ID` on requests to other CHO services
     can keep doing so. The handler uses that header to mint the service token
     when there is no caller.
5. **Actor identity.** For every write, take the actor from
   `ICurrentActor.UserId` (or `User.FindFirst("sub")`). Remove any reading of
   `X-User-Id` headers or body-supplied actor fields (`CreatedBy`, `UpdatedBy`,
   `PerformedBy`, `ReviewedBy`, `ApprovedBy`, `SubmittedBy` and similar on
   request DTOs). Drop those fields from request DTOs, or ignore them and leave
   a one-line comment. Persisted models keep their fields, filled from the token.
6. **Permissions.** Put the defaults below in `AddChoAuthentication`. Annotate
   actions that need something stricter with `[RequirePermission("...")]`. A
   comma-separated list means "any of". Replace bare `[Authorize]` attributes
   with the right permission. Mark public metadata endpoints `[AllowAnonymous]`
   only when they return no tenant data, and report each one you add.
7. **HTTP clients.** Change `new HttpClient()` used for CHO-to-CHO calls to
   `IHttpClientFactory` clients, so the token handler applies.
8. **Configuration.** Run
   `python3 scripts/security/add-dev-auth.py src/services/<svc>/appsettings.Development.json <svc-client-id>`.
   It adds development-only trust and a service token setting.
   For deployed environments, see `docs/security/portal-token-service.md`
   ("How services trust the issuer"). It covers trusting `cho-token-service`
   for user tokens and `cho-internal` for service tokens. Comments written by
   earlier runs of the script point at `docs/security/service-authentication.md`,
   which was never written. Read them as pointing at that document.
9. **Tests.**
   - Integration tests that use `WebApplicationFactory` must run in the
     `Development` or `Testing` environment. Create clients with
     `factory.CreateDefaultClient(new ChoDevelopmentTokenHandler())`. Pass roles
     to the handler when a test needs a narrower role. The handler turns the
     test's `X-Tenant-ID` into a signed development token.
   - Add a test for every defect you fix (actor from token, removed
     default-tenant, permission on a sensitive action). It must fail before the
     fix and pass after.
   - Never skip, disable or weaken an existing assertion that still describes
     correct behaviour.
10. **Commit** one service per commit:
    `feat(<svc>): require CHO tokens; tenant from token; <anything notable>`.

## Default permissions

Wildcard grants (`*:*`, `*:read`) never satisfy a `platform:*` permission.
Platform permissions act across tenants and are granted only by name, so a
cross-tenant action needs `[RequirePermission("platform:admin")]` (or another
`platform:` permission), never a tenant permission.

Service tokens satisfy every tenant permission but never a `platform:*`
permission. An endpoint that one specific service must call (for example
tenant-service's identity lookups for token-service) names that service with
`[RequireServiceClient("<client-id>")]`, which admits only a service token whose
`sub` and `azp` both equal the client id.

| Service | Default read | Default write | Stricter actions |
|---|---|---|---|
| accumulator-service | accumulators:read | accumulators:write | |
| appeals-service | appeals:read | appeals:write | |
| ar-service | finance:read | finance:write | |
| attachment-service | attachments:read | attachments:write | |
| authorization-service | authorizations:read | authorizations:write | approve/deny/status decisions: authorizations:decide |
| benefit-plan-service | benefits:read | settings:manage | /adjudicate and /calculate-benefits (they write accumulators): claims:work; read-only calculations (/estimate, /resolve-rates, NCCI checks and similar): claims:work,benefits:read |
| capitation-service | payments:read | payments:run | release/approve/void of payments: payments:approve (built-in FinanceApprover; Finance only prepares). Maker-checker: the user who created or executed the run cannot approve or release its statements (403) unless the tenant sets `configuration.paymentControls.enforceSeparationOfDuties: false` in tenant-service, which is logged per use |
| claims-service | claims:read | claims:work | void (POST {id}/void, DELETE {id}, status → Voided): claims:void; adjustments: claims:adjust; work-queue override: claims:override-approve; work-queue assign: workqueue:assign; Cosmos partition migration: platform:admin |
| claims-examiner-service | claims:read | claims:work | |
| consent-service | consent:read | consent:write | |
| coverage-service | coverage:read | coverage:write | |
| eligibility-service | eligibility:check | settings:manage | POST inquiries that only read (270-style) get eligibility:check |
| encounter-service | encounters:read | encounters:write | |
| encounter-submission-service | encounters:read | encounters:write | |
| enrollment-import-service | enrollment:read | enrollment:process | |
| ffs-service | payments:read | payments:run | |
| idcard-service | members:read | members:write | |
| member-document-service | members:read | members:write | |
| personal-representative-service | members:read | members:write | |
| premium-billing-service | billing:read | billing:run | |
| provider-contracts-service | contracts:read | contracts:write | |
| provider-service | providers:read | providers:write | credentialing decisions: providers:credential. Bank accounts are under dual control: a change (bank-account PUT/POST, or an account in a provider create/update body) is only proposed (providers:write) and stays pending until a different user approves it (`POST npi/{npi}/bank-account-changes/{id}/approve` or `/reject`: payments:approve, user tokens only; the proposer gets 403 "Separation of duties"; no tenant override). The masked read capitation uses returns the approved account only; the pending change is readable masked with payments:approve or providers:read |
| provider-verification-service | providers:read | providers:credential | |
| reference-data-service | reference-data:read | settings:manage | |
| rfai-service | rfai:read | rfai:write | |
| risk-adjustment-service | risk-adjustment:read | risk-adjustment:write | |
| sponsor-service | enrollment:read | enrollment:process | |
| tenant-service | (none: every action is annotated) | (none) | `{tenantId}` routes must match the token tenant unless the caller holds platform:tenants (audited). Own tenant record, operating mode, usage, role catalogue: any authenticated caller (billing ids and API key records only with settings:manage). Users, invitations (create/list/revoke/resend) and unlinking a user's Entra identity: users:manage; an invitation can never grant PlatformAdmin or cho.service. Tenant settings/configuration, API keys, billing: settings:manage. Operating-mode write: operating-mode:manage. Create/list/activate/suspend/delete tenants, status/tier changes, role catalogue writes: platform:tenants. `/internal/v1/identity/*` (including invitation redemption): `[RequireServiceClient("token-service")]`. Stripe webhook: anonymous, Stripe signature required |
| trading-partner-service | trading-partners:read | settings:manage | |
| CHO.TerminologyService | terminology:read | settings:manage | |
