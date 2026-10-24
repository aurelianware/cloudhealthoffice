# Plan configuration: settings that cascade, with overrides

**Status:** Agreed in review (2026-10-22); decisions in §13. Steps 1 and 2 of the build order are built (§§12.1, 12.2), all of step 2b (§§12.3–12.6), steps 3a to 3c of step 3 (§§12.7–12.9) and all of step 3d (§§12.10–12.16), plus the `SBR09` defect that 3d's fourth consumer turned up (§12.15).
**Scope:** One way for every CHO service to define a business setting, set it at the right level of the plan's structure, override it further down, and explain where an effective value came from.

**Related:**
- [enrollment-operations.md](enrollment-operations.md): the first settings to use this (retro limits, waiting periods, orphan grace, full-replace guard).
- [appeals-case-model.md](appeals-case-model.md): state rule packs, which are regulatory data rather than settings (§8).
- [edi-operations.md](edi-operations.md): partner settings, which stay in trading-partner-service (§8).

---

## 1. The problem

Enrollment alone already needs these settings, each resolved per member:
- retro add and term limits;
- waiting periods;
- the orphan grace period;
- the full-replace guard.

Premium billing needs grace periods. Claims, appeals and correspondence will need their own. Each one has a sensible default per line of business, and specific plans, sponsors or contracts override it.

**What exists today:**
- **Line of business is only an enum.** It sits on the sponsor and the benefit plan; nothing above it can carry configuration.
- **There is no sponsor policy or class.** The sponsor is a single record.
- **Settings live wherever they were first needed.** The premium grace period is on the sponsor's billing info. #1500 adds a small settings store inside enrollment-import-service.

Without one model, every service grows its own store, its own hierarchy logic and its own admin screen. Nobody can answer "why is this member's retro limit 90 days?" from one place.

## 2. How QNXT and Facets do it

**QNXT:**
- A **carrier** has one or more **programs**; a program is a line of business.
- Carrier settings are overridden at the program level.
- A **sponsor** has **sponsor policies**, and each policy turns settings on or off and sets their values.

**Facets:**
- The enrollment structure is **group → subgroup → class → plan**.
- Eligibility rules, waiting periods and billing hang off the subgroup and class.

**What both show:**
1. **Line of business is a thing, not a label.** One carrier runs several Medicaid programs (in Texas: STAR, STAR Kids, CHIP), and each has its own rules.
2. **The sponsor side and the product side are different branches.** A sponsor can buy plans from more than one program. A plan is sold to many sponsors.
3. **The contract is the most specific level.** That is the sponsor's policy for one plan, and a class within it.

## 3. The levels

Two branches meet at the sponsor policy:

```
Platform default (in code)
  └─ Tenant (the health plan organization)
       └─ Carrier (legal entity; optional, one by default)
            └─ Program (a line of business in a state: "TX STAR", "AZ Commercial Large Group")
                 └─ Benefit plan
                                                       Sponsor
                                                         └─ Sponsor policy  (sponsor × benefit plan × dates)
                                                              └─ Class      (e.g. full-time hourly)
```

**Resolution order** for a member covered under a class of a sponsor policy, most specific first: class → sponsor policy → sponsor → benefit plan → program → carrier → tenant → platform default. The first level that sets a value wins.

**Why sponsor sits above benefit plan in precedence:** a sponsor's negotiated term (for example, a longer retro window for the whole group) applies across every plan it buys, and it is more specific to the member than the plan's default. Settings that a sponsor must not change are protected by locks and floors (§5), not by their position in the order.

**Both sponsor and sponsor policy?** Yes, because they hold different kinds of settings:

| Level | Holds | Examples |
|---|---|---|
| **Sponsor** | How the sponsor operates, across all its policies | File cadence, orphan grace period, full-replace guard, correction channel and contacts |
| **Sponsor policy** | The contract for one plan | Retro add and term limits, termination timing (end of month or date of event), premium grace period, dependent age rules |
| **Class** | Eligibility within a contract | Waiting period, eligibility rule (hours per week, pay type) |

Each setting declares which levels it may be set at (§4). This keeps the choice of level deliberate. A full-replace guard on one policy would be meaningless, because the sponsor's file covers all its policies. So that setting simply can't be set at policy level.

**Carrier** is optional and defaults to one per tenant. It matters when an organization runs several legal entities, for example an HMO and an insurance company, with different filings.

## 4. Setting definitions

A setting is defined in code by the service that owns it, and registered in a shared catalog:

| Field | Purpose |
|---|---|
| Key | `enrollment.retro.addDays`, `enrollment.orphan.graceDays`, `premium.gracePeriodDays` |
| Type | Integer, decimal, boolean, enum, duration, date rule, or a small structured value |
| Default | The platform default, optionally per line-of-business type (Commercial 7, Medicaid 0) |
| Levels | Where it may be set (for example program, sponsor, sponsor policy) |
| Constraints | Bounds and rules checked on every write and every resolution (§5) |
| Owner and description | The owning service, and plain-language help shown in the admin screen |
| Change control | `None`, `ReasonRequired` or `TwoPersonApproval` |
| Effective-dated | Whether the value follows the transaction's date (most do) |

Definitions are versioned with the code. A value can only be stored for a registered key at an allowed level, and must pass the type and constraint checks.

## 5. Locks and floors

Overrides need two kinds of limits:
- **Floors and ceilings come from law or contract.** They are written as constraints on the definition, scoped by line-of-business type or program. Example: the commercial ACA rescission rule, under which a retro term may go back only to the premium paid-through date. A value that breaks a floor is refused when it is written. Resolution clamps to the floor as a second guard and reports that it did.
- **Locks come from the plan.** An administrator can lock a value at a level, and nothing below can override it. Example: a Medicaid program locks retro limits to "the state's dates", so no sponsor or policy can change them. A lock shows in the admin screen with who set it and why.

## 6. Effective dating

Values carry `effectiveFrom` and an optional `effectiveTo`. A change for next plan year is entered ahead of time and takes effect on its date.

Resolution is **as of a date**: the date of the transaction being processed, not today. A retro change on 2026-03-01 is judged by the settings in force on 2026-03-01.

Past periods are never edited in place. A correction adds a new version with a reason, and history keeps every version.

## 7. Resolving and explaining

```
resolve(key, scope, asOf) → { value, sourceLevel, sourceId, locked, clampedBy?, trace[] }
```

- **Scope** is the member's position: tenant, and where known carrier, program, plan, sponsor, policy and class. The caller passes the ids it has. A shared client fills in what can be derived, for example a policy's plan and a plan's program.
- **Trace** lists every level checked and what it held. The answer to "why is this 90 days?" is one call, and the admin screen shows it as an "inherited from Sponsor Acme" badge.
- **"Where is this overridden?"** lists every override of a key below a node. Auditors and implementation teams ask this constantly.

## 8. What is not a setting

- **Regulatory rule packs**, such as the state appeals clocks, stay as versioned, cited data packs ([appeals-case-model.md](appeals-case-model.md)). They describe the law, not the plan's choices. Where a plan may tighten a rule-pack value, that tightening is a setting whose floor is the pack.
- **Partner technical settings** (sender IDs, file schedules) stay in trading-partner-service.
- **Benefit design** (copays, accumulators) stays in the benefit model.
- **Infrastructure configuration** (URLs, secrets) stays in app settings and Key Vault.

## 9. Where it lives

**A configuration module in tenant-service** (decided, §13) owns the store, the catalog, the admin API and the audit history. It has its own collections, its own `api/v1/configuration/...` routes, its own permissions and its own change events, so it can move to a service of its own later without callers changing:
- **Services resolve through a shared client with a local cache.** Entries are keyed by tenant, key and scope.
- **Cache invalidation:** a `configuration.changed.v1` event invalidates entries, with a short expiry as the fallback for deployments without Kafka. The in-memory bus is process-local, the same constraint work-management handles with reconcile.
- **Definitions** live in a shared package, and each owning service registers its own at startup.
- **Hierarchy records stay where they belong.** Carrier and program go in tenant-service; benefit plan stays in benefit-plan-service; sponsor policy and class go in sponsor-service. The configuration service stores ids, not copies.

The alternative, a store inside each service, is what #1500 does today (its settings store and `enrollment:configure` are interim and are replaced in step 1). It is fine for one setting. It won't scale to a single admin screen, a single audit history or a single "where is this overridden" answer.

## 9a. Who changes what

Settings changes are driven by permissions, assigned by job function:

| Permission | Allows |
|---|---|
| `config:read` | See effective settings and explain them for a member |
| `config:sponsor` | Change sponsor, sponsor-contract, policy and class settings, within the product's negotiable ranges |
| `config:product` | Change program and plan settings, including which sponsor settings are negotiable and their ranges |
| `config:enterprise` | Change tenant and carrier settings; set and remove locks; promote change sets to production |
| `config:approve` | Approve a two-person or out-of-range change, never one's own |

