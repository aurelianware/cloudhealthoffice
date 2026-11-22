# Texas Medicaid demo, on demand

A Texas Medicaid shaped demo of Cloud Health Office that is off (and nearly
free) until someone needs it. One command, or one button in GitHub, turns it
on; the same turns it off. It shows three things a Texas Medicaid managed care
plan cares about:

1. **The daily file drop.** The plan's core sends CPCDS extracts (members,
   coverage, medical and pharmacy claims, UM authorizations). CHO loads them
   without anyone calling an API ([PR #1860](https://github.com/aurelianware/cloudhealthoffice/pull/1860),
   [#1867](https://github.com/aurelianware/cloudhealthoffice/pull/1867)).
2. **The CMS-0057-F APIs served from that drop.** Patient Access and Provider
   Access read the loaded claims as CARIN Blue Button ExplanationOfBenefits;
   the Prior Authorization API reads the loaded authorizations.
3. **Shadow adjudication.** CHO re-adjudicates every loaded claim and compares
   its answer with the core's, so the plan sees where its core paid more or
   less than its own fee schedule says.
4. **The Texas prior-authorization clocks.** Requests run on Tex. Gov't Code
   ch. 540 deadlines (1 hour, 72 hours, 1 business day, 3 business days), the
   earlier of the Texas and federal clock wins ([PR #1863](https://github.com/aurelianware/cloudhealthoffice/pull/1863)).

> **Synthetic data only.** The plan is "Sample Texas Community Health Plan
> (synthetic)". Every member, NPI, address and amount is invented; the NPIs
> sit below the range NPPES issues from, so none belongs to a real provider.
> The fee schedule is not TMHP's. The cluster trusts the public development
> signing key, so it is reached by port-forward or behind Cloudflare Access,
> never openly.

## What it costs

The demo reuses the GKE demo cluster from [GCP-DEMO.md](GCP-DEMO.md). "Off"
means the node pool has no nodes.

| State | What is billed | Roughly |
|---|---|---|
| Off | The volumes that hold the data (MongoDB, Kafka, Postgres, about 70 to 90 GB of standard disk) and the stored images. Google's free tier credits the management fee of one zonal cluster. | $5 to $10 a month |
| On | Plus one e2-standard-8 node and its boot disk | about $0.30 an hour |
| A 2-hour showing | | under $1 |

These are list-price estimates, not a bill; the budget alert in
`infrastructure/gcp` is the check. For comparison, the cluster left on all
month is about $210. A nightly job turns the demo off at 2 AM Central in case
someone forgets.

Nothing here adds infrastructure. It does not run `terraform apply`, and it
adds no bucket, load balancer or static IP.

## Turn it on and off

From GitHub (no tools needed): **Actions → Texas Medicaid demo (on / off) →
Run workflow**, then pick:

| Action | Does |
|---|---|
| `up+seed` | Adds the node, waits for the services (about 10 minutes from off), applies the Texas settings, loads the demo |
| `up` | The same without loading data |
| `seed` | Loads, or reloads, the demo on a running cluster |
| `status` | Nodes, pods and the running cost |
| `down` | Removes the node. The data stays for next time |

From a laptop with `gcloud` and `kubectl`:

```bash
export GCP_PROJECT_ID=...                 # the demo project
./scripts/demo/texas/demo.sh up           # ~10 minutes from off
./scripts/demo/texas/demo.sh connect      # leave running in its own terminal
./scripts/demo/texas/demo.sh seed
# ... the demo ...
./scripts/demo/texas/demo.sh down
```

`connect` opens the portal at http://localhost:8080 and the APIs on 5001
(claims), 5002 (benefit plans), 5004 (providers), 5006 (authorizations) and
5023 (FHIR). If the Cloudflare Tunnel is set up
([GCP-PUBLIC-HOSTING-PLAN.md](GCP-PUBLIC-HOSTING-PLAN.md)), the portal is also
at the Access-protected hostname and the audience needs nothing installed.

### The first time

The images in Artifact Registry predate the file drop, CPCDS and Texas clock
work (merged 2026-11-21), so build once before the first showing: **Actions →
Deploy demo to GKE → Run workflow** with the image tag blank. It builds every
image (20 to 40 minutes) and deploys. Then run `up+seed`. Automatic deploys
stay paused (`GKE_AUTO_DEPLOY=false`), so the demo does not change under you.

A deploy resets the deployments to the shared manifests, which drops the
Texas settings. `demo.sh up` and `seed` put them back (`demo.sh overlay` on
its own).

## What `up` and `seed` change

`up` resizes the node pool and then applies these settings with
`kubectl patch`. They are demo-only and live in the Deployments, not in the
shared manifests:

| Service | Setting | Why |
|---|---|---|
| claims-service | `CpcdsDrop__Enabled=true`, `Store=Folder`, `Root=/drops` on an `emptyDir` | The daily drop, read from a folder in the pod |
| claims-service | `Interval=00:00:30`, `QuietPeriod=00:00:15` | A drop loads within a minute of the copy |
| claims-service | `Adjudication__Shadow__Enabled=true` | Shadow adjudication after each claim file |
| claims-service | `Services__EnrollmentImportService`, `ProviderService`, `DocumentService` | The drop's upstream imports, at their in-cluster addresses |
| authorization-service | `PriorAuthDeadlines__TexasMedicaid__Tenants__0=demo`, `TimeZone=America/Chicago`, `Holidays__*` | The Texas clock for the demo tenant. The holiday list is illustrative: a plan's compliance team supplies the real one |
| benefit-plan-service | `ServiceCategoryMapping__AdminWriteEnabled=true` | Lets setup save the standard service categories |

`seed` runs, against the `demo` tenant:

1. `tx_demo.py setup`: three benefit plans (STAR, STAR Kids, CHIP; $0 cost
   sharing), a Medicaid network with six credentialed providers, the core plan
   code mappings (`STAR-DAL`, `STARKIDS-DAL`, `CHIP-DAL` in group
   `HHSC-DALLAS-SYN`), a professional fee schedule, and the tenant's drop
   (Medicaid, shadow after import) and shadow settings. Idempotent.
2. `demo.sh drop`: generates today's drop (120 members, about 180 medical and
   40 pharmacy claims paid over the last 30 days, 24 decided authorizations),
   copies it into `/drops/demo/inbound` and loads it. The files are the same
   for a given date, so a rehearsal and the showing load the same data. A
   later day's drop repeats most claims, as a core's rolling extract does, and
   CHO reports them unchanged.
3. `tx_demo.py seed-pa`: seven open Medicaid prior authorizations, one per
   Texas clock, and prints each one's Texas and federal deadline next to the
   deadline CHO stored, with a ✓ when they agree.

The generator builds in one finding for shadow adjudication: the core still
pays therapeutic exercise (97110) at last year's rate, $23.45 a unit against
the loaded $21.30, so CHO reports "core paid more" on those claims. ER
facility claims have no facility contract in the demo, so CHO lists them under
"what to load next", which is what a plan's first real run looks like.

## Click-through script (15 minutes)

Rehearse once per showing, the day before, with `up+seed`, and note the
numbers the drop and shadow run show; they are the same on the day. Times in
the portal are UTC; `seed-pa` prints them in Central time.

| Min | Segment | Show | Where |
|---|---|---|---|
| 0–1 | Frame | A Texas Medicaid plan keeps its core. CHO takes the core's nightly CPCDS extract and gives the plan CMS-0057-F APIs, a second opinion on every claim, and Texas PA clocks. Everything on screen is synthetic. | — |
| 1–4 | The daily drop | Today's run: five files, members and coverage loaded first, then claims, pharmacy and authorizations, with counts and nothing refused. The freshness report shows the drop is current. Run `demo.sh drop` live to show a second day: almost every claim comes back unchanged. | Portal → EDI Transactions → **Daily drop** |
| 4–7 | Patient Access from the drop | A member's claims as CARIN Blue Button EOBs, a pharmacy EOB with days supply and NDC, the coverage, and the synthetic data label on every response. Same records feed Provider Access. | `python3 scripts/demo/texas/tx_demo.py fhir --member TXM7100001`; Portal → **APIs** |
| 7–10 | Shadow adjudication | The run that followed the claim file: how many claims CHO decided, the match rate, the 97110 rate finding in dollars (core paid more), the ER claims under "what to load next", and the discrepancy CSV a claims team would work from. CHO changes nothing in the core. | Portal → **Shadow Adjudication** |
| 10–14 | Texas PA clocks | Open the poststabilization request: due one hour after receipt, labeled with §540.0302(b)(3). Then the outpatient MRI (3 business days against the federal 7 days), an inferred one (no scenario stated at intake), and the overdue one the console flags while the federal clock still has days left. | Portal → **Compliance → CMS-0057-F Console**; the links `seed-pa` prints |
| 14–15 | What it takes | The plan sends files it can already produce. No core connector, no change to the core. Production runs under a BAA in the hosted environment ([GCP-HOSTED-HIPAA.md](GCP-HOSTED-HIPAA.md)). | — |

Talking points to keep straight:

- In a holiday week the federal clock can be the earlier one: three Texas
  business days from a weekend request skip Thanksgiving and the day after,
  past the federal 7 days. CHO keeps the earlier deadline, and `seed-pa` says
  so on those requests. Show it: it is the "earlier clock wins" rule.
- Texas Medicaid dental is carved out to dental maintenance organizations, so
  the demo has no dental claims.
- The Texas clocks apply to requests CHO receives (PAS `$submit`, the portal).
  Authorizations loaded from the plan's UM platform are read-only history in
  CHO; their clocks are the UM platform's.
- The incomplete-documentation process (§540.0303(b)(2)) and contract or UMCM
  timeframes shorter than statute are plan policy inputs, not modeled.
- Not a TMHP, HHSC or CMS certification, and not live payer data.

## When something is off

| Symptom | Check |
|---|---|
| `seed-pa` shows ✗ on every request | `kubectl get deploy authorization-service -n cloudhealthoffice -o yaml \| grep TexasMedicaid`. Run `demo.sh overlay`. An image older than PR #1863 has no Texas clock: deploy first |
| Drop run says nothing is configured | `tx_demo.py setup` saves the drop settings; `demo.sh overlay` turns the drop on for the deployment |
| Members load but coverage rows are refused as unmapped | The plan code mappings did not save: rerun `tx_demo.py setup` and read step 4 |
| Shadow run holds claims for provider screening | Expected: synthetic NPIs cannot be screened against NPPES. CHO still compares the rest of its answer |
| A fresh pod has an empty drop folder | The folder is an `emptyDir`; loaded data is in MongoDB. Rerun `demo.sh drop` to show a run live |
| Pods pending after `up` | `kubectl describe pod`: the node may still be joining. `demo.sh status` again in a minute |
