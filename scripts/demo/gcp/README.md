# Cloud Health Office demo on GKE

Scripts for running the demo against the GKE environment from
`infrastructure/gcp` (Terraform) and `.github/workflows/deploy-gcp-gke.yml`.
The full runbook, including the 25-minute walkthrough and teardown, is
[docs/deploy/GCP-DEMO.md](../../../docs/deploy/GCP-DEMO.md). To give a prospect their own login and record the walkthrough video, see [docs/deploy/PROSPECT-DEMO-ACCESS.md](../../../docs/deploy/PROSPECT-DEMO-ACCESS.md).

All data is synthetic. The cluster has no public ingress and trusts the
public development signing key, so it is reached only through port-forward.

| Script | What it does |
|---|---|
| `port-forward.sh` | Opens the portal (8080), claims-service (5001), benefit-plan-service (5002), fhir-service (5023) and authorization-service (5006) on localhost |
| `cho_demo.py smoke` | End-to-end checks: health, token enforcement, adapter labels, portal. Exits non-zero on failure |
| `reset-demo.sh` | Clears the demo tenant's claims activity (claims, adjudication runs, accumulators, payments, work items, appeals, RFAI cases) so a fresh `run-mcc.sh` starts clean. Keeps members, plans, providers, rules and the work-queue setup. Dry run unless `--yes` |
| `run-mcc.sh` | Demo-sized Million Claim Challenge (default 2,000 claims). Results show in the portal's Batch Adjudication page |
| `cho_demo.py claims` | Narrates a paid claim, then a pended claim with the AI examiner's advisory recommendation and its unchanged status |
| `cho_demo.py seed-auths` | Loads a CMS-0057-F prior-authorization scenario (open requests at every point of the 72-hour and 7-day clocks, recent reviewer and automated decisions, a year of history) for the portal's **Compliance → CMS-0057-F Console**. Re-running refreshes it |
| `cho_demo.py seed-story` | Loads the five prior authorizations the portal's `/demo` guided tour links to: an automated approval, a partial approval with a reason per line, a denial, a request extended once with its member notice, and an open expedited request. Re-running resets their clocks to now |
| `cho_demo.py seed-dental` | Loads two Medicaid dental prior authorizations: a crown on tooth 19 sent with a radiograph and approved, and adolescent braces denied because the malocclusion score is below the program's threshold. For dental plan prospects; synthetic data only |
| `cho_demo.py adapter-status` | Shows which FHIR resources are demo, hybrid or live, and the response headers |
| `cho_demo.py token` | Prints a development token for the demo tenant (for `scripts/demo/cms-0057-f-demo.sh` as `FHIR_TOKEN`) |

Typical sequence, after the deploy workflow has run:

```bash
gcloud container clusters get-credentials cho-demo --location us-west1-a --project YOUR_PROJECT_ID   # us-west1 for Autopilot
./scripts/demo/gcp/port-forward.sh          # leave running in its own terminal
python3 scripts/demo/gcp/cho_demo.py smoke
./scripts/demo/gcp/reset-demo.sh --yes     # optional: clear earlier claims first
GAR_REPOSITORY=... IMAGE_TAG=... ./scripts/demo/gcp/run-mcc.sh
python3 scripts/demo/gcp/cho_demo.py claims
python3 scripts/demo/gcp/cho_demo.py seed-auths   # CMS-0057-F console history
python3 scripts/demo/gcp/cho_demo.py seed-story   # the /demo guided tour's records
python3 scripts/demo/gcp/cho_demo.py seed-dental  # two dental prior auths for dental plan prospects
FHIR_URL=http://localhost:5023 TENANT_ID=demo FHIR_TOKEN="$(python3 scripts/demo/gcp/cho_demo.py token)" \
  ./scripts/demo/cms-0057-f-demo.sh
```