| Role | Holds | Works |
|---|---|---|
| **AccountManager** | `config:read`, `config:sponsor` | Enters a group's terms as a draft on the sponsor contract while working the new business or renewal |
| **Underwriter** | `config:read`, `config:approve` | Approves terms outside the negotiable ranges |
| **ConfigurationAnalyst** | `config:read`, `config:product`, `config:sponsor` | Builds programs and plans and sets the negotiable ranges |
| **ConfigurationManager** | all `config:*` | Locks, tenant and carrier settings, approvals, promotion |
| Operations (enrollment, claims, member services) | `config:read` | View and explain; questions about a sponsor's terms go to its account manager |

**Negotiable ranges.** For each sponsor-side setting the product team marks it fixed or negotiable on the program or plan, and gives a range (for example retro add 30–90 days, waiting period 0–90 days). An account manager's value inside the range activates with the contract; outside it, the change waits for an underwriter. A fixed setting is read-only to account managers. Locks and legal floors apply regardless.

**Drafts.** Sponsor-side changes are drafted against a sponsor contract term (§9b) and take effect when the term is activated, on its start date, as one audited change set naming the account manager and the contract.

## 9b. Sponsor contracts

The group agreement between the sponsor and the plan, per contract term (usually a plan year), is where sponsor-side configuration lives. It is a record in sponsor-service with the signed document in the document store:

| Section | Holds | Read by |
|---|---|---|
| Term | Contract period, renewal date, status (draft, in underwriting, signed, active, ended), the signed document | — |
| Plans offered | One **sponsor policy** per benefit plan | Enrollment, ID cards, claims eligibility |
| Tiers and rates | Tier structure (2-, 3- or 4-tier) and the rate for each tier | Premium billing, AR |
| Contribution | Employer and employee shares (optional; needed when CHO bills members directly: COBRA, retirees, direct bill) | Premium billing |
| Billing terms | List bill or self-bill, frequency, due day, grace period | Premium billing, AR (replaces the sponsor's flat billing info) |
| Eligibility by **class** | Hours, employee types, dependent rules (age limit, domestic partners) | Enrollment |
| Waiting period and effective-date rule | E.g. first of the month after 60 days | Enrollment |
| Termination and retro rules | End of month or date of event; retro windows | Enrollment |
| Life events | Allowed qualifying events, reporting windows, coverage start | Enrollment, member services |
| Documentation requirements | Proof required per event or dependent | Enrollment exceptions, correspondence |
| Underwriting conditions | Minimum participation and employer contribution | Renewal review |

**Numbers and what lasts across renewals:**
- The **group number** stays on the sponsor (`Sponsor.GroupNumber`): it identifies the account and does not change at renewal.
- Each **sponsor policy** has its own **policy (sub-group) number** under the group, e.g. 123456-0001 for the PPO and 123456-0002 for the HMO. This is the number often printed on ID cards and sent as the group number on the 834 (`REF*1L`) and the 837 (`SBR03`).
- A sponsor policy is a lasting record that continues across renewals, with its terms **versioned per contract term**. Renewal does not create new policies, so coverage, ID cards and claims never need re-pointing.
- The contract term holds the dates, status and signed document, and covers the policy terms for that year.
- **When a policy continues and when it is replaced.**
  - **Same plan, new terms** (rates, waiting period, retro window, life events): the policy continues and gains a new version of its terms, effective on the renewal date. Anything resolved for a date uses the version in force then.
  - **Different plan** (a new benefit plan, a different program or carrier, or a required change of policy number): the old policy ends the day before renewal, a new policy starts on the renewal date, and members move through the renewal's enrollment.
  - **Plan-year benefit changes** (deductibles, copays) are versions of the benefit plan itself, where accumulators reset; the sponsor policy points at the plan version in force.
  - Ending and recreating policies at every renewal is avoided: it forces every member to be termed and re-added, creates a coverage-break risk, pends claims around the boundary, reissues ID cards and splits history.
- The 834 import resolves `REF*1L` against policy numbers as well as group numbers (policy → sponsor). Today it, and the orphan retry, assume `REF*1L` is the group number.

```
Sponsor (group number)
  └─ Sponsor policy (policy / sub-group number; lasts across renewals)
       ├─ Classes
       └─ Terms per contract year (rates, eligibility, settings)
Contract term (dates, status, signed document) ── covers ──> that year's policy terms
```

**Legal floors on these** (to be verified against the regulation text when built): group waiting periods of at most 90 days; dependent coverage to age 26; special enrollment windows of at least 30 days for group plans, and 60 days after loss of Medicaid or CHIP.

**Flow:** a new group or renewal opens a draft term → the account manager enters plans, tiers, rates, eligibility and settings → anything out of range goes to underwriting → on signature the document is attached and the term activates on its start date → enrollment and billing resolve from it as of each member's dates. A detailed design follows at enrollment step 4.

## 10. Moving between environments

Implementation teams build configuration in a test environment and promote it to production. Settings export and import as a versioned JSON bundle, per tenant or per subtree (for example one sponsor and its policies). Ids map by business key: program code, plan code, group number and policy number.

An import is a preview first: what will change, and what each change overrides. It then applies as one audited change set. This addresses a long-standing pain with core-admin configuration migration.

## 11. Admin screen

**Admin › Plan configuration** has two trees:
- **Products:** carrier → program → plan.
- **Sponsors:** sponsor → policy → class.

**Each node shows every setting allowed at that level:**
- the effective value as of a chosen date;
- a source badge saying where the value comes from;
- actions to override, clear, lock or schedule a future value;
- the setting's history.

**Further views:**
- **"Explain for a member"** takes a member and date and shows the full trace for every setting.
- **"Where overridden"** lists every override of one key.

Changes needing a reason or a second approver use the same pattern as two-person retro approval.

## 12. Build order

1. **Catalog, store, resolver** (in tenant-service). *Built 2026-10-22 (§12.1); the tenant controls moved to step 2b.*
   - Definitions, values with effective dating, locks and constraints.
   - The resolve and explain API, the cache client and change events.
   - Levels: tenant, line-of-business type and sponsor.
   - Move #1500's enrollment settings into it; retire its interim store, endpoints and `enrollment:configure`.
   - The `config:*` permissions and the roles in §9a.
2. **Program and carrier records** in tenant-service. *Built 2026-10-23 (§12.2).*
   - Carrier and program records, one carrier per tenant created automatically.
   - Benefit plans and sponsors gain a program.
   - Carrier, program and benefit-plan levels in the resolver; naming a program resolves the levels above it.
   - `CrdContextResolver` reads the member's benefit plan → program.
   - **Still to do, as step 2b.** Each one is a consumer change rather than a new record, so they move one at a time behind the fields step 2 added:
     - Legal-entity details move onto the carrier: NACHA origination (premium-billing, capitation, payment) and the 835 payer, *built 2026-10-23 (§12.3)*; the name and address used on letters and ID cards (correspondence, idcard), *built 2026-10-23 (§12.4)*.
     - Tenant controls become catalog settings at tenant level: `payment.enforceSeparationOfDuties` (capitation-service), `personalRepresentative.requireSecondUserActivation` (personal-representative-service), *built 2026-10-23 (§12.5)*. Technical and integration configuration (modules, region, SFTP, clearinghouse, platform adapters) stays tenant configuration.
     - The `overrides` endpoint narrows by hierarchy: a carrier's programs, a program's plans, *built 2026-10-23 (§12.6)*; a sponsor's policies **moves to step 3**, which is what builds the sponsor contract the link would run through.
     - The engine's `ResolvedRuleSetKey` becomes a program id rather than a string concatenation of state, line of business and program code, *built 2026-10-23 (§12.6)*.
3. **Sponsor contracts** (§9b) in sponsor-service, with sponsor policies and classes, draft terms and underwriting approval (enrollment step 4), and their levels in the resolver. Staged, because the records come before anything that reads them:
   - **3a, the records:** sponsor policies with their classes, and contract terms with §9b's status flow. *Built 2026-10-23 (§12.7).*
   - **3b, terms versioned per contract year:** the tiers, rates, contribution, billing terms, eligibility, waiting period and life events of §9b's table, held per period so that a renewal adds a version rather than replacing a policy. *Built 2026-10-23 (§12.8), the policy's own sections and the contract term's.*
   - **3c, the levels:** `SponsorPolicy` and `Class` in `SettingScope` and in resolution, and the sponsor → policies narrowing of `GET overrides` that step 2b deferred here (§12.6). *Built 2026-10-23 (§12.9).*
   - **3d, the consumers,** one at a time behind a fallback: billing terms replacing `Sponsor.BillingInfo` *(built 2026-10-23, §12.10)*, the 834's `REF*1L` resolving against policy numbers *(built 2026-10-23, §§12.11–12.12)*, ID cards *(built 2026-10-23, §12.13)* the 837's `SBR03` *(built 2026-10-24, §12.14)* and claims eligibility *(built 2026-10-24, §12.16)*.
4. **First consumers:**
   - enrollment retro limits, waiting periods, orphan grace and the full-replace guard;
   - the premium grace period, moved off the sponsor's billing info.
5. **The prior-authorization denial-reason mapping** (gap G-17; proposal at `/mnt/project-files/pa-denial-codes/proposal.md` §5, decided 23 October 2026): a setting holding, per internal denial reason, the X12 external list 886 code for the PAS `reviewAction.reasonCode` and the CARC for the PDex EOB `denialreason` slice. CHO ships the CARC column prefilled, because CARCs are freely published, and the 886 column **empty**, because list 886 is X12's property and is not enumerated in the IG package — each tenant fills it from the licence it already holds to run a 278. Three things fix the shape of this one:
   - **An unmapped row emits nothing**, never a fallback. Both target elements have required bindings and `reviewAction.reasonCode` is 0..*, so an absent code is conformant and a wrong one is not. The denial still travels as certification action `A3` plus the prose reason.
   - **Validation is of shape only, with who entered each row and when.** Checking membership in list 886 needs the list, which is the thing CHO does not have; claiming more than shape would be claiming a check CHO cannot perform.
   - **It keys on a string until the internal set closes.** `Authorization.DenialReasonCode` is a free 50-character string today that a rule pack or a reviewer can set to anything, and closing it to an enumeration needs UM policy sign-off (proposal §4a). The setting tightens to that enumeration when it exists.

   Carrier or tenant level with an optional per-program override, not per benefit plan: the map follows a payer's own licence and UM policy rather than a product. The consumers belong to the CMS-0057-F conformance work, not here.
6. **Admin screen** with explain and where-overridden.
7. **Export and import** between environments.

### 12.1 What step 1 built

- **Shared package `CloudHealthOffice.PlanConfiguration`** (src/services/shared):
  - **Definitions** (`SettingDefinition`): key, type (integer, decimal, boolean, enum, duration, JSON), owner, description, unit, default and defaults per line-of-business type, allowed levels, constraints (range, allowed values, floors scoped by line of business with a citation), change control and whether the setting is effective-dated.
  - **The catalog** ships in code (`PlanSettings`, `PlanSettingCatalog`): a service adds its definition there and tenant-service accepts values for it with no other change. `PlanSettingCatalog.Require(key)` fails fast on an undefined key.
  - **The resolution rules** (`SettingResolver`, `SettingValidator`), shared by tenant-service and the in-process client so both answer the same way.
  - **Levels** are an ordered enum with room between them (tenant 100, carrier 200, line of business 300, program 400, benefit plan 500, sponsor 600, sponsor policy 700, class 800). Adding a level is a new definition entry and a scope field, not a resolver change. Step 1 held values at **tenant, line-of-business type and sponsor** (scope id: group number); step 2 added **carrier** (scope id: carrier id), **program** (program id) and **benefit plan** (the plan id a coverage carries).
  - **The client** (`IPlanSettings`, `AddPlanSettingsClient`): resolves through tenant-service, caches per tenant, key, scope and date for `PlanSettings:CacheSeconds` (60), and evicts a key's answers on `configuration.changed.v1` when Kafka is configured (each process listens in its own consumer group). It never throws for an unreachable tenant-service: it answers with the definition's default for the line of business, marked `IsFallback`, logs a warning and does not cache the fallback. `InMemoryPlanSettings` applies the same rules for tests.
- **Configuration module in tenant-service** (collections `configuration-values`, `configuration-pending`, `configuration-changes`; routes `api/v1/configuration/...`, all needing `config:read`):
  - `GET catalog`;
  - `GET resolve?key&asOf&lineOfBusiness&sponsor` (and `POST resolve` for up to 100): value, source level and node, lock, clamp and the trace of every level checked;
  - `GET overrides?key&level&scopeId`: every override at the node's level and all more specific levels, with the value in force and the next scheduled change (narrowed to the node's own branch in §12.6, as far as the hierarchy reaches);
  - `GET values/{level}/{scopeId}/{key}/history`, `GET changes?key`;
  - `PUT values/{level}/{scopeId}/{key}` `{value, effectiveFrom, effectiveTo, locked, reason}` and `DELETE` (clear from a date). Nodes: `tenant/_`, `lineOfBusiness/Commercial`, `sponsor/{groupNumber}`. Each write needs the level's permission, is checked for level, type, range, floors (refused at line-of-business level; clamped at resolution elsewhere) and a reason where required, and adds a version — values are never edited in place;
  - two-person settings answer 202 with a pending change; `POST pending/{id}/approve|reject` needs `config:approve`, never by the requester, decided once (compare-and-swap on its status);
  - every applied change is recorded (who, when, why, before, after, version) and published on `configuration.changed.v1`, best-effort: the change is stored first, and caches expire on their own if an event is lost.
