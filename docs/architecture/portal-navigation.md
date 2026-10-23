# Portal navigation: Home, modules and record pages

**Status:** approved 2026-10-15. The decisions are recorded in §8.

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
3. **Related records link out; they don't nest.** A related record (for example the member on a claim) can be reached three ways:
   - **Peek:** clicking the name in a record header opens a side panel without leaving the page. For a member it shows eligibility on the date of service, PCP, alerts and accumulators. The panel has **Open** (same browser tab) and **Open in new tab** buttons.
   - **Open:** a normal click moves to the record in the same browser tab. Back, or the breadcrumb, returns to where you came from, on the tab you were on.
   - **New browser tab:** every record link is a real URL, so Ctrl/⌘-click and middle-click open it in a new browser tab. A small ↗ icon appears beside record links on hover or keyboard focus, for people who don't know the shortcut. The record header always shows an "Open in new tab" button.
   - There are no windows inside the app, so several open records are just several browser tabs.
4. **Tabs load when opened.** The header loads with the page; each tab fetches its own data the first time it is shown.
5. **Tabs follow permissions.**
   - A tab the user can't use is not shown. Examples: Access log needs `audit:read`; AR and Premium need finance reads.
   - The API still enforces every read, so hiding a tab is a convenience, not the control.
6. **Shared tabs are one component.** Notes, Documents, History, Calls and Access log work the same on every record they appear on, backed by one service each:
   - the notes services;
   - the document store (`document-store.md`) — `DocumentsPanel`, built 2026-10-23 on claim, authorization, appeal and provider;
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

Home replaces the current Dashboard as the landing page. It answers one question for whoever signs in: what do I need to do now? Everything on it opens a record or a filtered list. Charts and trends live in each module's Insights view (§5.1).

**For everyone, at the top:**
- **Search:** the global search box (§3.2), large.
- **Get Next:** the user's queue counts by work type (claim pends, appeals, auths, call follow-ups), showing only the queues they belong to. This is the existing `GetNextCard`.
- **Due today and overdue:** the user's assigned items, sorted by deadline clock rather than arrival date. An expedited appeal due in 6 hours sorts above last week's claim pend.
- **Recent records:** the last 10 records the user opened, across all modules. They are kept per user on the server, so they follow the user to any device.
- **Pinned records:** what the user is following, such as an open grievance or a member in case management.
- **Plan notices:** bulletins posted by admins, such as "new fee schedule effective 11/1" or "system maintenance Saturday".
  - Each notice has a start and an expiry date, can be targeted to roles, and can be marked as read.
  - Posting takes a `notices:write` permission; TenantAdmin has it by default.

**Below that, role tiles** (a user sees the tiles their roles allow):

| Role | Tiles |
|---|---|
| Claims examiner | my pends by age; finished today; claims waiting on records I requested |
| Supervisor | inventory and aging by queue; unassigned items; items at deadline risk; throughput by person (reassign from here) |
| Customer service | call follow-ups due; my recent calls |
| Appeals & grievances | cases by deadline (48 hours, overdue); acknowledgments not sent; cases waiting on a member or provider |
| Utilization management | pending auths by decision deadline, expedited first |
| Compliance | timeliness against state and CMS standards; prompt-pay interest this month; unusual access-log activity (e.g. one user opening hundreds of member records) |
| Finance | next payment run and total; held payments; cash posting exceptions |
| Operations / IT | last batch adjudication run; EDI rejects; failed transactions |

The current Dashboard's claim metrics and batch-run panel move into the supervisor and operations tiles and the Claims Insights view.

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
| Documents † | attachments, 275s, RFAI responses | built 2026-10-23 |
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

Step 1 of `cho-service.md` §8 shipped 2026-10-22 as one page rather than tabs,
which is what fits a screen a rep works while someone is on the line: who is
calling, what they are asking about, what was discussed, and the wrap-up, with
the history beside it. The module lands at `/service`. Follow-ups appear when
step 3 puts them in work management.

## 5. Module landing: List and Insights

Each module opens on two views, **List** and **Insights**, at `/{module}` and `/{module}/insights`.

### 5.0 List


Each module opens on a searchable list. Its parts:

- **Filters:** saved per user.
- **Columns:** chosen per role.
- **Opening a row:** a row opens the record page; Ctrl/⌘-click or the middle button opens it in a new browser tab, because rows are real links.
- **Filter state lives in the URL,** so "members in Dallas County with an open grievance" is a link someone can send.

### 5.1 Insights

Each module carries its own reporting, so people working in Claims don't have to go somewhere else to see how claims are doing.

| Module | Insights |
|---|---|
| Claims | inventory and aging; auto-adjudication rate; pend reasons; denial reasons (CARCs) and top edits; receipt-to-payment turnaround; prompt-pay interest; first-pass rate by provider |
| Members | enrollment trend, adds and terms; PCP assignment gaps; ID cards outstanding; call reasons |
| Providers | network size and changes; credentialing due; top billers; denial rate by provider; records requests outstanding |
| Authorizations | volume; approval and denial rates; decision timeliness (standard vs expedited); auths with no claim |
| Appeals & Grievances | timeliness by level; overturn rate; top reasons; cases sent to the state |
| Service | call volume and reasons; first-call resolution; follow-ups overdue |
| Payments / Finance | paid by period; recoupments; held payments; cash posting exceptions |

**Rules:**
1. **Every number drills down.** Clicking a number opens the module's List with that filter, as a shareable URL. Example: "312 claims pended over 30 days".
2. **One definition per metric.** A metric is defined once, server-side, and used by Insights, Home tiles and compliance exports alike. The appeals timeliness shown on screen is the same figure as the regulator export.
3. **Permission and scope.** Insights for a module need that module's read permission. Counts respect tenant and line-of-business scope. Insights shows aggregates only; member-level detail appears only on drill-down, which is an audited list read.
4. **Export and schedule.** Any view exports to CSV and can be scheduled to email a recipient with that permission. The email carries a link, not PHI.
5. **Cross-domain reporting lives in Reports** (Operations). This covers executive views, medical cost trend, state quarterly reports and CMS timeliness universes, built on the same metric definitions.

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
3. **Home (search, Get Next, due items, recent, pinned, plan notices, role tiles) and global search.**
4. **The left nav regrouped** as in §3, with old routes kept as redirects.
5. **Provider, Authorization, Appeal, then Payment pages.**
   - Each module's Insights view ships with or after its page; Claims Insights comes first, built from today's Dashboard.
6. **Calls,** with CHO Service.

Each step is a separate PR. Nothing is removed until its replacement is live.

## 8. Decisions

| # | Question | Recommendation |
|---|---|---|
| 1 | Should Home replace Dashboard as the landing page, with the dashboard metrics moving to role tiles? | **Agreed.** Plan notices included. |
| 2 | Should the member dialog stay as a quick-view peek, for example from a claim header? | **Agreed:** a peek panel with Open and Open in new tab (principle 3). |
| 3 | Should tab order and visibility be configurable per tenant? | **Agreed:** fixed order plus permission-based hiding for now; revisit if a plan asks. |
| 4 | Should the claim's Adjudication Pipeline and Benefit Breakdown tabs merge into one Adjudication tab? | **Agreed.** |
| 5 | Should the top bar get a record-tab strip, i.e. several open records as tabs inside the app, like QNXT's open windows? | **Agreed:** no in-app tab strip. Use browser tabs, deep links and the ↗ icon; Recent covers going back. |
| 6 | Should each module carry its own Insights view? | **Agreed** (§5.1). |
