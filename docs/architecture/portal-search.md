# Portal search: finding records by job

**Status:** draft for review, 2026-10-21. Open questions are in §9.

**Builds on:** `portal-navigation.md` §3.2 (global search) and §5.0 (module lists). Those set where search lives. This document sets what each module's search asks for, and why.

## 1. The idea

People don't search a health plan system for fun. Something lands on their desk and they need the one record it's about, or the set of records they're going to act on:

- a provider calls about a claim;
- a fax arrives with an auth request;
- a lockbox file lands;
- a supervisor needs every claim paid on the old fee schedule.

What they have in hand at that moment decides what the search must accept. A provider's billing office knows its own patient account number and its TIN, not CHO's claim number. A member reads the number off their ID card. A cash poster has a check number and an amount. A search that only takes CHO's own keys forces a second lookup, or a guess.

So every module below starts from the jobs that search it:
- **Who:** the role.
- **Trigger:** what made them search.
- **In hand:** the identifiers and facts they actually have.
- **Need:** one record, or a set to work.

The criteria follow from that.

## 2. Two kinds of search

Every module needs both kinds, and they behave differently.

| | Lookup | Work list |
|---|---|---|
| **Question** | "Which record is this?" | "Which records do I act on?" |
| **Typical user** | CSR on a call, examiner checking a duplicate, cash poster | Supervisor, adjustment analyst, collections, compliance, finance close |
| **Criteria** | Identifiers, or name + DOB. Few fields, keyboard-fast | Codes, date ranges, statuses, amounts, LOB, assignee |
| **Results** | Small. A single exact hit opens the record | Large. Counted, sorted, selected in bulk, exported |
| **Keeps** | Nothing, or Recent records | Saved searches, shared with a team, pinned to Home with a live count |

On screen this is one page per module. It has two parts:
- **Find bar:** one identifier box plus the two or three lookup fields for that module.
- **More filters panel:** the work-list criteria, collapsed until opened.

A user on a call never has to open the panel. A supervisor builds a work list in the panel, saves it, and comes back to it every Monday.

## 3. Rules for every module

### 3.1 Identifiers first

1. **One ID box per module accepts every identifier form that module knows.** Examples:
   - a claim number, a patient account number or a payer claim control number;
   - a member ID, subscriber ID with suffix, Medicaid ID or MBI.

   The box normalizes the input and tries each form. If exactly one record matches exactly, it opens.
2. **Normalize before matching:**
   - Strip spaces, hyphens and leading/trailing punctuation from IDs.
   - Upper-case IDs.
   - Check the NPI Luhn digit before calling the server.
   - Accept DOB as `MMDDYYYY`, `M/D/YY`, `YYYY-MM-DD` and so on, and show the parsed date back to the user.
3. **The global box (`portal-navigation.md` §3.2) uses the same identifier rules.** Each module registers its identifier forms with it (§7.3), so adding a module to global search is a registration, not a new branch of code.

### 3.2 Criteria combine

- All filled fields apply together (AND). Today Members uses only the first filled field, and Claims sends some fields the server ignores. Both stop.
- Each field states its match type in its placeholder: Exact, Starts with, Range, Any of.
- Multi-select fields (status, LOB, codes) mean "any of".

### 3.3 Name matching

- **Default: starts with,** case- and accent-insensitive, ignoring hyphens and apostrophes. "Garcia-Lo" finds Garcia-Lopez; "OBrien" finds O'Brien.
- **Sounds like:** a checkbox next to the name fields. It matches phonetically (e.g. "Smyth" finds "Smith"), using a stored phonetic key (§7.2). Callers spell names the way they sound; CSRs need this.
- **No contains search on people's names.** A leading wildcard can't use an index, and "contains 'an'" is browsing, not finding.
- **Organization names** (providers, groups, payers) may match whole words anywhere in the name. "Valley" finds "Desert Valley Cardiology". This is word-prefix matching on indexed tokens, not a raw contains.

### 3.4 Dates say what they mean

- **Service dates overlap.** A claim or auth "with DOS 3/1–3/31" is any whose service span touches that range. Today the claim and auth filters require containment, which misses multi-month inpatient stays.
- **As-of date for status.** "Active" always means active on a date: today by default, or the date the user enters, such as the date of service on the claim being worked.
- **Presets for ranges:** today, last 7, last 30, this month, last month, this quarter, plan year. Saved searches store the preset (relative dates), not the frozen dates.

### 3.5 Minimum necessary, without getting in the way

The existing `SearchCriteriaPolicy` requires "at least one criterion" on PHI modules. That is too weak in one direction and too strict in another:

- **Too weak:** "Status = Paid" is a criterion, and it lists every paid claim.
- **Too strict:** a supervisor's legitimate work list may have no identifier at all.

**Replacement:** each PHI module (Claims, Members, Authorizations, Appeals, member-level Premium and Payments) defines its **selective criteria**. A search must include one of these, or a scope narrow enough to stand in for one:
- an identifier;
- a name with DOB or a first-name prefix;
- a provider (NPI/TIN);
- a date range of 31 days or less;
- an assignee or queue.

A search that is too broad is not rejected silently. The page says what to add ("Add a member, provider or a service date range of 31 days or less") and keeps the user's entries.

Work-list permissions can lift the limit. A role with `claims:worklist` can run wide searches, but always with a count shown first and paging, never an unbounded dump.