- **Effective dating:** at a date a node's value is its version in force then with the latest start, the highest version breaking ties; a cleared version means the node sets nothing from that date; an end date lets an earlier version apply again.
- **Locks:** a locked value at a general level stops every more specific level, and the trace shows the ignored overrides.
- **Permissions and roles:** `config:read`, `config:sponsor`, `config:product`, `config:enterprise`, `config:approve` on `ChoRolePermissions`; roles AccountManager, Underwriter, ConfigurationAnalyst, ConfigurationManager (and the tenant-service role catalogue and portal role names); `config:read` added to EnrollmentSpecialist, ClaimsSupervisor, MemberServices and ComplianceViewer (ComplianceOfficer has it through `*:read`). Until programs bring negotiable ranges, `config:sponsor` writes are bounded by each definition's constraints.
- **First setting moved:** `enrollment.orphan.graceDays`. enrollment-import resolves it through the client; on a fallback it opens the orphan rather than hold it on a guessed default. The interim `enrollment-settings` store, `api/v1/enrollment/settings` and `enrollment:configure` are removed; stored interim values were not migrated (no tenant had set any) and the benefit-plan level returns with step 2.
- **Deferred to step 2b:** the tenant controls (`payment.enforceSeparationOfDuties`, `personalRepresentative.requireSecondUserActivation`) became catalog settings together with their consumers in §12.5.

### 12.2 What step 2 built

- **Carrier and program records** (tenant-service `Hierarchy/`; collections `carriers`, `programs`):
  - A **carrier** is a legal entity of the tenant: code (upper case, unique in the tenant), name, legal name, tax ID, NAIC code, payer IDs per transaction, NACHA immediate origin and company identification, and the name and address used on letters and ID cards. One per tenant, code `DEFAULT`, created from the tenant's own record the first time carriers are read — so no caller has to handle "this tenant has no carrier yet", and two concurrent readers cannot create two (the id is `{tenant}|{code}`, so the loser of the insert reads the winner's). A tenant must keep one active carrier.
  - A **program** is a line of business in a state under a carrier: code, name, line-of-business type (a canonical `LinesOfBusiness` name), two-letter state, regulatory contract number, effective dates, active. A program's code, carrier, state and line of business are **fixed once it exists**: every rule set, setting and filing keyed on the program would move with them, so a change means a new program and retiring the old one. Its name, contract number, dates and status can change.
  - Routes `api/v1/carriers` and `api/v1/programs` (list, read, create, patch), all needing `config:read`; a carrier write needs `config:enterprise` and a program write `config:product`, the same split `SettingValidator.WritePermissionFor` applies to settings at those levels. Both are revision-guarded, as the tenant record is.
