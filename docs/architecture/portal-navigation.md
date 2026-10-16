# Portal navigation: Home, modules and record pages

**Status:** proposed. Mark up the tab lists in §4 and the open questions in §8 before the build starts.

Health plan staff think in records. A call comes in and they say "pull up the member"; a provider disputes a payment and they say "pull up the claim". QNXT and VIDA Suite were both built around that habit. You land on a home page, open a module (Member, Claim, Authorization, ...), find the record, and work it from a set of tabs. CHO keeps that pattern and fixes what made it painful:

- windows stacked inside windows;
- no way to share or bookmark a record;
- losing your place on Back;
- tabs that load everything up front.

## 1. Where the portal is today

- **The left nav is grouped by function.** The groups are Operations, Members & Providers, Configuration, Finance, Monitoring, EDI Tools, Compliance and Admin. Records and run-the-plan pages are mixed together: Claims sits next to EDI Transactions and Batch Adjudication.
- **Claims is the only record with a page of its own** (`/claims/{id}`, with tabs Claim Details | Adjudication Pipeline | Benefit Breakdown).
- **Every other record opens in a dialog:**
  - `MemberDetailsDialog` has 15 tabs, including Coverage, Accumulators, PCP, Documents, ID Cards, Enrollment History, 834s, Family, Notes, Claims, AR and Premium.
  - `ProviderDetailsDialog` has 6 tabs, `AuthorizationDetailsDialog` 6 and `AppealDetailDialog` 3.
- **What a dialog costs:**
  - it has no URL, so it can't be bookmarked, shared or opened twice side by side;
  - Back closes it;
  - a claim listed inside a member opens yet another layer.

## 2. Principles

1. **Every record has a page, and every tab has a URL.**
   - Examples: `/members/{id}/claims`, `/claims/{id}/edits`, `/authorizations/{id}/reviews`.
   - Back, bookmarks, new browser tabs and "send me the link" all just work.
2. **One shell for every record.**
   - The layout is the same everywhere: header, alerts, tabs, then the tab body.
   - An examiner who knows the claim page already knows the member page.
3. **Related records link out; they don't nest.**
   - A claim on the member's Claims tab opens the claim page.
   - A breadcrumb leads back to the member, on the tab you came from.
4. **Tabs load when opened.** The header loads with the page; each tab fetches its own data the first time it is shown.
5. **Tabs follow permissions.**
   - A tab the user can't use is not shown. Examples: Access log needs `audit:read`; AR and Premium need finance reads.
   - The API still enforces every read, so hiding a tab is a convenience, not the control.