**Demo tenants.** The relaxation follows the tenant's data, not the environment:
- **What decides it:** a tenant flagged as synthetic-data (`IsDemo` today) may browse without selective criteria. A production deployment can host a demo tenant safely, and a test environment loaded with a copy of real data stays locked down.
- **What stays on in demo:** audit, counts, paging and the 200-row page cap still apply. Only the selective-criteria rule relaxes.
- **The flag must be true.** A demo tenant refuses inbound 834/837 files from production trading partners, so real members can't end up in a tenant that browses freely.
- **Curated data, not just open search.** Demo tenants ship with named personas (a member with a family and an open appeal, a provider group with a disputed payment, a delinquent group) whose IDs and names are in the demo script. Most demos then use the same lookups a CSR would.
- **Showing the guardrails.** A demo tenant can switch the rule back on (`Portal:Search:RequireCriteria = Always` per tenant) to show a compliance buyer what production behaves like.

### 3.6 Results

- **Rows are links** (`RecordLink`): click opens, Ctrl/⌘-click opens a new browser tab.
- **Disambiguating columns come first.** For people that is DOB, member ID, group and coverage status; two "Maria Garcia"s must be told apart without opening either.
- **Why it matched.** When the ID box matched an alternate identifier, the row says so ("matched Medicaid ID").
- **Server-side paging, sorting and counting** on every module:
  - a total count, exact up to 10,000, shown as "10,000+" beyond that;
  - column headers sort.
  - Today only Claims and Premium Invoices page on the server.
- **Columns are chosen per role,** and the user can change and keep their own.
- **Work lists add:**
  - select-all-matching (not just the page);
  - bulk actions where the module has them (reassign, adjust, void, send notice);
  - export to CSV, audited.

### 3.7 Keyboard and flow

- **Enter searches** from any field.
- **`/` focuses the ID box.** It already focuses the global box; on module pages it targets the module's ID box.
- **Arrow keys move through results;** Enter opens and Ctrl+Enter opens in a new tab.
- **Tab order follows the phone script:** ID, then last name, first name, DOB.
- **Clear empties the find bar but keeps the filters panel,** so a supervisor's work-list filters survive a quick lookup.

### 3.8 URLs, saved searches and PHI

`portal-navigation.md` §5.0 puts filter state in the URL so a list can be shared. That is right for non-PHI filters such as status, dates, LOB, codes and queue. It is wrong for a member's name, DOB or ID:
- URLs end up in browser history;
- they end up in proxy and server logs;
- they end up in chat messages.

**Rule:**
- **Non-PHI criteria go in the query string,** e.g. `/claims?status=pended&pend=P123&received=last30`.
- **PHI criteria go in a saved search handle,** e.g. `/claims?s=7Kq2…`. The portal stores the criteria server-side, per user, and the handle resolves only for users with that module's read permission in the same tenant. Unsaved handles expire after 24 hours.
- **Portal-to-service calls carry criteria in a POST body,** never a query string. Today Members, Providers and several finance pages send names and IDs as unescaped query parameters.

**Saved searches:**
- They are personal, or shared with a team (a work-management group).
- They can be pinned to Home, where they show a live count ("Pends over 30 days: 312").
- They can be scheduled as an export to a recipient with the same permission. The email carries a link, not data (`portal-navigation.md` §5.1 rule 4).

### 3.9 Audit

- **PHI searches record the records returned, not the terms.** This is the existing `[AuditClaimAccess]` / `[AuditRecordAccess]` pattern, and it extends to every module that returns member-level data:
  - premium billing for individual members;
  - payments;
  - AR member postings.
- **Finance searches over groups and providers are logged as list reads.** Today benefit-plan, premium-billing, AR, capitation, provider-contracts and sponsor searches are not audited at all.
- **Exports log the record IDs exported.**

### 3.10 Speed

- **Target:** a lookup returns in under 1 second at p95; a work-list first page in under 2 seconds.
- **Every criterion in this document has an index behind it** (§7.1), or is only allowed together with one that does.
- **A combination that would scan is refused** with the same "add a…" guidance as §3.5, not run slowly.

## 4. Records

### 4.1 Claims — `/claims`

**Who searches, and with what in hand**

| Who | Trigger | In hand | Needs |
|---|---|---|---|
| CSR / provider services | Provider calls: "where's my claim?" | Their patient account # (CLM01), their NPI/TIN, member name + DOB or ID, DOS, billed amount. Sometimes the payer claim # from a 277 or 835 | One claim, fast |
| CSR | Member calls about an EOB | Member ID from card; the EOB's claim # or DOS and provider name | One claim |
| Claims examiner | Working a pend: is this a duplicate? Is there an auth? | The claim they're on: member, provider, DOS, procedure codes | Similar claims for the same member, provider and DOS |
| Examiner / adjustment analyst | Provider dispute, corrected claim (frequency 7/8) | Original claim # or patient account #, DOS | The original and every version |
| Adjustment / reprocessing analyst | Fee schedule or config fix, retro eligibility | Provider TIN or contract, procedure or revenue codes, DOS range, paid date range | A work list to mass-adjust |
| Claims supervisor | Inventory, aging, prompt-pay risk | Queue, examiner, pend codes, received date | Work list, counts |
| Recovery / overpayment | Audit findings, COB discovered | TIN, member, paid date range, CARC | Work list |
| SIU | Tip or data pattern | Provider, procedure codes, amount ranges, place of service | Work list, export |
| EDI support | Trading partner says a file was sent | Trading partner, file/batch, received date, attachment control # (PWK) | Claims from that file |
| Compliance | Prompt-pay or timeliness audit | Received/clean/paid dates, LOB, state | Work list, export |

