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
  - Minimal-API endpoints (`MapGet`, `MapPost`, ... directly or in a
    `MapGroup`) get the same defaults through the fallback authorization
    policy: an endpoint with no authorization metadata of its own requires the
    default read permission for GET/HEAD and the default write permission
    otherwise, and is denied when that default is not set. `.AllowAnonymous()`,
    `.RequireAuthorization(new RequirePermissionAttribute("..."))` and
    `.RequireAuthorization(new RequireServiceClientAttribute("..."))` replace
    the default. A bare `.RequireAuthorization()` (any signed-in user) also
    replaces it, so don't use one; name the permission.
  - Registers `ICurrentActor`. The acting user is read from the token.
  - Every `IHttpClientFactory` client gets `ChoOutboundTokenHandler`. Calls to
    CHO hosts forward the caller's token. When there is no caller (a message
    consumer or a hosted service), the handler mints a service token for the
    tenant named in the outbound request's `X-Tenant-ID`. Calls to any other
    host get no `Authorization` and no `X-Tenant-ID` from the handler (logged
    at debug). See "Which hosts are CHO hosts" below.
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
   - Every outbound call to another CHO service names its tenant in
     `X-Tenant-ID`, including calls that today only run inside a request. A
     call from a consumer, hosted service or job without it goes out with no
     token and gets 401. Thread the tenant from the record or message into the
     client method. Test it with no inbound caller (see the
     `BackgroundCallsCarryServiceTokenTests` classes): the request must carry
     a service token whose `tenant_id` is that tenant.
   - Reading a tenant's `configuration.<x>Platform` from tenant-service goes
     through `CloudHealthOffice.Infrastructure.Tenancy.TenantPlatformLookup`.
     The rule is the same in every service: an answer (a platform, or none,
     meaning `cho`) is cached; 401/403 is logged as an error, never cached, and
     fails the operation; 404, 5xx, transport failures and unreadable bodies
     use `cho` for that call only and are not cached.
8. **Configuration.** Run
   `python3 scripts/security/add-dev-auth.py src/services/<svc>/appsettings.Development.json <svc-client-id>`.
   It adds development-only trust and a service token setting.
   For deployed environments, see `docs/security/portal-token-service.md`
   ("How services trust the issuer"). It covers trusting `cho-token-service`
   for user tokens and `cho-internal` for service tokens. Comments written by
   earlier runs of the script point at `docs/security/service-authentication.md`,
   which was never written. Read them as pointing at that document.
   Then check every CHO service this service calls against "Which hosts are
   CHO hosts" below. A callee whose base URL is not a `Services:*` URL or a
   `*.cloudhealthoffice` name (for example `ClaimsService:BaseUrl` set to
   `http://claims-service:8080`, or a code default like
   `http://tenant-service/`) goes in `ChoAuth:Outbound:Hosts` in
   `appsettings.json`. Development files that point CHO URLs at `localhost`
   add `ChoAuth:Outbound:DevelopmentHosts: ["localhost"]`.
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

## Which hosts are CHO hosts