- **`GET api/v1/programs/by-benefit-plan/{planId}`** follows the plan → program link that benefit-plan-service holds, so every consumer asks one service one question (§7) rather than joining two records itself. Its **404 means the plan carries no program**; a **503 means benefit-plan-service could not be asked**. A caller that could not tell those apart would read an outage as a member with no program.
- **Benefit plans and sponsors carry a program.** `BenefitPlan.ProgramId` and `Sponsor.ProgramId`, both nullable: every record written before programs existed carries none, and a reader must treat null as "not stated" rather than guessing from the line-of-business enum, which carries no state and no program code. Where both are set the program is the authority on the line of business — it is the one a contract was written against — but they are not reconciled on write, because a plan can be given a program long after it was created and refusing the link would leave the plan with neither. A sponsor that buys from more than one program leaves its own field null and carries the program on each plan: the two branches meet at the sponsor policy, not at the sponsor (§3).
- **Resolution through the new levels.** `SettingScope` gains `Carrier`, `Program` and `BenefitPlan`, so the chain runs sponsor → benefit plan → program → line of business → carrier → tenant → platform default. **Naming a program is enough to place a member:** `SettingScope.CompletedFrom` reads the program's carrier and line-of-business type off the one record, so a caller holding a single program id resolves the whole branch above it — which is what "line-of-business defaults move from the enum to programs" amounts to in practice. What the caller already stated wins, so a record it did not name cannot contradict it, and a program the tenant does not have is left as given (the resolution then simply finds nothing at that node rather than failing a read of an unrelated setting).
- **`IProgramDirectory`** (shared `CloudHealthOffice.PlanConfiguration`): `GetAsync` and `ForBenefitPlanAsync`, reading tenant-service, cached per tenant for `ProgramDirectory:CacheSeconds` (300 — programs change about once a plan year). It answers null for a program that is not there and **throws when tenant-service could not be asked**, so a caller can tell "no program" from "did not get to look". `InMemoryProgramDirectory` for tests and hosts without tenant-service.
- **First consumer: `CrdContextResolver`** (fhir-service). The prior-authorization rule engine keys its rule sets on state, line of business and program, and a program record carries all three. Where the member's coverage names a benefit plan sold under a program, all three now come from the program: the line of business the contract was written against, and the state the plan operates in, rather than whichever state the member's mailing address is in. Where there is no program, state and line of business come from the address and the coverage's `cho-line-of-business` coding as before, and `Program` stays null — a correct answer, just a less specific one. **If the program could not be looked up, the context is not filled in from the address instead:** the resolution fails with the IG's `technical` reason and the engine is not called. A member on a Texas STAR plan living in Ohio would otherwise get an Ohio rule set during an outage, which is the confident-answer-about-someone-else's-benefits failure the resolver exists to stop. A coverage that names no plan has nothing to look up, so it takes the address path without a call.
- **Deferred to step 2b** (§12): the money files and the 835 payer moved onto the carrier in §12.3, the letterhead in §12.4, the two tenant controls in §12.5, and hierarchy narrowing in `overrides` together with the rule-set key in §12.6. Nothing of step 2b is outstanding; the sponsor's half of the narrowing waits on step 3.

### 12.3 Step 2b, first part: the money files and the 835 payer

Built 2026-10-23. Who the bank and the provider are told is paying.