**ID box accepts:**
- CHO claim number;
- patient account number (CLM01, as the provider sent it);
- payer claim control number (the number on the 277/835 back to the provider);
- attachment control number (PWK);
- check/EFT trace number, which lists the claims on that payment.

**Find bar:** ID · Member (ID, or name + DOB) · Provider (NPI, TIN or name) · DOS range.

**More filters**

| Group | Criteria | Match |
|---|---|---|
| People | Subscriber ID; group/sponsor; rendering NPI; billing NPI; pay-to TIN; facility (for institutional) | Exact |
| Dates | Received; clean date; adjudicated; paid; check date | Range + presets |
| Status | Claim status; line status; pended / denied / paid / adjusted / voided; version: original or adjusted; latest version only (default on) | Any of |
| Why | Pend reason codes; edit codes (NCCI, MUE, custom); denial CARC / RARC; override applied | Any of |
| What | Claim type (professional, institutional, dental); bill type; place of service; procedure codes (CPT/HCPCS, single or range); revenue codes; modifiers; diagnosis (any position, or principal only); DRG | Any of; ranges for codes |
| Money | Billed; allowed; paid; patient responsibility; interest paid | Range |
| Plan | LOB; benefit plan; network status of provider (par/non-par) | Any of |
| Work | Assigned examiner; queue; age (days since received); prompt-pay due within N days | Exact / range |
| Source | Submission channel (EDI, portal, paper/OCR, API); trading partner; file or batch ID | Exact |
| Links | Has auth; auth #; has appeal; has attachment; has COB | Yes/No, exact |

**Results**
- Lookup columns: Claim # · Patient acct # · Member (name, DOB, ID) · Provider (name, NPI) · DOS · Billed · Paid · Status · Received.
- Work-list columns add: pend codes, age, assignee and paid date.
- Default sort: received date, newest first (lookup); age, oldest first (work list).

**Selective criteria:** an identifier, a member, a provider, an auth #, a file/batch, a queue/assignee, or a DOS or received range of 31 days or less.

**Bulk actions:** reassign; release to adjudication; send to adjustment (mass adjust); request records; export.

**Backend today → needed** (claims-service)
- **Exists:**
  - claim # (exact); auth # (header or line); pend and denial codes; received and adjudicated ranges; LOB;
  - member and NPI (billing or rendering), on GET only;
  - real total count on POST.
- **Fix:** POST ignores `MemberName`, `ProviderName`, `ClaimType`, `SortBy` and `SortOrder`. DOS filtering is containment, not overlap.
- **Add:**
  - patient account #, payer claim control # and TIN filters;
  - paid/check date; procedure, revenue and diagnosis codes; amount ranges;
  - assignee/queue (joined from work-management); trading partner/batch; latest-version-only.
- **Indexes to add:** tenant + billing/rendering NPI + DOS; tenant + PatientControlNumber; tenant + Status + ReceivedDate; tenant + PaidDate; multikey tenant + line procedure code + DOS.

### 4.2 Members — `/members`

**Who searches, and with what in hand**

| Who | Trigger | In hand | Needs |
|---|---|---|---|
| CSR | Member calls | ID card number (could be subscriber ID + suffix, Medicaid ID, MBI); or name + DOB; phone number (screen pop) | The member, then verify (DOB, address) |
| CSR / provider services | Provider calls to check eligibility | Member ID or name + DOB; DOS | The member, coverage on that DOS |
| Enrollment specialist | 834 rejected, employer calls about an employee | Subscriber ID, SSN, group number, employee name, effective date | Member and family; pending or termed spans |
| Enrollment specialist | Duplicate member suspected | Name, DOB, SSN, address | All near matches |
| Premium / billing | Individual policyholder calls about a bill | Subscriber name or ID, invoice # | The subscriber |
| UM / care management | Referral, admission notice | Name + DOB, Medicaid ID, facility | The member |
| Claims examiner | Claim came in with no member match | Name, DOB, ID as submitted, subscriber name | Candidates to link |
| Compliance / privacy | Records request, access audit | Member identity | The member, then Access log tab |

**ID box accepts:**
- CHO member ID;
- subscriber ID, with or without dependent suffix;
- card ID;
- state Medicaid ID;
- MBI;
- prior/legacy IDs (the `Identifiers` list, any type);
- full SSN (9 digits, only for roles with `members:ssn-search`; see §9).

**Find bar:** ID · Last name · First name · DOB · Sounds like ☐.

**More filters**

| Group | Criteria | Match |
|---|---|---|
| Identity | Middle name; gender; SSN last 4 (only with last name or DOB); phone; email; ZIP; city/state | Exact / starts with |
| Coverage | Coverage status as of a date (active, termed, future, pending); LOB; benefit plan; group/sponsor; effective or term date range | Any of / range |
| Household | Subscribers only; dependents of a subscriber ID; relationship | Exact |
| Care | PCP (NPI or name); PCP assignment missing; member alerts present; in case management | Exact / yes-no |
| Enrollment | Maintenance type/reason (834); last 834 date; enrollment source; draft/pending records | Any of / range |
| Other | Deceased; language; dual eligible; spend-down (Medicaid) | Yes-no / any of |

