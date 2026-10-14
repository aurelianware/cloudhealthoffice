# Cloud Health Office demo on GKE

Scripts for running the demo against the GKE environment from
`infrastructure/gcp` (Terraform) and `.github/workflows/deploy-gcp-gke.yml`.
The full runbook, including the 25-minute walkthrough and teardown, is
[docs/deploy/GCP-DEMO.md](../../../docs/deploy/GCP-DEMO.md).

All data is synthetic. The cluster has no public ingress and trusts the
public development signing key, so it is reached only through port-forward.

| Script | What it does |
|---|---|
| `port-forward.sh` | Opens the portal (8080), claims-service (5001), benefit-plan-service (5002) and fhir-service (5023) on localhost |
| `cho_demo.py smoke` | End-to-end checks: health, token enforcement, adapter labels, portal. Exits non-zero on failure |
| `run-mcc.sh` | Demo-sized Million Claim Challenge (default 2,000 claims). Results show in the portal's Mass Adjudication console |
| `cho_demo.py claims` | Narrates a paid claim, then a pended claim with the AI examiner's advisory recommendation and its unchanged status |
| `cho_demo.py adapter-status` | Shows which FHIR resources are demo, hybrid or live, and the response headers |
| `cho_demo.py token` | Prints a development token for the demo tenant (for `scripts/demo/cms-0057-f-demo.sh` as `FHIR_TOKEN`) |

Typical sequence, after the deploy workflow has run:

```bash
gcloud container clusters get-credentials cho-demo --location us-west1-a --project YOUR_PROJECT_ID   # us-west1 for Autopilot
./scripts/demo/gcp/port-forward.sh          # leave running in its own terminal
python3 scripts/demo/gcp/cho_demo.py smoke
GAR_REPOSITORY=... IMAGE_TAG=... ./scripts/demo/gcp/run-mcc.sh
python3 scripts/demo/gcp/cho_demo.py claims
FHIR_URL=http://localhost:5023 TENANT_ID=demo FHIR_TOKEN="$(python3 scripts/demo/gcp/cho_demo.py token)" \
  ./scripts/demo/cms-0057-f-demo.sh
```