- **What this actually moved.** The step 2 plan called this "move from tenant configuration to the carrier", which overstated where it was. NACHA origination and the payer identity were never tenant configuration: they were deployment `appsettings` — `Nacha:ImmediateOrigin`, `Nacha:CompanyId`, `Nacha:CompanyName`, `Payer:Name`, `Payer:Id` — one identity shared by every tenant in a deployment. A deployment serving two tenants originated both under the same entity, and nothing recorded that this was wrong. So this is a promotion rather than a move: the identity becomes per-carrier, which is per-tenant by default and per-legal-entity where a tenant runs two.
- **`ICarrierDirectory`** (shared `CloudHealthOffice.PlanConfiguration`), beside `IProgramDirectory` and built the same way: `DefaultAsync` and `GetAsync`, reading `api/v1/carriers`, cached per tenant for `CarrierDirectory:CacheSeconds` (300). It reads the **list**, because listing is what materializes a tenant's default carrier, so a tenant that has never had one written still gets an answer. `Carrier.ToIdentity()` is the contract, and it copies the payer ids rather than sharing them, since the record is mutable and the directory caches what it is given.
- **`CarrierIdentity.LetterheadName`** is the correspondence name, else the legal name, else **null** — deliberately never `Name`, which is what staff call the carrier and is filled in for every carrier including the default one, where it is the tenant's own organization name. A consumer falling back on it would have started putting that name on files the day carriers existed. Null means the carrier states no letterhead, and the consumer keeps its configured name.
- **Consumers.** premium-billing (sponsor debits), capitation (provider credits) and payment (FFS provider credits, and the 835's `N1*PR` payer name and identifier, in both payment and reversal runs) ask the carrier first and fall back to their configured values field by field. A carrier that states nothing changes nothing, which is what every deployment looks like the day this ships: filling a field in on the carrier is the migration.
- **Fail closed, consistently with §12.2.** An unreachable tenant-service **throws** rather than falling back to configuration. From a consumer's side a carrier that states nothing and a carrier that could not be read look identical, and only the first is safe to originate under the configured entity — the second could draw on a sponsor's account, or pay a provider, in the wrong legal entity's name. The release fails and the drafts or disbursements go back to Pending for the next release; nothing is sent. Same rule as the CRD resolver, same reason: "could not look" must not read as "there is none".
- **One invariant kept.** A provider reassociates an EFT deposit with its 835 on the originating company id, which is the remittance's TRN03. payment-service already refused a `Nacha:CompanyId` that differed from `Era:OriginatingCompanyId`; the check now covers a company id stated by the carrier too, and names which carrier stated it. The file-ID-modifier allocation probe is built from the same carrier, so a carrier with its own origin is correctly a distinct originator at the bank.
- **Not in this part:** correspondence-service has no return address and the ID card renders only the plan name, so the letter and ID-card issuer block is new rendering rather than a moved read. It follows in §12.4.

### 12.4 Step 2b, second part: letters and ID cards name their issuer

Built 2026-10-23. Who the member is told the document is from.

- **What this actually moved.** Nothing, in the sense step 2 meant. correspondence-service had **no sender at all**: a letter carried the recipient's address, the letter's own merge data, and nothing saying who sent it. The ID card was headed by the **sponsor** — the employer group — which is not what a member ID card is conventionally headed by, and was that way not by decision but because CHO had no issuer to put there. So this part adds rendering rather than redirecting a read, which is why it is separate from §12.3.
- **Letters, two ways.** A new `TemplateBlockKind.ReturnAddressBlock` left blank renders the carrier's name and address, exactly as a blank `AddressBlock` renders the recipient's; and a template that lays out its own letterhead reaches the same values through `{{ carrier.name }}`, `{{ carrier.city }}` and so on. Only **stated** values are offered, so a template naming `{{ carrier.name }}` where the carrier states none leaves the path unresolved, which fails the `MergeFieldsResolved` check. Either way the letter is **not sent**: a notice of appeal rights headed by a blank line, or by whatever the deployment happens to be called, is worse than one that fails its checks. Every template that exists today has neither, so none of them changes.
- **The written record.** A letter is stored write-once and its data snapshot is the audit trail. It has always recorded who the letter went *to*; it now also records which legal entity it went out *under* (`carrier.id` and the letterhead name as rendered). A letter composed with no carrier records `carrier: null` rather than an invented one. The **preview** uses the same carrier, so a reviewer approving a template sees what will be on the page.
- **ID cards.** `CardBindings` gains `IssuerName` and `IssuerAddress`. The card is headed by the issuer and carries its address on the back; the SVG layout gains `{{IssuerName}}`, `{{IssuerAddress}}` and `{{CardTitle}}`, where **`CardTitle` is the one that falls back** to the sponsor. A tenant whose carrier states no letterhead name therefore gets exactly the card it got before, and the employer group stays in the Group field where it belongs. A carrier with no address puts no blank line on the card.
- **Fail closed, as in §12.3.** An unreachable tenant-service throws: composing a letter or issuing a card fails rather than producing one that names the wrong issuer, or none. In idcard-service the carrier is read in the same parallel wave as the sponsor and plan, so it costs no extra round trip.

### 12.5 Step 2b, third part: the two tenant controls become settings

Built 2026-10-23. Two switches that decide whether one person can act alone.

- **What moved.** `payment.enforceSeparationOfDuties` (capitation-service: who may release a payment run) and `personalRepresentative.requireSecondUserActivation` (personal-representative-service: who may activate a representative) were read straight out of the tenant document, each by a small cache and HTTP client of its own that duplicated the other almost line for line. Both are now catalog settings, resolved through `IPlanSettings`, so they get effective dating, history, a reason on every change, the `config:*` permissions, the admin screen and change-event eviction — none of which the tenant document gave them.
- **Tenant level only, and two-person approval to change.** Neither is something a sponsor or a program negotiates: they are internal controls over the tenant's own staff. Both default to `true`, and **turning either off needs a second person with `config:approve`** — `ChangeControl.TwoPersonApproval`. A maker-checker rule that one person can remove single-handedly is not a maker-checker rule, and the configuration module already had the mechanism.
- **Fail closed, for free.** The client answers the definition's default and marks it `IsFallback` when tenant-service cannot be reached, and the default is the enforced one, so an outage cannot turn either control off. The consumers do not read the tenant document in that case: the service that just failed is the same one that holds it, and a 404 or an empty body there would read as "no override" — the "could not look" / "there is none" confusion §12.2 exists to prevent.
- **The one tenant whose behaviour must not change.** Where **nothing is set** in plan configuration, both consumers still read the tenant document, so a tenant that turned a control off before plan configuration existed keeps the behaviour it configured rather than having a two-person rule silently switched back on. That read logs a warning naming the tenant and the setting to move the value to, and it goes away once the values are moved. Where a value **is** set, plan configuration is the authority even where the two disagree, and the tenant document is not read at all.
- **Audited the same way.** personal-representative-service already wrote an audit warning and a `secondPersonOverride` flag on the activation event whenever the creator activated alone. Which of the two places the override came from does not change what is being recorded — that one person acted alone — so the audit trail is untouched.

### 12.6 Step 2b, fourth part: a question answered about one branch

Built 2026-10-23. Two places that named the hierarchy without following it.

- **`GET overrides` was tenant-wide below the node.** Asked where a setting is overridden under one carrier, it returned the carrier's own override and then **every** node in the tenant at every more specific level — other carriers' programs, other programs' plans. The answer was correct as a list and wrong as an answer: the question is "what overrides this node", and a reader taking the list at face value would attribute another carrier's program override to this one. It now follows the links step 2 added: a **carrier → its programs**, a **program → its benefit plans**, and a **line of business → the programs of every carrier that runs it**, which falls out of the same read. Plans come from benefit-plan-service, which owns the plan → program link, through a new `?program=` filter on `GET api/v1/plans` — the only way to ask the question from this side, since the link runs from the plan.
- **What it says about itself.** The response is no longer a bare list. It carries the node asked about, the overrides, the levels the list **was** confined to, and the levels it **was not** with the reason for each. Both halves matter: a caller reading a narrowed list as the tenant's, or an unnarrowed one as this branch's, is wrong either way, and the honest fix is to say which it is rather than leave it to be inferred from a shape that looks the same both ways.
- **Sponsor policies wait for step 3.** A carrier does not reach a sponsor: the link runs through the sponsor contract, and the contract, its policies and its classes are what step 3 builds (§9b). So sponsor, sponsor-policy and class nodes are still listed whatever branch they belong to, and the response says exactly that in the reason. Narrowing them is step 3's work, with the records it creates. Guessing the link now — from a sponsor's own `ProgramId`, say — would be wrong for precisely the sponsor the field exists to describe: one that buys from more than one program leaves it null and carries the program on each plan (§12.2).
- **An outage gives up the narrowing rather than the list.** Where the hierarchy could not be read, every override below the node is still returned and the levels are reported as unnarrowed. The failure that must not happen is the opposite one: a narrowing built on "no programs" would answer that a carrier overrides nothing, which is the "could not look" reading as "there is none" that §12.2 and §12.3 are built against. And where one level could not be read, the narrowing is given up at **all** of them, because one carrier's programs beside another's plans is harder to read correctly than an unnarrowed list that says so.
- **`ResolvedRuleSetKey` names a record.** A PA decision recorded which rule set decided it as `txmco01/TX/Medicaid/STAR` — a string that looks like an identifier, is built out of four values, and points at nothing. Where the rule set belongs to a program it is now that **program's id**, which can be looked up; `PaRuleContext` carries the id beside the code, and `CrdContextResolver` fills it in from the program record it already reads. Where the rule set is **not** one program's — the state-and-line-of-business fallback — it still says so, so the fallback from a program's rules to the state's stays visible in the audit trail instead of being recorded as the program's own. A pend for want of any rule set names what was looked for, not the member's program: nothing of that program's was loaded.
- **The lookup did not change.** Rules are still stored, cached and resolved on state, line of business, program code and tenant, so there is no migration of the rule documents and no change to the four-step fallback. The program id travels beside the key rather than inside it — deliberately, since the engine de-duplicates its candidates on the key's string form, and a program id there would make a tenant's rule set and the platform's look like one and skip the platform fallback. The engine stamps the key on the decision, because the engine is what knows which rule set it loaded; a rule only knows its own document.

### 12.7 Step 3, first part: the records a contract is made of

Built 2026-10-23, in sponsor-service. §9b describes a contract this codebase had nowhere to put: a sponsor had a group number, flat billing information and nothing between itself and a benefit plan. This stage builds the two records the rest of step 3 hangs off, and nothing reads them yet — deliberately, so that each consumer moves on its own in 3d rather than as part of the change that creates the records.

- **A sponsor policy is a plan offered under its own number**, under the group, with its classes. Its id is tenant, group number and policy number, so the policy number is the identity rather than a field on it, which is what §9b means by a record that lasts across renewals.
- **A contract term is a period with a status**, following §9b's flow: draft → in underwriting → signed → active, with ended reachable throughout. The transitions are a table rather than scattered `if`s, so an invalid move is a refusal with the moves that *are* allowed, and every change is kept with who made it, when and why. Signing requires the id the signed agreement was stored under: a term that says it is signed with nothing attached is the record claiming something nobody can check.
- **Identity fields are refused on an update, not ignored.** §9b's continue-versus-replace rule is the whole reason the policy record exists, and the way to break it is a PATCH that moves a policy's benefit plan: every member's coverage changes with no enrollment, no ID card and a 200 back. The policy number, benefit plan and program are therefore refused with the reason and the thing to do instead — end this policy the day before the renewal and start a new one. Refused rather than silently dropped, because a caller who sent a change and got 200 has every reason to believe it happened. Sending a policy's own current values back is not a change, so a client that round-trips the whole record still works.
- **Two periods cannot cover the same day.** The term in force on a date is what every consumer in 3b will resolve against, so an overlap would make that question have two answers. Ended terms are ignored in the check, since a period that was abandoned should not block the period that replaced it.
- **A term cannot be active before it starts.** Signed and future is the normal state of a renewal for the months before it begins; active means in force now, and so is refused until the period start.
- **"No term" is an answer, not a gap.** `contract-terms/in-force` 404s where the group has no term covering the date, which is every group until one is entered. A consumer in 3d falls back on what it reads today rather than treating it as a failure — the same reading the rest of this document is built on, that could-not-look must never read as there-is-none.
- **Writes are revision-guarded** the way the sponsor record is: a replace lands only while the stored revision is still the one the write read, so a concurrent edit is a conflict rather than one edit quietly undoing the other. The guard spans the read and write inside one request; it is not an ETag a client holds across requests, so two people editing the same policy from screens opened minutes apart still have a last-write-wins problem at the field level. That is the sponsor record's existing behaviour, and worth fixing for both at once rather than for this record alone.
- **Reading needs `config:read` or `enrollment:read`** — enrollment has to see which policies a group offers — and writing needs `config:sponsor`, which §9a already assigns to the account manager who enters a contract.

26 tests. What they cover is the handful of rules nothing downstream could recover from: a policy whose plan was swapped under its members, two periods covering one day, a term signed with nothing attached, a term active before its contract, and another tenant's group answering `NotFound` rather than an empty list.

### 12.8 Step 3, second part: a year of terms, and the floors under them

Built 2026-10-23. §9b's per-year sections — rates by tier, contribution, eligibility by class, the waiting period and effective-date rule, termination and retro windows, life events and their documentation — now exist as a version of a policy's terms per contract period. Still nothing reads them; the consumers move in 3d.

- **Resolving a date goes through the contract term, not past it.** §9b has the term covering that year's policy terms, so a version is in force only while a term that is **signed or active** covers the date. A draft year's rates are a quote, and billing from a quote is the mistake this arrangement exists to prevent. That is also why a version is keyed on the contract period rather than carrying dates of its own: two records each holding their own dates would eventually disagree about which year a date belongs to, and then the question "what did this member pay in March" would have two answers.
- **Finding nothing has four quite different reasons, and they are not interchangeable.** A resolution says which applies: the group has **no contract term** signed or active over the date; the **policy is not offered** then; the contract exists and this policy's **terms for it were never entered**; or it resolved. The first is every group until a contract is entered, and a consumer may fall back on what it reads today. The third must never be read that way — a contract with a hole in it is not a group without a contract, and falling back there would bill last year's rates under this year's agreement. It is logged as a warning for the same reason. This is the same discipline as §12.6's narrowed-versus-unnarrowed answer: where a caller reading one state as another would be wrong, the answer says which state it is rather than leaving it to be inferred from a shape that looks the same either way.
- **An unknown class is not the default class.** A class may carry its own rates and eligibility, and one that carries neither falls back on the policy's — that much is ordinary. What is not ordinary is a class the terms say nothing about: handing back the policy-wide rules there would rate and enrol a member under rules written for somebody else. So the record answers separately whether it covers a class at all, and terms for a class the policy does not have are refused outright, since terms nobody can be placed under would still read as a real answer about that class.
- **A signed year is settled.** Terms may be entered and restated while their contract term is a draft or in underwriting; once it is signed or active they are what both sides agreed, and an edit in place would change them with no record of what moved or when. An amendment is therefore a contract event — a new period — rather than a PATCH. That is deliberately strict, and the strictness is the part worth reviewing: a mid-year amendment is a real thing that happens, and the alternative is a status move back to draft, which the transition table does not allow from signed.
- **A rate table quotes exactly its structure's tiers.** A four-tier table missing the employee-plus-children rate is a household shape nobody can bill; a tier from another structure is a rate nothing will ever look up. Both are refused, and a table is stored in its structure's own order so that two tables of the same structure cannot look different.
- **The federal floors §9b names are refused rather than clamped.** A waiting period over 90 days, a child age limit under 26, a special-enrolment window under 30 days, or under 60 after losing Medicaid or CHIP. A contract may be more generous than a floor and not less, and only the events federal law actually gives a window to are held to one — a plan's own rule for an internal transfer is not special enrolment. These are **not yet `SettingFloor` entries with citations to the regulation text**, which is still an open question (step 3 proposal, question 4); what is safe meanwhile is to refuse the writes that would be unlawful either way, with the number and the reason named, because a refusal cannot create a non-compliant contract and a silent clamp would hide one.

**The contract term's own per-year sections** are billing terms and underwriting conditions, which belong to the term rather than to a policy because the group is billed and underwritten as a whole. The billing terms are the ones §9b moves off `Sponsor.BillingInfo`, where one set of terms covered the account however many years it ran, and the grace period keeps the same 0-to-90-day range the sponsor record allowed, so moving the field does not change what a customer may enter. Underwriting conditions are **data only** — the thresholds a quote was written against, and a note on what was agreed. Who routes a term that misses them and who decides is the approval workflow, which §9b leaves to enrollment step 4.

Two things in there are worth naming:

- **Signature settles the agreed terms and not the plumbing.** The billing method, how often the premium falls due, the day it falls due on and the grace period are what both sides signed, so after signature they move only by opening a new contract period. The billing account number and the payment method are not agreed terms — they are how a payment gets applied — and a group that changes bank in March should not need a new contract period to say so, so those two stay correctable. Billing terms entered for the first time *after* signature are refused rather than accepted, since that would be stating terms nobody signed.
- **A due day past the end of a month lands on its last day.** A due day of 31 means the end of the month, not a date that does not exist, and clamping it in the record is what stops every consumer getting February wrong in its own way.

57 tests across 3b.

### 12.9 Step 3, third part: the contract's own levels, and the branch they make

Built 2026-10-23. This closes the item step 2b deferred (§12.6) and wires the two levels under the sponsor into resolution, now that 3a and 3b have built the records they hang off.

- **A sponsor reaches its policies, and through them their classes.** `GET overrides` asked about a sponsor now confines the list to that sponsor's own policies and their classes, through sponsor-service's `GET api/v1/sponsors/{group}/policies`. Asked about one policy, it confines the classes to that policy's. The link is real rather than guessed: the classes 3a built compute their own node id from the policy's id, which is why §9b put that computation in one place.
- **What a carrier still does not reach is a sponsor.** The deferral is gone but the honest limit under it is not: the link runs from a policy to a plan, so a sponsor buys plans rather than belonging to one, and there is no read from a carrier or a program to the sponsors under it. From the plan side the three contract levels stay unnarrowed, with a reason that now says *why* instead of saying "step 3 will fix it". Narrowing them would need sponsor-service to answer "which policies are in this program", which nothing asks yet; when something does, that is the read to add.
- **`SponsorPolicy` and `Class` are in `SettingScope`,** in the chain, in the cache key and on the resolve endpoints. A class resolves through its policy, its sponsor, the plan, the program, the line of business, the carrier and the tenant, in that order.
- **A class named without its policy is refused, not resolved.** It looks like a convenience to accept one and skip what was not named, and it is the same mistake in a new place: resolution would check the class and then the tenant and answer as though neither the policy nor the sponsor had set anything — "could not look" reading as "there is none" again. The caller holding a class id holds the policy's already, because the class id is built from it. Same for a policy named without its sponsor.
- **An outage gives up the narrowing rather than the list,** as on the plan side. A sponsor whose contract could not be read still has every policy and class override returned, labelled unnarrowed, because narrowing on "no policies" would answer that the sponsor overrides nothing. A group that genuinely has no contract — which is every group until one is entered — *does* narrow to nothing, and that is correct: it has no policies.
- **A policy id that does not name its group is reported, not guessed.** sponsor-service lists policies by group, and a policy node names only itself, so the group is taken from the policy's id. Where the id is not in that form, the class level comes back unnarrowed with its own reason rather than narrowed to nothing. The id format is therefore known in two services that share no package; it is pinned by a test on the tenant-service side, so a change on either side fails loudly instead of quietly matching nothing. If a third service needs it, it belongs in a shared contract.

24 tests.

### 12.10 Step 3d, first consumer: billing on the contract's terms

Built 2026-10-23. The first thing to read a sponsor contract rather than merely store one: premium billing takes the day a premium falls due, how long it may run late, and how it is paid from the contract term in force over the period being billed, and from the sponsor record where there is no contract. This is the field §9b explicitly moves off `Sponsor.BillingInfo`.

- **The fallback is the feature.** A group with no contract term covering the period bills exactly as it does today, which is every group until a contract is entered. Nothing has to be migrated, no tenant has to opt in, and a tenant that never enters a contract never sees a difference.
- **"Could not read the contract" is not "there is no contract".** A sponsor-service failure, or a permission the deployment has not granted, leaves that one sponsor unbilled with a warning on the run — the same shape coverage-service failures already have, which never produce a $0 invoice. Billing them on the sponsor record instead would quietly bill last year's due day under this year's signed agreement, and nothing downstream would ever show which it used. One sponsor's unreadable contract does not stop the rest of the run.
- **A contract with its billing left blank falls back, but says so.** It is a separate answer from "no contract" (`BillingNotEntered` against `NoContractTerm`) and is logged once per sponsor per run, because the two read very differently to a person: one is a group nobody has contracted with, the other is a contract somebody left half entered. A consumer treats them the same, which is why the endpoint rather than the consumer draws the distinction.
- **Billing terms sit on their own route, not inside the term.** `GET api/v1/sponsors/{group}/billing-terms?asOf=` admits `billing:read` — what premium billing already holds to read the sponsor list — while the whole contract term needs `config:read`. Finance has no business reading a group's rates, eligibility or underwriting conditions to find out when its premium falls due, and the same reasoning already put the status-only `PUT` beside the full sponsor update.
- **The date asked about is the period being billed, never today.** Terms are per contract year, so asking about "now" would bill March on next January's renewal the moment that renewal is entered.
- **A contract term's billing account number is now masked like the sponsor record's.** It was not when 3b built it: the masking converter covered `BillingInfo` only, so the same number was written in the clear one route over, and the stricter rule decided nothing. A matching converter now covers `BillingTerms`, request bodies are still read unchanged so the number can be set, and storage is unaffected. Premium billing never reads the number at all.
- **Only the three fields premium billing already uses move.** The contract's billing method and frequency are stored and read by nothing: honouring a quarterly frequency would change which periods are invoiced at all, which is a change to the billing run rather than to one sponsor's dates, and it belongs with whatever asks for it. A payment method the contract leaves blank keeps the sponsor record's, since a blank is an operational detail nobody agreed rather than a method of "none".

21 tests.

### 12.11 Step 3d, second consumer, first half: what an inbound `REF*1L` names

Built 2026-10-23, in sponsor-service. The 834's `REF*1L` is "group or policy number" and a trading partner may send either; CHO has always read it as a group number and nothing else. This half answers which one a value is; §12.12 records the answer.

- **The group number is tried first and wins outright.** That one ordering is what makes this additive: a value that resolves to a group today resolves to the same group afterwards, and a policy number is only ever consulted for a value that means nothing at all today. If a group is called `SHARED` and some *other* group happens to have a policy numbered `SHARED`, the file has always meant the group and still does. Reversing the order would silently re-file an existing partner's members under a different employer, which is why the order has a test of its own rather than being left to a reading of the code.
- **One policy number on two groups answers ambiguous, with the groups named.** A policy number is unique within a group and nothing makes it unique across groups, so this is a reachable state rather than a corrupt one. Picking either would file a member under the wrong employer; the response says which groups it could be and refuses to choose. `GET api/v1/sponsors/resolve-identifier` answers 409 for it, 200 for either resolved kind and 404 for a value that is neither.
- **A policy whose offering has ended still identifies its group.** A file naming last year's policy is a file about the right employer, and the date is the enrollment's problem to report. Refusing to identify the sponsor would turn a reportable date into an unmatched record, which is strictly less information.
- **The route is tenant-wide, not under a group,** because the question it answers is *which* group — and it admits `enrollment:read` as well as `config:read`, since the caller is the enrollment importer.

11 tests, most of them about the two orderings above.

Resolving a member's policy instead by matching their benefit plan against the group's policies goes ambiguous exactly when a sponsor offers two policies on one plan, which is the case §9b exists to model, so it is not a usable substitute for the file saying which.

### 12.12 Step 3d, second consumer, second half: enrolment records the policy

Built 2026-10-23. The enrollment importer now asks §12.11 what each `REF*1L` is, files the member under the group either way, and keeps the policy number on the coverage it writes.

- **It closes a defect rather than only adding a field.** An unrecognised `REF*1L` has always fallen through to the create path, so a partner sending a policy number got a brand new employer whose group number *was* that policy number — silently, alongside the real employer, with that member's coverage, premium and reporting hanging off the wrong one. A policy number is now recognised for what it is, and the group it belongs to is the group the member is filed under.
- **The policy goes on the coverage record, not the member.** A §9b policy is a group plus a policy number pointing at a benefit plan, and coverage already carried the other two parts of that identity. It is a property of the coverage rather than of the person: one member can hold medical under one policy and dental under another, and at renewal the same member moves policy while staying the same member — on a member record those two cases are indistinguishable. Coverage is already effective-dated the way a policy is, so a renewal that moves someone is a new coverage span, which is machinery that already exists. Every consumer still waiting (ID cards, the 837's `SBR03`, claims eligibility) already reads coverage, so no other home would have saved them a read.
- **Named `SponsorPolicyNumber`, holding the number and not the id.** `Coverage` already has a `PolicyNumber` — on `OtherInsuranceInfo`, meaning the *other* carrier's policy for coordination of benefits — and the two sitting an object apart meaning opposite things is a trap worth naming out of existence. The number rather than the policy id because the id is `{tenant}|{group}|{policyNumber}`, both of which the coverage already knows, so the id would repeat them and could drift from them.
- **An ambiguous number rejects that transaction, naming both employers.** It lands in the enrollment exception queue under its own reason code (`AMBIGUOUS_SPONSOR_IDENTIFIER`) rather than any generic refusal, so the queue can tell "the file names two employers" from everything else, and it is reprocessable once the partner sends the group number or the duplicate policy number is resolved. Picking a candidate would file a member, their coverage and their premium under the wrong employer with nothing downstream showing it happened.
- **"Could not ask" must never read as "neither number is on file",** because that is precisely the branch that creates an employer. A sponsor-service outage or a missing permission fails that member into the queue as reprocessable; a 404 and a 409 are answers and are acted on, and a 200 naming a kind but no group number is refused as a wire mismatch rather than filing coverage against nothing.
- **One lookup per distinct `REF*1L` in a file, not one per member.** A real 834 carries thousands of members under a handful of groups, and the sponsor file does not change under an import. Only a recognised answer is remembered: "neither number is on file" is the one answer the import itself invalidates a moment later, when the N1 loop creates the sponsor, so it is asked again.
- **A dependent with no `REF*1L` of its own is held under exactly its subscriber's policy;** one carrying its own resolves it separately, since a dependent's Loop 2000 may name a policy just as a subscriber's may. A standalone dependent clears the state first, so the previous member's policy cannot carry into it.
- **The eligibility class deliberately did not ride along.** There is no migration to share — coverage is a Mongo document and a new nullable field reads as null on every existing record — so the only thing bundling them would have bought is a field nothing writes (a class would have to come out of the 834's reporting categories, which nothing maps) and nothing reads (the only class in the codebase is the rate table inside the contract). Always-null would read as "this member has no class" rather than "we do not capture class", which is worse than absent. It goes in with the first thing that rates or restricts eligibility by class, which is also where where-it-comes-from gets answered rather than guessed.
- **What the policy's own benefit plan does not do.** `resolve-identifier` also answers the policy's `BenefitPlanId`, and the importer still resolves the plan from the HD plan code as before. Letting the policy override that would change which plan members land on, which is a separate decision with its own fallback, not a side effect of reading the identifier.

24 tests, split between what the wire answers mean and what the importer does with each answer.

### 12.13 Step 3d, third consumer: the policy on the member's ID card

Built 2026-10-23. A provider keys eligibility off the card, so where a member holds one of several policies of the same employer, the card is where that has to be legible.

- **It reads the coverage, which is the whole reason the field went there.** idcard-service already resolves the member's coverage for the group number and the plan id; the policy number comes back on the same read, with no second lookup and no new dependency. §12.12's choice of home is what makes this a three-line change.
- **Both the group and the policy, never one in place of the other.** The group says who the employer is; the policy says which of its offerings this member joined. A provider phoning about eligibility has to be able to name both.
- **The field is conditional, so no existing card changes.** It sits with Group, Plan and Network in a field list that omits what is empty, so a card for a member enrolled by group number — every card until a tenant enters policies — is laid out exactly as it is today, rather than gaining an empty label. A blank string is treated as none, because a label with nothing after it reads as a card printed wrong.
- **The built-in SVG layout deliberately does not place the token.** An SVG draws text at fixed coordinates and cannot close a gap the way the PDF's field list does, so a line that is empty for most members would leave a hole. The token (`{{SponsorPolicyNumber}}`) exists for a tenant's own layout, which knows whether its members are on policies.

6 tests.

**The 837's `SBR03` turned out not to be a policy field at all**, which §12.14 takes up.

### 12.14 Step 3d, fourth consumer: the group number in the 837's `SBR03`

Built 2026-10-24. `SBR03` was slated as a consumer of the sponsor policy and is not one. It is "insured group **or** policy number" and carries the **group** number where there is one, which CHO coverage always has — so a member's sponsor policy number has no element of its own in loop 2000B, and putting it in `SBR03` would be wrong rather than more informative. Whether a particular receiver wants the policy number there is a per-receiver companion-guide question, not a CHO default.

What the reading turned up instead were two real `SBR03` defects, neither about policies, and both now fixed:

| where | was | is |
|---|---|---|
| `encounter-service` (the live generator) | `SBR03` written empty | the group number |
| `CloudHealthOffice.EncounterEngine` (no production caller) | the **subscriber id** — a person identifier in an element that means a group, so the group number never went at all | the group number |

- **The group number arrives on the create request, not from a coverage lookup.** The caller builds an encounter from an adjudicated claim that already resolved the member's coverage, so resolving it again here would add a second outbound dependency on the submission path for a fact the caller already holds. The encounter's create body *is* its record, so this is one nullable field rather than a new contract.
- **An encounter with no group number writes the segment byte for byte as before.** Every encounter on file today has none, so that is the output that must not move, and it has a whole-segment test rather than an element one. The element is situational in the IG, so an empty one stays conformant and is only less informative.
- **No other element shifts.** A receiver reads a segment positionally, so an element that moves is a silently wrong file rather than a rejected one. Both generators keep every other element where it was, and a group number carrying a stray `*` or `~` is stripped rather than allowed to split the segment in two.
- **A correction inherits the group number when its body omits one.** The void half was already built from the original; the replacement half is the caller's own body, and a caller written before the field existed would otherwise send a replacement that says less about the member than the file it replaces. An explicitly supplied value still wins, so the inheritance fills a gap without overruling anyone.
- **Taking the subscriber id out of the engine's `SBR03` loses nothing.** `SBR02` is `18`, meaning the patient *is* the subscriber, so the member id already in that loop's `NM1*IL` is the subscriber's identifier.

12 tests across the two generators.

Reading this segment also turned up a defect in the element next to it, taken up in §12.15.

Still to come in 3d at the time: claims eligibility, which §12.16 takes up and which completes the step.

### 12.15 The claim filing indicator in the 837's `SBR09`

Built 2026-10-24, from the defect §12.14 found and deliberately left out of that change. `SBR09` is the claim filing indicator code (X12 element 1032): the element that tells the receiver what kind of coverage a claim or encounter is under. No generator in the codebase was writing it.

Three were found, not the two §12.14 recorded. The third is the one that matters most, because it is the only one of the three submitting to a live programme:

| generator | was | is |
|---|---|---|
| `encounter-service` | `MC` at `SBR07`, two elements early; `SBR09` absent | the code for the encounter's line of business, at `SBR09` |
| `CloudHealthOffice.EncounterEngine` | **two** codes, `HM` at `SBR06` and `MB` at `SBR08`; `SBR09` absent | one code at `SBR09` |
| `claims-service` FMMIS transformer (Florida Medicaid, live) | `MC` at `SBR07`; `SBR09` absent | `MC` at `SBR09` |

`SBR04` to `SBR08` are all "not used" in loop 2000B, so in each case the code sat in an element that does not mean what it held while the element that does mean it was empty. A receiver parsing positionally reads a well-formed segment that says nothing about the coverage — the failure mode is a silently wrong file, not a rejected one, which is why the tests here count elements instead of matching substrings. A `Contains("MC")` assertion would have passed throughout the defect's life.

Corroboration that `SBR09` is the right element and these three were the outliers: every other `SBR` in the repository — the COB fixtures, the parser's own samples, the portal's benefit-plan validation file — already put the filing indicator at `SBR09`.

- **`encounter-service` now derives the code instead of asserting it.** It wrote the constant `MC` for every encounter, so a Medicare or commercial encounter was filed as Medicaid. The encounter record already carries both the line of business and the 837 variant, so moving the element without using them would have put the wrong code in the right place. Commercial and Exchange map to `CI` (a qualified health plan *is* commercial insurance, and element 1032 has no exchange-specific code), Medicaid to `MC`, TRICARE to `CH`, VA to `VA`, and Medicare splits on the variant — `MA` for institutional, `MB` for professional, because those are Part A and Part B.
- **An unrecognised line of business falls back to `MC`, the code written today.** `LineOfBusiness` has no member at `0`, so an encounter created without one lands there. Keeping today's value as the fallback means the only encounters whose `SBR09` content changes are the ones that say in so many words that they are not Medicaid.
- **The engine's two codes could not both be right, and neither was defensible.** `HM` and `MB` are both from element 1032, so the segment handed a receiver contradictory codes in two elements that mean neither. The engine's input carries no line of business, so its single code is a constant matching `encounter-service`'s fallback, and a caller that gains one should supply it rather than rely on that.
- **FMMIS needed only the position.** Florida Medicaid is always `MC`, which is why the code is a companion-guide constant there; nothing about the value was ever in question.
- **Fixtures that claimed to be generator output were refreshed.** Several 837 samples in the claims tests carry a comment naming the transformer that produced them. Left alone they would have gone on describing a shape the generators no longer emit, which is the same trap in miniature: a stale fixture reads as current truth. One hand-authored sample mirroring a smoke script is left as it is, because it stands for a file a trading partner sends us rather than one we build, and the parser's tolerance of it is the point.

15 tests across the three generators. The claims suite (1689 tests) passes unchanged apart from the refreshed fixtures.

### 12.16 Step 3d, last consumer: claims eligibility

Built 2026-10-24. "Claims eligibility" in the build order turns out to name one specific, verifiable gap rather than an eligibility rewrite: **claims adjudication could not see a plan setting entered at any of the levels steps 3a to 3c had just built.**

It resolved exactly one setting (`pricing.inpatient.groupDrg`, from #1517) and passed `new SettingScope { LineOfBusiness = ... }` — nothing else. So a value entered on a benefit plan, on a sponsor, or on one of that sponsor's policies resolved to the tenant default. Not refused, not logged: a fallback is a legitimate answer, so a payer who had deliberately turned DRG grouping on for one employer saw claims priced as though they never had.

The fields were not missing. coverage-service's active-coverage response already carried the group number and the sponsor policy beside the plan id; `HttpCoverageResolver` deserialised **only** `planId` and threw the rest away. So the change is mostly a matter of stopping the discard:

- **The resolver returns the coverage, not one field of it.** `ICoverageResolver.ResolveBenefitPlanIdAsync` became `ResolveCoverageAsync`, answering a `CoverageResolution` of plan id, group number and sponsor policy. Its non-throwing posture is unchanged, and the sponsor hierarchy is read off the *same* coverage the plan id came from — otherwise a claim could be priced on one coverage's plan and scoped by another's sponsor.
- **The cache caches the whole thing.** `CachingCoverageResolver`'s key is unchanged, built from the same inputs, but its cached *value* widened. Had it kept caching the plan id alone, the first claim for a member would have resolved at the policy and every claim behind it, for the five-minute TTL, would have fallen back to the tenant default — intermittently and invisibly.
- **One scope builder, on the context.** `ClaimAdjudicationContext.SettingScopeForClaim()` is where every stage reading a plan setting gets its scope, so stages cannot drift apart on which levels a claim is entitled to see.
- **The benefit plan comes from the claim, not from the coverage.** The orchestrator writes the resolved plan back onto the claim, and the JSON `/import` and MCC on-ramps supply one directly; reading the coverage instead would have left those two on-ramps stuck at the line-of-business level.
- **The catalog entry's deferral is over, and its comment rewritten.** `GroupInpatientDrg` carried a note that benefit plan "joins when plan-configuration step 2 lands — `SettingScope` carries no benefit-plan id yet, so declaring the level here would accept a value nothing could ever set." Step 2 landed as #1515 and the scope now carries all seven levels, so the entry gains BenefitPlan, Sponsor and SponsorPolicy. **Class is deliberately excluded:** an eligibility class divides a policy's members by who they are, and whether a stay is priced on the billed or the grouped DRG is a property of the contract, not of the member — a class-level value would let one employee's stay be priced by a different method from a colleague's.

**The trap worth naming, because it would have shipped a change that did nothing.** A sponsor policy is not named in the hierarchy by its policy number. sponsor-service's `SponsorPolicy.IdFor` builds `{tenantId}|{groupNumber}|{policyNumber}`, with the policy number trimmed and upper-cased, and that composite is the node id a stored value is keyed by. A scope carrying the bare policy number matches nothing and resolves a level higher — silently, for the same reason the original gap was silent. claims-service is the first service that has to *build* that name rather than read one it was handed (tenant-service only parses them), so it now lives in `SponsorSettingNodes` in the shared `CloudHealthOffice.PlanConfiguration` package, with literal-string tests pinning both forms. sponsor-service publishes no contracts package, so nothing can call its builder directly; if a fourth consumer appears, the answer is that package rather than a fourth copy.

A policy is **dropped** when no sponsor came with it. `SettingScope.Incoherent()` refuses such a scope — rightly, since answering it would skip the levels between the policy and the tenant — and tenant-service answers 422. Resolving one level shallower is the right failure: a claim should not stop adjudicating because the hierarchy above it was only partly known.

**What this does not reach, and why it was left alone.** The two on-ramps that carry their own `BenefitPlanId` (JSON `/import` and MCC) make no coverage call at all, so they resolve at benefit plan and line of business without the sponsor levels. Resolving coverage for them as well would close that, and it is one line — but `Adjudicate_ClaimWithExistingBenefitPlanId_DoesNotCallCoverageResolver` holds in so many words that those claims "must not override **or even query**" coverage. That is a decision with a test behind it, so it stands until someone chooses to change it rather than being overturned to suit this change. The 837 on-ramp, where a claim arrives without a plan, is the one that matters in practice and is fully covered.

Additive on merge: no values exist at the new levels yet, so every claim resolves exactly where it did before until a payer enters one.

20 tests.

With this, step 3d is complete.

## 13. Decisions (2026-10-22)

1. **Where it lives:** a module in tenant-service with a clean boundary (§9). Carrier and program live there too, and a new service would be operational cost without a need yet.
2. **Carrier:** built now, one per tenant by default. Legal-entity details move onto it; tenant controls become settings; technical configuration stays (step 2).
3. **Who changes what:** permission-driven by job function (§9a). Account managers configure sponsor terms as drafts on the contract, within negotiable ranges; underwriters approve out-of-range terms; configuration analysts own products; operations staff read and explain.
4. **#1500:** merged as is; its store is interim and moves into the catalog in step 1.
5. **Sponsor contracts** are the home of sponsor-side configuration (§9b).
6. **Change requests from operations** are tracked in CHO. "Request a change" on any setting raises a configuration work item (work type `ConfigurationChange`) in work-management, carrying the setting, the node, the effective value with its source, and the member or exception that prompted it. Routing: sponsor, policy and class settings to the sponsor's account manager; program and plan settings to a configuration queue; tenant and carrier settings to the configuration manager. The request links to the resulting change set and any approval. An optional external ticket reference holds a ServiceNow or Jira number; a two-way connector waits for a pilot that needs it.
7. **CRM:** CHO is the system of record for the signed contract terms that enrollment, claims and billing execute against. The CRM keeps the sales pipeline (prospects, quotes, brokers, renewal opportunities). The contract term stores the CRM opportunity id and link, and CHO exposes "create draft contract term" so a won opportunity can open the draft; a Salesforce connector waits for a pilot that uses it.