**Results**
- Columns: Name · DOB (with age) · Member ID · Subscriber ID / relationship · Group · Plan · Coverage status as of date · PCP.
- With "Sounds like" or an alternate ID, each row shows what matched.
- Default sort: last name, first name, DOB.

**Selective criteria:** an identifier; last name + (DOB or first-name prefix); DOB + first name; phone; or a group + coverage date range.
- Last name alone is refused on non-demo tenants. "Smith" in a 500,000-member plan is a browse.

**Bulk actions:** export (work lists only); assign PCP; order ID cards.

**Backend today → needed** (member-service, coverage-service)
- **Exists:** exact member ID; last-name prefix; exact DOB; group (GET); FHIR Patient search with given/family/name prefix, an identifier match ignoring spaces and hyphens, a DOB range and a real total.
- **Fix:**
  - `subscriberId` and `activeOnly` are accepted and ignored;
  - the "total" is the page size;
  - there is no sort;
  - the portal sends only one field.

  The FHIR Patient repository already does most of this right; the portal search should use it, or share its query builder.
- **Add:**
  - search over `Identifiers` (any type) by blind index (§7.2);
  - phonetic keys for last and first name; phone; ZIP;
  - coverage-as-of joined from coverage-service; PCP.

  coverage-service ignores `lineOfBusiness` and `asOfDate` today.
- **Indexes:** none exist on `Members`. Add tenant + LastNameKey + FirstNameKey + DOB; tenant + DOB; tenant + Identifiers.Hash; tenant + SubscriberMemberId; tenant + GroupNumber; tenant + Phone; tenant + LastNamePhonetic.

### 4.3 Providers — `/providers`

**Who searches, and with what in hand**

| Who | Trigger | In hand | Needs |
|---|---|---|---|
| Provider services / CSR | Provider office calls | NPI, TIN, office phone (screen pop), practice name | The provider and the right location |
| Claims examiner | Claim's billing/rendering provider didn't match | NPI, TIN, name, service address/ZIP from the claim | Candidate records and locations |
| Member services | Member needs a doctor (staff-side directory) | Specialty, ZIP + distance, language, gender, accepting new patients, network for member's plan | A list to read out |
| Network management / contracting | Recruiting, adequacy, roster updates | Specialty, county, network, contract status | Work list |
| Credentialing | Recredentialing cycle, license expiry, sanctions monitoring | Credentialing status, due dates, license # | Work list |
| Provider data management | Roster file from a group | Group TIN, NPIs on the roster | All practitioners in that group |
| SIU / compliance | Exclusion list (OIG/SAM) hit | NPI, name, TIN | The provider |

**ID box accepts:**
- NPI (individual or organization);
- TIN/EIN;
- CHO provider ID;
- state Medicaid provider ID;
- Medicare PTAN;
- license number.

**Find bar:** ID · Name (individual last/first, or organization) · Specialty · ZIP + radius.

**More filters**

| Group | Criteria | Match |
|---|---|---|
| Who | Provider type (practitioner, facility, group); taxonomy code; gender; languages; board certification | Any of |
| Where | Address/city/county/state; ZIP + radius; phone | Exact / starts with / distance |
| Network | Network; tier; plan; LOB; par/non-par; PCP and panel open/closed; accepting new patients; effective as of a date | Any of / as-of |
| Contract | Contract status; contract # ; payment methodology; contract ends within N days | Any of / range |
| Credentialing | Credentialing status; recredential due within N days; license expires within N days; sanctions/exclusion flag | Any of / range |
| Affiliation | Member of group (TIN or group name); hospital privileges at facility | Exact |
| Record status | Active / inactive / pending; changed since a date | Any of / range |

**Results**
- Columns: Name · NPI · TIN · Type/specialty · Location (city, ZIP; "+3 more" when several) · Network status for the selected plan · Accepting new patients · Credentialing status.
- Default sort: name. With ZIP + radius: distance.

**Selective criteria:** none. Provider data is not PHI, so browsing by specialty or network is allowed. Searches are still list-audited (§3.9).

**Bulk actions:** export (rosters, adequacy); start recredentialing; mark network change.

**Backend today → needed** (provider-service)
- **Exists:** name contains across first, last and organization; specialty; ZIP; state; plan/LOB participation; NPI exact lookup; FHIR Practitioner search.
- **Fix:**
  - search returns Active only, with no way to find inactive or pending providers;
  - name is an unanchored contains that can't use the name indexes;
  - no total count and no page size cap;
  - the portal sends the term unescaped in the query string.
- **Add:** TIN; alternate IDs; taxonomy; county; radius (needs geocoded location points + a 2dsphere index); credentialing and contract filters (joined); group membership; phone; status.
- **Indexes:** tenant + TIN; tenant + Locations.Geo (2dsphere); tenant + name tokens; tenant + taxonomy; tenant + Status.

### 4.4 Authorizations — `/authorizations`

**Who searches, and with what in hand**

