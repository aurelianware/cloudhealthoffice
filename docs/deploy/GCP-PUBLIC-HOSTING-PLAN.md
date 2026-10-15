# Hosting Cloud Health Office on cloudhealthoffice.com: plan

Status: plan. Phase 1 is implemented in this repository; Phases 2–4 are proposals. Cost figures are list-price estimates as of October 2026; check current pricing before committing.

This plan makes three things reachable under `cloudhealthoffice.com`, which Cloudflare already serves as DNS and registrar:

| Hostname | What runs there | Who can reach it | Cost while idle |
|---|---|---|---|
| `cloudhealthoffice.com` | Marketing site, as today (Cloudflare) | Everyone | ~$0 |
| `demo.cloudhealthoffice.com` | The full platform on the GKE demo cluster, through a Cloudflare Tunnel | Only people approved in Cloudflare Access | Disks only, once the cluster can sleep (Phase 2) |
| `portal.` / `api.cloudhealthoffice.com` | A public sandbox: tenant signup, API keys, synthetic data (Cloud Run) | Everyone, rate-limited at the edge | Near $0, plus a small managed database |

A production, multi-tenant service holding real member data is Phase 4. It is a business decision with standing compliance cost, not a hosting change.

## Ground rules

1. **The demo cluster is never public.** It runs with the public development signing key from this repository (`ChoDevelopmentAuth`), and three services run as Testing hosts that trust it. Anyone who can reach it could mint an administrator token. It is reached only through Cloudflare Access, and it holds synthetic data only.
2. **Every tunnel hostname gets a Cloudflare Access application before it gets a route.** Create the Access application first, then the public hostname. That way there is never a window when the hostname resolves without a login wall.
3. **Public surfaces (Phase 3) are a separate deployment.** They get Production hosts, real signing keys in Secret Manager and their own database. They never connect to the demo cluster.

## Phase 1: `demo.cloudhealthoffice.com` through Cloudflare Tunnel and Access

**Done in the repo:** `deploy/gcp/cloudflare/cloudflared.yaml` runs one `cloudflared` connector in the `cloudhealthoffice` namespace, with a non-root, read-only filesystem and a pinned image. `scripts/deploy-gke.sh` applies it when `CLOUDFLARE_TUNNEL_TOKEN` is set and removes it when it is not. The deploy workflow passes the secret through.

`cloudflared` makes outbound connections only. It adds no load balancer, no external IP (the new account has four) and no inbound firewall rule.

**One-time setup, in this order.** Steps 1–3 are in the Cloudflare dashboard.

1. **Identity.** In Zero Trust → Settings → Authentication, add Google Workspace as a login method, or use the built-in one-time PIN by email.
2. **Access application first.** In Zero Trust → Access → Applications → Add → Self-hosted, set hostname `demo.cloudhealthoffice.com`. Add an Allow policy for specific emails, or an email domain for a whole prospect organization, and set the session to 24 hours.
3. **Tunnel.** In Zero Trust → Networks → Tunnels → Create (Cloudflared), name it `cho-demo` and copy the token. Under Public hostnames, add `demo.cloudhealthoffice.com`, service type `HTTP`, URL `portal.cloudhealthoffice.svc.cluster.local:80`.
4. **Secret.** On your Mac, run `gh secret set CLOUDFLARE_TUNNEL_TOKEN -R aurelianware/cloudhealthoffice` and paste the token at the prompt.
5. **Deploy.** Run the `Deploy demo to GKE` workflow with the existing `image_tag`.
6. **Check.** In a private browser window, `https://demo.cloudhealthoffice.com` should show the Cloudflare Access login first, then the portal's demo sign-in. The Tunnel page should show the connector as Healthy.

To give someone access, add their email to the policy. To shut access off, disable the Access application or delete the public hostname. The cluster is unaffected either way.

Cloudflare's Zero Trust free plan covers up to 50 users, and routing public hostnames through a tunnel needs no paid features.

Only the portal is routed. To let an approved person call the FHIR or claims APIs directly, add a second hostname behind its own Access application that uses Access service tokens for programmatic clients. Do this only when needed.