`ChoOutboundTokenHandler` (and fhir-service's `SmartCallerOutboundHandler`)
attach a CHO token and `X-Tenant-ID` only to a host that `ChoOutboundHosts`
admits. Nothing is inferred from the shape of a host name: a dot-less name is
not internal. A host is a CHO host only when it is:

- listed in `ChoAuth:Outbound:Hosts` (exact host names, no scheme or port);
- under `.cloudhealthoffice`, `.cloudhealthoffice.svc` or
  `.cloudhealthoffice.svc.cluster.local` (always), or under a suffix in
  `ChoAuth:Outbound:Suffixes` (for another namespace);
- the host of a `Services:*` URL in configuration, unless the key is
  `TokenService`, `ArgoWorkflows`, `Prometheus` or listed in
  `ChoAuth:Outbound:ExcludedServices` (put a `Services:*` entry that is not a
  CHO backend there);
- in the Development and Testing environments only, listed in
  `ChoAuth:Outbound:DevelopmentHosts` (docker-compose names, `localhost`).

`localhost`, `*.localhost` and IP literals (including `[::1]`) are never CHO
hosts by suffix or because a `Services:*` URL points at them; only an explicit
`Hosts`/`DevelopmentHosts` entry admits one. A misconfigured or external base
URL therefore receives no credentials, and the callee's 401 shows the gap:
fix the configuration, never widen the rule.

```json
"ChoAuth": {
  "Outbound": {
    "Hosts": [ "claims-service", "trading-partner-service" ],
    "Suffixes": [ ".cho-staging.svc.cluster.local" ],
    "ExcludedServices": [ "Nppes" ],
    "DevelopmentHosts": [ "localhost" ]
  }
}
```

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

Two permissions exist for fhir-service only. The built-in roles are defined
once in `ChoRolePermissions.cs` and mirrored in tenant-service's
`StandardRoles` (`Models/TenantRole.cs`):

- `clinical:read` (USCDI clinical FHIR resources): UMCoordinator;
  TenantAdmin and PlatformAdmin through `*:*`; ComplianceOfficer through
  `*:read`. MemberServices does not get it, and `members:read` does not
  satisfy it.
- `payer-to-payer:initiate` (fhir-service `PayerToPayer/$initiate`):
  MemberServices and EnrollmentSpecialist, through whom members ask for a
  transfer; TenantAdmin and PlatformAdmin through `*:*`. `*:read` does not
  satisfy it.

Legal holds have their own permission, `records:legal-hold`, held by
ComplianceOfficer by name and by TenantAdmin and PlatformAdmin through `*:*`.
Placing a hold is a write, so `*:read` does not satisfy it (ComplianceViewer and
every read-only grant are refused), and neither does `members:write`
(EnrollmentSpecialist cannot place or release a hold). member-document-service
uses it today: see its row below. member-service uses it for member alerts:
creating or ending an alert of type `LitigationHold`
(`POST api/v1/members/{id}/alerts`, `POST .../alerts/{alertId}/end`) needs
`records:legal-hold`; every other alert type keeps `members:write` (the route
admits either permission and the action checks the alert's type; a refusal
is logged as `AUDIT member alert ... refused`).

Billing: Finance runs premium billing end to end with billing:read,
billing:run and finance:write; it holds no coverage or enrollment permission,
so the reads billing makes (sponsor list, coverage search) also admit
billing:read and sponsor suspension has a status-only endpoint that admits
finance:write (rows below). FinanceApprover holds billing:read (no
billing:run, no finance:write) so it can review the premium invoices and
billing runs behind the sponsor debits it releases.

Reference data and risk adjustment: code sets are not PHI, so Finance,
FinanceApprover and ComplianceViewer hold `reference-data:read` alongside the
claims, member services, enrollment, UM and provider-relations roles (every
built-in role now reads code sets; writes stay `settings:manage`). Finance
holds `risk-adjustment:read` and `risk-adjustment:write`: it calculates RAF
scores and runs the CMS submissions. risk-adjustment-service writes need
`risk-adjustment:write` only; `finance:write` (ledger changes) no longer
reaches risk scores, so a custom tenant role that relied on finance:write for
score writes must add risk-adjustment:write.

| Service | Default read | Default write | Stricter actions |
|---|---|---|---|
| accumulator-service | accumulators:read | accumulators:write | |
| appeals-service | appeals:read | appeals:write | |
| ar-service | finance:read | finance:write | |
| attachment-service | attachments:read | attachments:write | |
| authorization-service | authorizations:read | authorizations:write | approve/deny/status decisions: authorizations:decide |
| benefit-plan-service | benefits:read | settings:manage | /adjudicate and /calculate-benefits (they write accumulators): claims:work; read-only calculations (/estimate, /resolve-rates, NCCI checks and similar): claims:work,benefits:read |
| capitation-service | payments:read | payments:run | release/approve/void of payments: payments:approve (built-in FinanceApprover; Finance only prepares). Maker-checker: the user who created or executed the run cannot approve or release its statements (403) unless the tenant sets `configuration.paymentControls.enforceSeparationOfDuties: false` in tenant-service, which is logged per use. Full provider routing/account numbers for NACHA credits (`POST disbursements/nacha-file`, the NACHA part of `POST disbursements/batch`) come from provider-service's service-only full read (`GET /api/v1/internal/providers/npi/{npi}/bank-account`) with capitation-service's own service token (dedicated `ProviderBankAccounts` client, never the user's), fetched only after the releasing user passed payments:approve and separation of duties; no approved account or a refusal is an item needing attention (`needsAttention`; the disbursement stays Pending), never a silent skip. NACHA credit files go from capitation-service straight to the tenant's bank by SFTP (tenant-service `configuration.paymentControls.nachaTransmission`, credentials from Key Vault, pinned host key); responses carry a masked summary and a receipt, never the file. A file that cannot be sent is held encrypted for 7 days (`AwaitingRetrieval`): `GET disbursements/nacha/held` (payments:read or payments:approve), `POST disbursements/nacha/held/{ref}/retry` (payments:approve, user token, not the releaser), `POST disbursements/nacha/held/{ref}/retrieve` (platform:admin, user token, not the releaser, reason required, audited 4905) (see [bank-account-data.md](bank-account-data.md)) |
| claims-service | claims:read | claims:work | void (POST {id}/void, DELETE {id}, status → Voided): claims:void; adjustments: claims:adjust; work-queue override: claims:override-approve; work-queue assign: workqueue:assign; Cosmos partition migration: platform:admin |
| claims-examiner-service | claims:read | claims:work | |
| consent-service | consent:read | consent:write | |
| coverage-service | coverage:read | coverage:write | coverage search (`GET /api/v1/coverage`, what premium billing reads to price a sponsor's invoice with the Finance user's forwarded token): coverage:read or billing:read. Every other read and all writes keep the defaults |
| eligibility-service | eligibility:check | settings:manage | POST inquiries that only read (270-style) get eligibility:check |
| encounter-service | encounters:read | encounters:write | |
| encounter-submission-service | encounters:read | encounters:write | |
| enrollment-import-service | enrollment:read | enrollment:process | |
| ffs-service | payments:read | payments:run | |
| fhir-service | (none: every action states its callers with `[FhirAccess(smart, cho)]`; unannotated is denied) | (none) | SMART tokens are governed by scopes, patient binding and Provider Access, never by a CHO permission. CHO permissions per endpoint: Patient members:read; Coverage coverage:read; Claim/EOB claims:read; Encounter encounters:read; USCDI clinical resources (Condition, Observation, MedicationRequest, MedicationDispense, AllergyIntolerance, Procedure, Immunization, DiagnosticReport, CarePlan, CareTeam, Goal, Device): clinical:read; PayerToPayer/$initiate: payer-to-payer:initiate; Task rfai:read or appeals:read; Communication/DocumentReference/ClaimResponse appeals:read; Questionnaire and QuestionnaireResponse reads authorizations:read. Bulk export ($export, Group/$export, status) is SMART/system only, with no CHO permission and no staff access, until a `bulk-export` permission is approved. Payer-to-Payer $member-match and $member-data-export are SMART only |
| idcard-service | members:read | members:write | |
| member-document-service | members:read | members:write | legal holds (PUT member-documents/{id}/legal-hold, and an upload or pre-signed upload with `legalHold: true`): records:legal-hold, not members:write. A release needs a reason (400 without one). A held document cannot be deleted, modified or have its content replaced by anyone (there is no delete or metadata-update endpoint; finalize of an already finalized held document answers 409). Each placement and release is kept in the document's `legalHoldHistory` (action, actor from the token, reason, time) and written as an `AUDIT member document legal hold` log line. Every save replaces only the version it read (Mongo `Version` field, Cosmos ETag): a legal-hold change and another write started from the same read cannot overwrite each other; the later one gets 409 |
| payment-service | payments:read | payments:run | releasing money: executing a payment run (POST paymentruns/{id}/execute: check numbers, Posted payments, 835 envelopes, claims finalized as paid) and a reversal run (POST reversalruns/{id}/execute: negative payments, reversal 835s, claims voided): payments:approve, user tokens only, and maker-checker: the run's creator gets 403 "Separation of duties" (no tenant override; no creator recorded is allowed and logged). Create-and-execute (POST paymentruns/execute, reversalruns/execute) is always 403. Payment ledger changes (POST payments, {id}/post, {id}/reconcile): finance:write. 835 downloads (payments/{id}/835, era-envelopes/{id}/edi) mask BPR routing and account numbers to the last 4. After that check, the run's claims-service and trading-partner calls (and the finalize / void retries, POST paymentruns/{id}/finalize and reversalruns/{id}/void, payments:run) carry payment-service's own service token for the run's tenant, never the approver's token; the approver is recorded on the run, payments, 835s and void reason. A claim payment-service already paid (or reversed) is never selected again; a failed finalize leaves the payment PaidPendingFinalize. A run starts with one conditional Pending→Running write (a second executor gets 409); each claim is reserved (PaymentClaimReservations, insert-if-absent per tenant, kind and claim) before it is paid or reversed; a claim whose provider has no trading partner is not paid (NeedsTradingPartnerClaimIds). Stranded reservations: a hosted job (as payment-service, per tenant, no outbound calls) auto-releases a reservation only when its run is Failed/Cancelled, older than PaymentRuns:ReservationGracePeriod (30 min) and payment-service holds no payment and no 835 for the claim (conditional delete, audited in PaymentReservationAudit, listed in ReleasedReservationClaimIds); anything else (payment or 835 exists, run Running past PaymentRuns:ReservationStuckThreshold (2 h), unclassifiable) is flagged NeedsAttention (ReservationsNeedingAttention; GET claimreservations/needs-attention, payments:read). POST paymentruns|reversalruns/{id}/reservations/{claimId}/release: payments:approve, user tokens only, a reason, not the run's executor (403 "Separation of duties"); 409 when a Posted or PaidPendingFinalize payment exists (retry the finalize instead) |
| personal-representative-service | members:read | members:write | |
| premium-billing-service | billing:read | billing:run | ledger changes (record payment, void invoice, settle/return/cancel EFT draft) and process-delinquencies (suspends sponsors through sponsor-service's status endpoint, forwarding the caller's token; a failed suspension is recorded on the invoice, answered 502 and retried on the next run): finance:write. Releasing sponsor debits (POST eft/drafts, eft/drafts/batch, eft/nacha/generate): payments:approve, user tokens only, and maker-checker: the user who created or executed the invoice's billing run gets 403 "Separation of duties" (no tenant override). Sponsor bank details for a released debit come from sponsor-service's service-only full read (`GET /api/v1/internal/sponsors/{group}/bank-account`) with premium-billing-service's own service token, fetched only after the releasing user passed payments:approve and maker-checker; a sponsor not enrolled (`eftEnabled` false) is a normal skip, no approved account or a refusal is an item needing attention. NACHA debit files go from premium-billing-service straight to the tenant's bank by SFTP (no file download; `generate-and-download` was removed); responses carry a masked summary and a receipt. A file that cannot be sent is held encrypted for 7 days (`AwaitingRetrieval`): `GET eft/nacha/held` (billing:read, payments:read or payments:approve), `POST eft/nacha/held/{ref}/retry` (payments:approve, user token, not the releaser), `POST eft/nacha/held/{ref}/retrieve` (platform:admin, user token, not the releaser, reason required, audited 4905) (see [bank-account-data.md](bank-account-data.md)). Draft reads: billing:read or payments:read. Member premium summary: billing:read or members:read. Stripe webhook: anonymous, Stripe signature required, tenant from the signed PaymentIntent metadata |
| provider-contracts-service | contracts:read | contracts:write | |
| provider-service | providers:read | providers:write | credentialing decisions: providers:credential. Bank accounts are under dual control: a change (bank-account PUT/POST, or an account in a provider create/update body) is only proposed (providers:write) and stays pending until a different user approves it (`POST npi/{npi}/bank-account-changes/{id}/approve` or `/reject`: payments:approve, user tokens only; the proposer gets 403 "Separation of duties"; no tenant override). Accounts set before dual control are not active; they must be proposed and approved (the `Provider.BankAccount` row copy and records seeded from it with `legacy-provider-row` are never the active account: masked read 404, full read 404 `NoApprovedAccount`, GET provider shows none). The masked read capitation uses returns the approved account only; the pending change is readable masked with payments:approve or providers:read. Full numbers only from `GET /api/v1/internal/providers/npi/{npi}/bank-account`: `[RequireServiceClient("capitation-service")]`, active approved account only (404 `NoApprovedAccount` otherwise), never the bank tax id, audit event 4706; every user token (TenantAdmin and PlatformAdmin included) and every other service gets 403. Routing, account and bank tax numbers are encrypted at rest (`ProviderBankAccounts` and the `Provider.BankAccount` copy on provider documents and version rows; `FieldProtection:KeyRing` required outside Development/Testing, otherwise those writes answer 503); existing plaintext is re-encrypted by `provider-service --encrypt-bank-accounts`. See [bank-account-data.md](bank-account-data.md) |
| provider-verification-service | providers:read | providers:credential | |
| reference-data-service | reference-data:read | settings:manage | |
| rfai-service | rfai:read | rfai:write | legacy `GET api/rfai/by-auth/{tenantId}/{authNumber}` kept; the path tenant must equal the token tenant (403 otherwise). Responses omit attachment `storageProvider`/`storageKey`. Requested/submitted/closed-by come from the token |
| risk-adjustment-service | risk-adjustment:read | risk-adjustment:write | `finance:write` does not satisfy writes. Finance holds both permissions. capitation-service reads only the diagnosis-free summary (`GET members/{id}/scores/{year}/summary`) with its service token |
| sponsor-service | enrollment:read | enrollment:process | sponsor list, get and coverage-summary: enrollment:read or billing:read; member view: enrollment:read or members:read. Status only (`PUT /api/v1/sponsors/{group}/status`, body `{ status, reason }`, every other field ignored; premium billing suspends delinquent sponsors here): finance:write or enrollment:process. It allows Active → Suspended and Suspended → Active only (setting the current status again is a no-op 200; anything else 409; Terminated is terminal here), records reason, actor (token subject) and time on the sponsor with a status history entry, writes only those fields, and logs `AUDIT sponsor status ...`. The full `PUT /api/v1/sponsors/{group}` (any field, status included) and `DELETE` (terminate) stay enrollment:process. Sponsor bank accounts (what premium billing auto-debits) are under dual control: `POST {group}/bank-account-changes` proposes (billing:run or enrollment:process; 202, pending; the first account is pending too), `POST {group}/bank-account-changes/{id}/approve` or `/reject` decides (payments:approve, user tokens only; the proposer gets 403 "Separation of duties"; a stale approval is 409; no tenant override), `/cancel` withdraws (billing:run or enrollment:process). Reads (`GET {group}/bank-account`, `bank-account-changes`, `bank-account-changes/pending`) are masked to the last 4: billing:read, payments:read or enrollment:read (review reads also payments:approve). Full numbers only from `GET /api/v1/internal/sponsors/{group}/bank-account`: `[RequireServiceClient("premium-billing-service")]`, active approved account only; every user token (TenantAdmin included) and every other service gets 403. Routing and account numbers and `BillingInfo.billingAccountNumber` are encrypted at rest; sponsor responses show `billingAccountNumberLast4` only. See [bank-account-data.md](bank-account-data.md) |
| tenant-service | (none: every action is annotated) | (none) | `{tenantId}` routes must match the token tenant unless the caller holds platform:tenants (audited). Own tenant record, operating mode, usage, role catalogue: any authenticated caller (billing ids and API key records only with settings:manage). Users, invitations (create/list/revoke/resend) and unlinking a user's Entra identity: users:manage; an invitation can never grant PlatformAdmin or cho.service. Tenant settings/configuration, API keys, billing: settings:manage. `configuration.paymentControls.nachaTransmission` (the bank's SFTP drop for NACHA files) holds Key Vault secret names only (`nacha--{tenantId}--...`); a literal credential, another tenant's prefix, or `enabled` without a SHA-256 `hostKeyFingerprint` is 400; readers without settings:manage see `privateKeyConfigured`/`passwordConfigured` instead of the names. Operating-mode write: operating-mode:manage. Create/list/activate/suspend/delete tenants, status/tier changes, role catalogue writes: platform:tenants. `/internal/v1/identity/*` (including invitation redemption): `[RequireServiceClient("token-service")]`. `POST /internal/v1/tenants/{tenantId}/onboarding-complete` (pending → active only, own tenant, audited): `[RequireServiceClient("wf-tenant-onboarding")]`, the tenant-onboarding Argo workflow's workload token (docs/security/argo-service-tokens.md). Stripe webhook: anonymous, Stripe signature required |
| trading-partner-service | trading-partners:read | settings:manage | |
| CHO.TerminologyService | terminology:read | settings:manage | |