| Who | Trigger | In hand | Needs |
|---|---|---|---|
| UM intake coordinator | Fax, portal or PAS (`Claim/$submit`) request arrives | Member, requesting provider, servicing provider/facility, CPT/HCPCS, diagnosis, requested dates, provider's own tracking # | Duplicates or an existing auth to extend |
| UM nurse / medical director | Working reviews | Decision due date, priority, assigned reviewer | Work list, ordered by clock |
| Concurrent review nurse | Daily inpatient census | Facility, admit date, expected discharge | All open inpatient stays at a facility |
| Claims examiner | Claim needs an auth | Member, servicing NPI, DOS, procedure codes | Auths that cover that claim |
| CSR / provider services | Provider calls: "is it approved?" | Auth #, their reference #, or member + provider + service | One auth and its status |
| Compliance (CMS-0057-F) | Timeliness reporting, audits | Received/decision dates, priority, decision | Work list, export |

**ID box accepts:**
- CHO auth number;
- provider/clearinghouse reference # (e.g. Availity trace);
- PAS request identifier.

**Find bar:** ID · Member · Requesting or servicing provider · Service dates (overlap).

**More filters**

| Group | Criteria | Match |
|---|---|---|
| Service | Service type (inpatient, outpatient, DME, home health, BH, pharmacy); procedure codes; diagnosis; place of service; units | Any of |
| Covers a claim | Member + servicing NPI + DOS + procedure: "auths that cover this claim" | Overlap; procedure any of |
| Clock | Priority (standard, expedited, retro); received date; decision due within N hours/days; overdue | Any of / range |
| Status | Status; decision (approved, partial, denied, pended, withdrawn); review type (prior, concurrent, retrospective); peer-to-peer requested | Any of |
| Work | Assigned reviewer; queue; review level (nurse, physician) | Exact |
| Utilization | Units used vs approved; expires within N days; no claim received yet | Range / yes-no |
| Inpatient | Facility; admit date; length of stay over N days; not yet discharged | Exact / range |
| Plan | LOB; plan; delegated vendor | Any of |

**Results**
- Columns: Auth # · Member (name, DOB, ID) · Service type and codes · Servicing provider · Service dates · Priority · Status/decision · Decision due (with clock color).
- Default sort: decision due, soonest first (work list); received, newest first (lookup).

**Selective criteria:** an identifier, a member, a provider, a facility, an assignee or queue, or a received or service range of 31 days or less.

**Bulk actions:** reassign; export; extend dates (where policy allows).

**Backend today → needed** (authorization-service)
- **Exists:** auth # exact; member exact; requesting or servicing NPI exact; status; LOB; date range. The portal's "Auth ID" field is filtered client-side as a contains match.
- **Fix:**
  - no indexes on member or NPI;
  - no page size cap and no total;
  - the end-date filter treats a null end date as matching.
- **Add:** provider reference #; procedure/diagnosis; priority; decision due; reviewer; review type; facility/admit; utilization; "covers claim".
- **Indexes:** tenant + MemberId + service start; tenant + ServicingNpi; tenant + RequestingNpi; tenant + Status + DecisionDue; tenant + AssignedReviewer.

### 4.5 Appeals and grievances — `/appeals`

Covered briefly because it shares the pattern. The case model is in `appeals-case-model.md`.

| Who | In hand | Needs |
|---|---|---|
| A&G coordinator | Case #, member, original claim/auth, received date | One case; cases by clock |
| CSR | Member or provider calling about "my appeal" | The case |
| Compliance | Level, timeliness, outcome | Work list, regulator export |

- **ID box accepts:** case #, original claim #, original auth #, external review/IRE #.
- **Filters:** kind (appeal, grievance); level; expedited; status; due within N; overdue; received range; category/reason; outcome; assignee; LOB; state fair hearing referred.
- **Backend gaps:** no kind filter on search; no total count.

## 5. Configuration

### 5.1 Benefit plans — `/benefit-plans`

**Who searches, and with what in hand**

| Who | Trigger | In hand | Needs |
|---|---|---|---|
| Configuration analyst | Plan year build, regulatory change, a sponsor's plan change | Plan ID or name, product, LOB, plan year, CMS contract-PBP or HIOS ID, state program code | The plan, its versions |
| Configuration analyst | "Which plans does this change touch?" | A benefit rule (see 5.2) | Plans |
| Sales / account management | Renewal | Sponsor/group | Plans that group offers |
| CSR | Member asks about their plan | (Comes from the member record, not a search) | — |

- **ID box accepts:** plan ID; CMS contract + PBP (H1234-001); HIOS plan ID; state program/plan code.
- **Find bar:** ID · Name (word prefix) · Plan year.
- **More filters:** LOB; product (HMO, PPO, EPO, POS, HDHP); metal level; plan year / effective as of; status (draft, active, retired); has unpublished changes; network; service area (state/county); sponsor/group using it; changed since a date / by whom.
- **Results:** Plan ID · Name · LOB · Product · Plan year · Status · Members enrolled · Groups · Last changed.
- **Selective criteria:** none (not PHI).