## Phase 2: sleep when idle, wake on demand

The node pool can scale to zero; that is already the documented pause. Waking takes 5–10 minutes: node boot, then MongoDB, Kafka and 35 services passing readiness. That is fine behind a "starting your environment" page, and not fine for a link someone expects to load instantly.

**Proposal:**

- **Sleep:** a Cloud Scheduler job resizes the `demo` node pool to 0 nightly. An idle check is a later refinement, for example no tunnel requests for two hours, read from Cloudflare analytics.
- **Wake:** a Cloudflare Worker on `demo.cloudhealthoffice.com` runs after Access. When the tunnel has no healthy connector, it serves a branded "Starting the demo environment" page that polls for readiness. It also calls a small, authenticated Cloud Run function that resizes the node pool to 1. The function's service account gets only `container.clusters.update` on this cluster.
- **Scheduled wake** before a known meeting, from the same function.

Cost asleep: the persistent disks (about 165 GB of standard disk, a few dollars a month) plus Artifact Registry storage. Running: about $7 a day. Cloud Scheduler and the function stay inside free tiers at this volume.

Effort: about one to two days.

## Phase 3: public sandbox on Cloud Run (`portal.` and `api.`)

The self-serve loop is partly built:

- **pricing-api** has `POST /api/v1/signup`, API-key authentication, and free, starter and professional tiers with monthly usage limits (`PricingApi:*TierMonthlyLimit`).
- **tenant-service** has platform subscriptions (`/api/v1/platform/subscriptions`) and Stripe billing controllers.
- The **portal** has tenant-aware sign-in. The `portal-ingress.yaml` and HTTPS docs already reference `portal.cloudhealthoffice.com`.

**Proposal:**

- **Cloud Run, minimum instances 0.** pricing-api, tenant-service and the portal first; the FHIR read APIs against a synthetic sandbox tenant next. Each scales to zero and starts in seconds. The Blazor Server portal needs session affinity and WebSockets, which Cloud Run supports.
- **Data:** a managed MongoDB starter tier in `us-west1`, for example MongoDB Atlas on GCP, roughly $10–30 a month. It is separate from the demo cluster.
- **Messaging:** in-process (`Messaging:Backend=InMemory`), or Pub/Sub later. No Kafka in the sandbox.
- **Identity:** Production hosts. Signing keys and the Stripe test keys go in Secret Manager. The development key is never accepted. Signup issues API keys scoped to a new sandbox tenant.
- **Edge:** Cloudflare proxied DNS in front of Cloud Run. That gives TLS, DDoS protection, WAF managed rules and rate limiting on `/api/v1/signup` and per API key. Google issues the custom-domain certificate only while the DNS record is unproxied, so set it to DNS-only first, then turn the proxy on.
- **Data rule:** synthetic data only. The signup terms say so, and the sandbox should reject uploads that look like real PHI, such as 834 and 837 files not marked as test data.

Before anything goes public, the endpoints need a security review: authentication on every route, abuse limits on signup, API-key storage (hashed), CORS, and error responses that leak nothing.

Effort: about one to two weeks, most of it hardening and review rather than hosting.

## Phase 4: production multi-tenant SaaS (decision, not a task)

This is for payers that do not want to run the platform in their own cloud. When real member data is involved, the bar is:

- Google Cloud BAA executed, with only BAA-covered services in use
- HIPAA administrative, physical and technical safeguards, documented
- tenant isolation reviewed, plus customer-managed encryption keys, audit logging, backups with tested restores, and disaster recovery
- private networking, an incident response plan, and on-call coverage
- SOC 2 Type II eventually, which payer procurement will ask for

The platform is already multi-tenant: tenant IDs, tenant-scoped authorization and per-tenant configuration. The remaining work is operational and contractual. Start it when a paying payer asks for hosted delivery.

## Sources

- [Cloudflare Tunnel on Kubernetes](https://developers.cloudflare.com/tunnel/deployment-guides/kubernetes/)
- [Cloudflare Zero Trust free plan (third-party summary)](https://costbench.com/software/business-vpn/cloudflare-zero-trust/free-plan/)
