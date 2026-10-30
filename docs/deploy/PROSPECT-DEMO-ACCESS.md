# Giving a prospect the demo portal

This runbook gets `demo.cloudhealthoffice.com` ready for one prospect to use on their own, records the walkthrough video, and takes access away afterwards. It builds on [GCP-DEMO.md](GCP-DEMO.md) (the cluster) and [GCP-PUBLIC-HOSTING-PLAN.md](GCP-PUBLIC-HOSTING-PLAN.md) (the tunnel and Cloudflare Access application).

Everything on the demo is synthetic. The cluster trusts the public development signing key, so it is reachable only through Cloudflare Access, and a prospect is added to Access by email, one person at a time.

## Before the prospect's first login

1. **Pause automatic deploys.** Set the repository variable `GKE_AUTO_DEPLOY` to `false`, so a merge to `main` cannot restart the pods while the prospect is in the portal. Run **Deploy demo to GKE** by hand when you want a new build.

2. **Load the data.** On your laptop:

   ```bash
   gcloud container clusters get-credentials cho-demo --location us-west1-a --project YOUR_PROJECT_ID
   ./scripts/demo/gcp/port-forward.sh                  # leave running
   python3 scripts/demo/gcp/cho_demo.py smoke         # must end "All smoke checks passed."
   python3 scripts/demo/gcp/cho_demo.py seed-auths    # a year of prior-auth history for the CMS-0057-F console
   python3 scripts/demo/gcp/cho_demo.py seed-story    # the five records the /demo guided tour opens
   ```

   For the claims and batch adjudication steps, run `scripts/demo/gcp/run-mcc.sh` once as well (see [GCP-DEMO.md](GCP-DEMO.md)).

   `seed-story` dates each record from now: the expedited request is 60 hours into its 72-hour clock when you run it. Run it again shortly before the prospect logs in, and again before recording.

3. **Check it as a visitor.** In a private browser window, open `https://demo.cloudhealthoffice.com/demo` and click through all nine steps. Each one should open a populated page.

## Let the prospect in

In Cloudflare Zero Trust → Access → Applications → the `demo.cloudhealthoffice.com` application → its Allow policy, add the prospect's email address under **Include → Emails**. Keep the session at 24 hours.

Send them `https://demo.cloudhealthoffice.com/demo`. Cloudflare emails them a one-time code; after that the portal signs them in as the demo user and the guided tour opens.

## Record the walkthrough video

The **Record demo walkthrough** workflow drives the portal with Playwright and uploads a captioned video (`prior-auth.webm` or `full-tour.webm`) and a screenshot per step.

One-time setup:

1. Zero Trust → Access → **Service Tokens** (under Service auth or Service credentials, depending on the dashboard version) → create a token named `github-demo-recorder`. Copy the client id and secret.
2. On the demo application, add a policy with action **Service Auth** that includes that service token.
3. Add the repository secrets `DEMO_CF_ACCESS_CLIENT_ID` and `DEMO_CF_ACCESS_CLIENT_SECRET`.
4. If Cloudflare Bot Fight Mode is on for the zone, turn it off. It challenges automated browsers from GitHub's runners as it did the Inferno test kits.

Each recording: run `seed-story`, then Actions → **Record demo walkthrough** → choose `prior-auth` (about 1 minute, prior authorization only) or `full-tour`. Download the artifact from the run. The job fails, and names the steps, if any page did not render; the screenshots show what it saw.

## Afterwards

- Remove the prospect's email from the Access policy when the evaluation ends.
- Set `GKE_AUTO_DEPLOY` back to `true`, or delete it.
- To clear claims activity before the next prospect, run `scripts/demo/gcp/reset-demo.sh --yes`, then reload the data as above.