**Backend today → needed** (benefit-plan-service)
- **Exists:** payer, plan type and active filters, but the service loads the first 500 plans and filters in memory. No paging parameters.
- **Fix:** page and filter in the database; tenant-scoped indexes (today's two indexes lack the tenant).
- **Add:** external IDs; plan year; status; service area; network; sponsor (joined); name tokens.

### 5.2 Benefits — inside plans

Two jobs, which need two different tools.

**Benefit answer (CSR, provider services, UM intake)**
- **The question:** "Is this covered for this member, and what will it cost?"
- **In hand:** the member (so their plan and DOS), a service as a procedure code or a plain-language category ("physical therapy"), and the provider's network status.
- **Not a list search.** A panel on the member record and on the global box, which answers:
  - covered or not;
  - copay / coinsurance / deductible applies;
  - limits ("20 visits per year, 7 used");
  - PA required;
  - the benefit row it came from.
- It reads accumulators as of the DOS. It uses the existing benefits viewer (`benefits-viewer.md`) and declarative model, so the answer is the one adjudication would give.

**Benefit search (configuration analyst, compliance)**
- **The question:** "Which plans cover X / require PA for Y / have a $40 specialist copay?"
- **In hand:** a service, a cost-share value or a rule attribute.
- **Criteria:**
  - service category;
  - procedure code or range; diagnosis; place of service; network tier;
  - cost share (copay, coinsurance, deductible applies) by value or range;
  - PA required; limits present;
  - plan filters from 5.1;
  - effective as of a date.
- **Results** are benefit rows grouped by plan: Plan · Benefit · Tier · Cost share · Limit · PA · Effective. Each row opens the plan on that benefit.
- **Bulk action:** "change in all selected plans" goes through plan versioning (`plan-versioning.md`) as a draft, never a live edit.

**Backend today → needed:** only per-plan `GET {id}/benefits` exists. Add a cross-plan benefit query, and a benefit-answer endpoint (member + service + DOS + network status).

### 5.3 Sponsors / groups — `/sponsors`

| Who | In hand | Needs |
|---|---|---|
| Enrollment / account management | Group #, employer name, TIN, broker | The group |
| Premium billing | Group # from a remittance | The group and its account |
| Sales | Renewal month, broker, size | Work list |

- **ID box:** group number; TIN/EIN.
- **Filters:** name (word prefix); LOB; status; renewal/anniversary month; broker; state; size band; plan offered; billing method (list bill, self bill).
- **Backend:** today there is no name or group search, only an exact group lookup, and the portal filters everything client-side. Add name tokens, TIN, renewal month and indexes; the collection has none today.

## 6. Finance

### 6.1 Premium billing — `/premium-billing`

Premium billing covers groups (list bills) and individuals (ACA, Medicare Advantage premiums, COBRA). The jobs:

| Who | Trigger | In hand | Needs |
|---|---|---|---|
| Billing specialist | Group calls about an invoice | Group #, group name, invoice #, billing period | The invoice and its lines |
| Billing specialist | Individual policyholder calls | Subscriber name/ID, invoice # | Their invoices |
| Cash application | Remittance arrives | Check/EFT trace #, payer name, amount, invoice # on the stub | The invoice(s) to apply it to |
| Collections / delinquency | Grace period management (incl. ACA 3-month grace) | Days past due, grace end date, notice sent | Work list: who to notify, who to terminate |
| Billing supervisor | Billing run review | Run #, period, LOB | Invoices in a run, exceptions |
| EFT / treasury | ACH returns (R01, R02…) | Return code, trace #, settlement date, account last 4 | The draft and invoice |
| Finance close | Month-end | Period, status, LOB | Totals |

**ID box accepts:** invoice #; group #; subscriber/member ID; remittance check or EFT trace #; EFT draft trace #; billing run #.

**Find bar:** ID · Group or subscriber name · Billing period.

**More filters**

| Group | Criteria | Match |
|---|---|---|
| Invoice | Status (open, partially paid, paid, past due, delinquent, written off, voided); due date; billing period; balance; amount | Any of / range |
| Delinquency | Days past due; grace period ends within N days; in grace (ACA); notice sent / not sent; termination pending | Range / yes-no |
| Who | Billing entity type (group, individual); LOB; plan; broker; billing method | Any of |
| Payment | Remittance received date; payment method; unapplied amount; exceptions (short pay, overpay, unidentified) | Range / any of |
| EFT | Draft status; settlement date; ACH return code; NACHA file; held | Any of / range |
| Run | Billing run; created by | Exact |

**Results:** Invoice # · Bill-to (group or subscriber) · Period · Due · Amount · Paid · Balance · Days past due · Status.

**Selective criteria:** for individual (member-level) invoices, the PHI rule applies (an identifier, name, or 31-day range). Group invoices are browsable.

**Bulk actions:** send notices; write off (with approval); re-bill; export.

**Backend today → needed** (premium-billing-service)
- **Exists:** invoices by group # (exact), period range and status, paged but with no total.
- **Missing:** no delinquency search, no EFT search (the portal reads drafts one invoice at a time), no run filter on invoices, no indexes on invoices, no audit.
- **Add:** invoice #; subscriber; balance and days-past-due; grace fields; run ID; remittance and EFT searches; indexes; access audit for individual invoices.

### 6.2 Accounts receivable — `/finance/ar`

| Who | Trigger | In hand | Needs |
|---|---|---|---|
| Cash poster | Lockbox / bank file | Batch #, deposit date, check #, payer, amount | Unposted or unidentified receipts; where each goes |
| AR / collections | Aging review | Account, aging bucket, balance, LOB | Work list |
| Recovery | Provider overpayment receivable | Provider NPI/TIN, claim #, receivable # | The receivable and its offsets |
| Accountant | Month-end close, reconciliation | GL account # or name, period, journal/entry #, source system | Entries and balances |
| Auditor | Sample selection | Period, amount threshold, account | Work list, export |

- **ID box accepts:** GL account #; journal/entry #; cash posting or receipt #; check/trace #; lockbox batch #; receivable #; claim # (for recoveries).
- **Filters:**
  - account type; GL account (by # or name, word prefix); LOB; period;
  - posting date range; source (premium, claims, capitation, recovery, manual);
  - status (unposted, posted, reconciled, reversed); reconciled yes/no;
  - payer type; payer name; amount exact, ± tolerance, or range;
  - aging bucket; adjustment type; created by / approved by.
- **Amount search needs a tolerance.** "$1,234.50 ± $1.00" is how a cash poster finds a receipt keyed with a typo.
- **Results:** depend on the view (receipts, adjustments, balances, entries). Each view is one list with the same filter bar, not five separate pages with different controls as today.

**Backend today → needed** (ar-service)
- **Exists:** exact filters by type, status, period and account; keyset paging on GL entries.
- **Gaps:**
  - no account # or name search;
  - no amount search;
  - no totals;
  - the aging endpoint loads every balance;
  - the portal's AR Aging page never loads data (`ComputeAging` is a TODO);
  - no audit.
- **Add:** amount ± tolerance; check/trace; batch; account name; provider/receivable; indexes on amount and date.

### 6.3 Payments (disbursements) — `/payments`

| Who | Trigger | In hand | Needs |
|---|---|---|---|
| Provider services / CSR | Provider: "where's my payment?" | NPI/TIN, claim #, check/EFT trace # from their bank, amount, date | The payment, its status, its claims |
| Provider services | "We got a check we can't match" | Check #, amount, date | The 835 and claims on it |
| Disbursement / treasury | Payment run, positive pay, voids | Run #, payment date, method, bank account | Work list |
| Disbursement | Void / reissue / stop payment | Check #, payee | The payment |
| Treasury | Outstanding / stale-dated checks, escheatment | Issue date older than N days, uncleared | Work list by state |
| EDI support | 835 not received / rejected | Trading partner, ERA control #, run | The ERA and its payments |
| Tax | 1099 questions | TIN, tax year | Payments for that TIN |
| Member services | Member reimbursement | Member ID, claim # | Payment to member |

- **ID box accepts:** check #; EFT trace # (TRN02); payment ID; payment run #; ERA/835 control #; claim # (finds the payment that paid it).
- **Find bar:** ID · Payee (NPI, TIN or name; or member) · Payment date range · Amount.
- **More filters:**
  - payee type (provider, member, vendor);
  - method (check, EFT, virtual card);
  - status (pending, issued, cleared, voided, stopped, returned, stale-dated, escheated);
  - issue/clear dates;
  - outstanding over N days;
  - amount (exact ± tolerance, range);
  - run; LOB; bank account; trading partner;
  - has recoupment/offset; interest included.
- **Results:** Payment # / check or trace # · Payee (name, NPI/TIN) · Method · Amount · Issue date · Status · Claims count · Run.
- **Selective criteria:** member payees fall under the PHI rule. Provider payments are business data but carry claim (PHI) detail one click away, so searches are audited (they are today).
- **Bulk actions:** void/reissue (with approval); stop payment; escheat report; export.

**Backend today → needed** (payment-service)
- **Exists:** date range, payer and status search; separate lookups by check #, payee NPI and claim; audited.
- **Fix:** payee NPI, check # and claim are separate lookups rather than criteria that combine. No total, no page size cap, no query indexes.
- **Add:** TIN; trace #; amount ± tolerance; method; cleared/outstanding; run; ERA control #; indexes (tenant + CheckNumber, tenant + TraceNumber, tenant + PayeeNpi + PaymentDate, tenant + PayeeTin, tenant + Status + PaymentDate).
- **FFS payment runs:** `ffs-service` has no controllers; payment runs live in payment-service.

### 6.4 Capitation — `/capitation`

| Who | In hand | Needs |
|---|---|---|
| Capitation analyst | Provider/group NPI or TIN, contract, period | Statements, member rosters, retro adjustments |
| Provider services | PCP group calls: "why did our cap drop?" | TIN, period | Statement and its member-month changes |
| Finance | Run #, period, LOB | Run totals |

- **ID box:** statement #; run #; contract #; disbursement trace #.
- **Filters:** provider (NPI, TIN, name); contract; period; LOB; statement status; has retro adjustments; amount range; member count change over N%.
- **Backend:**
  - statements need npi + period, or status;
  - a period-only query returns 400;
  - no paging on statements;
  - contracts ignore `planId`.
- **Portal:** rows link to `/capitation/statements/{id}`, which no page serves.

### 6.5 Provider contracts — `/finance/contracts`

| Who | In hand | Needs |
|---|---|---|
| Contracting / network management | Contract #, provider/group name, TIN, NPI | The contract |
| Configuration analyst | "Which contracts use fee schedule X?" | Fee schedule, methodology | Contracts to update |
| Contract manager | Renewals | End date within N days | Work list |

- **ID box:** contract #; TIN; NPI.
- **Filters:** provider/group name (word prefix); LOB; network; payment methodology (FFS, % of Medicare, case rate, capitation, VBC); fee schedule; status; effective/end range; ends within N days; auto-renew.
- **Backend:** contract # lookup and exact NPI, LOB, status, methodology and network filters exist. Add TIN, name, fee schedule, end-date range and totals.

## 7. How it's built

### 7.1 One search contract per service

Each service exposes one `POST /api/v1/{resource}/search` with the same envelope:

```json
{
  "criteria": { "...": "module-specific, typed" },
  "sort": [{ "field": "receivedDate", "dir": "desc" }],
  "page": { "size": 50, "cursor": null },
  "count": "exact-to-10000"
}
```

The response:

```json
{
  "items": [],
  "total": 1234,
  "totalIsLowerBound": false,
  "nextCursor": "…",
  "matchedOn": { "<recordId>": "patientControlNumber" }
}
```

- **Criteria are a typed DTO per module,** not a bag of strings. An unknown or unsupported criterion is a 400, not silently dropped.
- **Sort fields are an allow-list per module,** each backed by an index.
- **Page size is capped at 200 for interactive use.** Exports run as a background job.
- **The selective-criteria rule (§3.5) is enforced in the service,** not only in the portal.
- **Existing endpoints stay** until the portal and other callers have moved.

### 7.2 Matching building blocks (shared library)

- **Normalized keys,** stored at write time:
  - `LastNameKey` / `FirstNameKey`: upper-cased, accents folded, punctuation removed;
  - `LastNamePhonetic`: Double Metaphone;
  - organization name tokens for word-prefix matching;
  - `IdKeys`: every identifier normalized.
- **Blind indexes for encrypted identifiers.** SSN, MBI and Medicaid ID are encrypted at rest (`MemberIdentifier.IsEncrypted`), so they can't be searched by value. Store a keyed HMAC of the normalized value (and of SSN last 4 + DOB) and search on the HMAC. The key lives in Key Vault with the field encryption keys.
- **No regex built from user input** except anchored prefix on a normalized key.
- **Backfill:** a hosted service computes keys for existing records, the same way index initializers run today.

### 7.3 Identifier registry

Each module registers its identifier forms:
- a pattern (e.g. 10 digits + Luhn check = NPI; the 11-character MBI layout, which excludes the letters S, L, O, I, B and Z);
- a normalizer;
- a resolver call.

The module ID box and the global box both use the registry. An input that fits several patterns (a 9-digit number could be a TIN, an SSN or a member ID) tries each, in parallel, with permission checks, and groups the results.

### 7.4 Portal components

| Component | Does |
|---|---|
| `SearchPage<TCriteria, TRow>` | Find bar + More filters + results; URL and handle state; keyboard handling |
| `IdBox` | Identifier input using the registry; "matched on" chip |
| `PersonFields` | Last / first / DOB / sounds-like, with DOB parsing |
| `ProviderPicker`, `MemberPicker` | Typeahead that resolves to an ID (already sketched in Claims' autocompletes) |
| `DateRangeField` | Range with presets and relative values |
| `CodePicker` | CPT/HCPCS/ICD/revenue/CARC with ranges, backed by terminology-service |
| `AmountField` | Exact, ± tolerance, range |
| `ResultsGrid` | `MudDataGrid` with `ServerData`, sortable headers, column chooser, select-all-matching, bulk action bar |
| `SavedSearches` | Save, share, pin to Home, schedule export |

## 8. Build order

Each step is a separate PR; nothing is removed until its replacement is live.

1. **Foundations:**
   - the search contract (§7.1);
   - the matching library and blind indexes (§7.2);
   - the identifier registry (§7.3);
   - `SearchPage`, `ResultsGrid`, `IdBox`, `PersonFields`, `DateRangeField`.
2. **Members.** Highest call volume, and the most broken today (single-field search, no indexes, no paging).
3. **Claims.** Patient account #, payer claim #, TIN, overlap DOS, codes, paid date, sort.
4. **Providers.** TIN, alternate IDs, status, radius.
5. **Payments.** "Where's my money" is one of the most common provider calls, and today it needs three separate lookups.
6. **Authorizations,** then **Appeals.**
7. **Saved searches,** Home tiles with counts, scheduled exports.
8. **Premium billing,** then **AR,** with access audit added to those services.
9. **Benefit plans,** benefit search and the benefit answer panel.
10. **Sponsors, capitation, provider contracts.**
11. **Bulk actions** per module, each behind its own permission.

## 9. Open questions

| # | Question | Recommendation |
|---|---|---|
| 1 | Should full SSN be searchable, and by whom? | Yes, exact 9 digits only, behind `members:ssn-search` (enrollment and privacy roles). Last 4 only combined with name or DOB. Never shown in results; the row says "matched SSN". |
| 2 | Phonetic name matching: stored Double Metaphone keys, or MongoDB Atlas Search? | Stored keys now. They work on any MongoDB, including the Cosmos for MongoDB deployments. Revisit Atlas Search if typo-tolerant search (edit distance) is wanted. |
| 3 | PHI criteria in saved-search handles rather than URLs (§3.8)? | Yes. It changes `portal-navigation.md` §5.0 only for PHI fields. |
| 4 | Replace "at least one criterion" with per-module selective criteria (§3.5)? | Yes, enforced in each service. Keep the `Portal:Search:RequireCriteria` modes, decided per tenant by its synthetic-data flag rather than per environment, and backed by curated demo personas (§3.5, Demo tenants). |
| 5 | Who may run wide work-list searches? | A `{module}:worklist` permission per module, granted to supervisor, finance and compliance roles. |
| 6 | Should the benefit answer panel be in v1 of benefits search, or later? | First. It serves the most users (every CSR call about coverage). The cross-plan configuration search follows. |
| 7 | Should staff-side provider directory search share the public directory API (`network-roster-api.md`)? | Share the query library, not the endpoint. Staff need inactive and pending providers and contract data the public API must not expose. |