6. **Shared tabs are one component.** Notes, Documents, History, Calls and Access log work the same on every record they appear on, backed by one service each:
   - the notes services;
   - the document store (`document-store.md`);
   - the access log (#1365).
7. **Every record read is audited,** as claims are today. That extends to member, provider and authorization pages (§6).

## 3. The shape

```
Home                      landing: My Work / Get Next, pinned + recent records, search
├─ Search (global)        one box: member ID, claim #, auth #, NPI, check/EFT #, appeal #, call #
├─ Records
│  ├─ Members             list + search  →  /members/{id}/{tab}
│  ├─ Providers           list + search  →  /providers/{id}/{tab}
│  ├─ Claims              list + search  →  /claims/{id}/{tab}
│  ├─ Authorizations      list + search  →  /authorizations/{id}/{tab}
│  ├─ Appeals & Grievances               →  /appeals/{id}/{tab}
│  ├─ Service (calls)                    →  /calls/{id}/{tab}
│  └─ Payments                           →  /payments/{id}/{tab}
├─ Work                   My Work, Work Queues
├─ Operations             Batch Adjudication, EDI Transactions, EDI Operations, Enrollment Ops,
│                         Payment Runs, Capitation Runs, Workflows, Reports
├─ Finance                AR, Premium Billing, Provider Contracts (financial)
├─ Configuration          Benefit Plans, Sponsors/Groups, Trading Partners, Reference Data,
│                         Terminology, Pricing API, Work Management
├─ Compliance             CMS-0057-F, PA rules, appeals/grievance timeliness, access log search
└─ Admin                  Users, Settings, Correspondence, Platform (CHO staff only)
```

### 3.1 Home

Home replaces the current Dashboard as the landing page. It shows:

- **Get Next** and the user's queue counts (the existing `GetNextCard`).
- **Recent records:** the last 10 records the user opened, across all modules. They are kept per user on the server, so they follow the user to any device.
- **Pinned records:** what the user is following, such as an open grievance or a member in case management.
- **Role tiles:** a supervisor sees queue aging and SLA risk, compliance sees appeals near their deadline, and finance sees the next payment run.

### 3.2 Global search

The top bar holds a single search box on every page.

- **Exact identifiers jump straight to the record:** a claim number, member ID, auth number, NPI, check or EFT trace, appeal number or call number.
- **Anything else** (a name, or a name plus date of birth) shows grouped results: Members, Providers, Claims.
- **Keyboard:** `/` focuses the box.
- **Access log:** a search by member is recorded the same way claim searches are now. No search terms are stored, only the records returned.

## 4. The record shell and tab lists

**Shell layout:**
- **Header (sticky):**
  - record type and ID, name or summary, and status chips;
  - key dates;
  - key relationships as links: a claim's member and provider, an auth's member and requesting provider.
- **Alert strip:** member alerts (`MemberAlertBanner`), legal hold, integrity flags, open appeals, and SLA clocks.
- **Actions:** what this user can do to this record in its current state, such as Finish review, Request records or New note.
- **Tabs:** an overflow menu appears once there are more than about 8 tabs.

Tabs marked † are shared components (principle 6). The **Exists** column says where the content lives today.

### Member — `/members/{id}`

| Tab | Shows | Exists |
|---|---|---|
| Overview | demographics, current coverage, PCP, alerts, open items | dialog: Personal Info, Coverage & Benefits |
| Enrollment | spans, history, 834s | dialog: Enrollment History, 834 Transactions |
| Coverage & Benefits | plan, benefits, accumulators | dialog: Coverage, Benefits, Accumulators |
| PCP | current and history; change PCP | dialog |
| Family | subscriber and dependents, as links | dialog |
| Claims | the member's claims, linking to claim pages | dialog |
| Authorizations | the member's auths | new tab (data exists) |
| Appeals & Grievances | the member's cases | new tab (data exists) |
| ID Cards | cards issued and reorder | dialog |
| Premium / AR | billing and balance (finance permission) | dialog |
| Calls † | service interactions | new (CHO Service) |
| Notes † | | dialog |
| Documents † | | dialog |
| Access log † | who viewed this member's records | new (`audit:read`) |

### Provider — `/providers/{id}`

| Tab | Shows | Exists |
|---|---|---|
| Overview | NPI, TIN, taxonomy, status | dialog |
| Locations | | dialog |
| Networks & Affiliations | networks, groups, PCP panel | dialog: Networks |
| Credentials | credentialing and verification | dialog + verification page |
| Contracts | contract terms, fee schedules | dialog: Contract |
| Claims | the provider's claims | new tab |
| Payments | checks, EFTs and 835s for this payee | new tab |
| Authorizations | auths requested or servicing | new tab |
| Performance | | dialog |
| Calls †, Notes †, Documents † | | new |

### Claim — `/claims/{id}`

| Tab | Shows | Exists |
|---|---|---|
| Summary | header, money row, status, pend reasons | page: Claim Details |
| Lines | lines, pricing, line disposition | page: Claim Details |
| Edits | edits to decide, overrides, line denials | page: Claim Details panels |
| Adjudication | pipeline trace and benefit breakdown | page: two tabs, merged |
| Payment | remit, check/EFT, interest, clean date | new tab |
| History | versions, adjustments, audit timeline | partly on page |
| Related | duplicates, adjustments, appeals, auths | partly on page |
| Notes † | | page panel |
| Documents † | attachments, 275s, RFAI responses | new |
| Access log † | | new (`audit:read`) |

### Authorization — `/authorizations/{id}`

The tabs are Overview | Services | Clinical Review | Decision & Letters | Related claims | Notes † | Documents † | Access log †.

This takes over the dialog's General, Service Details, Review and Attachments tabs. The dialog's Member and Provider tabs become header links.

### Appeal / grievance — `/appeals/{id}`

The tabs are Overview (level, clocks, status) | Original decision | Review | Decision & Letters | Timeline | Notes † | Documents †.

The levels and clocks come from the rule packs in `appeals-grievances-compliance.md`.

### Payment — `/payments/{id}`

The tabs are Summary (check/EFT, payee, status) | Claims paid | 835 | Adjustments & recoupments | Documents †.

### Call — `/calls/{id}`

The tabs are Summary | Linked records | Follow-ups | Notes † | Documents †. The full design is in `cho-service.md`.

## 5. Lists (module landing)

Each module opens on a searchable list. Its parts:

- **Filters:** saved per user.
- **Columns:** chosen per role.
- **Opening a row:** a row opens the record page; Ctrl/⌘-click or the middle button opens it in a new browser tab, because rows are real links.
- **Filter state lives in the URL,** so "members in Dallas County with an open grievance" is a link someone can send.

## 6. Audit and security

- **Every record page read is logged.** Member, provider, authorization and appeal pages record to an access log the same way claims do (#1365): one append-only store per service, the same event shape, and the same `audit:read` gate.
- **The member Access log tab is the HIPAA accounting view.** It combines the member's own record reads with reads of their claims.
- **Tab visibility uses the CHO permission set** (`PermissionGate`). The server-side checks on each tab's API stay the control.
- **Recent and pinned records hold IDs only.** Display names are fetched (and logged) when shown.

## 7. Build order

1. **The shell** (`RecordShell`, `RecordHeader`, `RecordTabs`):
   - tab routing (`/{module}/{id}/{tab?}`) and lazy tab bodies;
   - a breadcrumb that remembers the tab you came from.
   - Claims moves onto it first, since most of its tabs exist; this gives it Payment, Documents and Access log.
2. **Member page.** Lift the dialog's tabs into the shell, one tab component at a time. The dialog becomes a thin "quick view" that links to "Open member" during the transition, then goes away.
3. **Home and global search.**
4. **The left nav regrouped** as in §3, with old routes kept as redirects.
5. **Provider, Authorization, Appeal, then Payment pages.**
6. **Calls,** with CHO Service.

Each step is a separate PR. Nothing is removed until its replacement is live.

## 8. Decisions for Mark

| # | Question | Recommendation |
|---|---|---|
| 1 | Should Home replace Dashboard as the landing page, with the dashboard metrics moving to role tiles? | Yes. |
| 2 | Should the member dialog stay as a quick-view peek, for example from a claim header? | Keep a small peek (overview and alerts only), with "Open member". |
| 3 | Should tab order and visibility be configurable per tenant? | Not at first. Use a fixed order plus permission-based hiding, and revisit if a plan asks. |
| 4 | Should the claim's Adjudication Pipeline and Benefit Breakdown tabs merge into one Adjudication tab? | Yes. |
| 5 | Should the top bar get a record-tab strip, i.e. several open records as tabs inside the app, like QNXT's open windows? | No. Browser tabs plus deep links do this better; Recent covers going back. |
