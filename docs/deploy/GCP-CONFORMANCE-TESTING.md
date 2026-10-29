# External conformance testing against the GKE demo

Hosted Inferno ([inferno.healthit.gov/suites](https://inferno.healthit.gov/suites)) can only judge a server it can reach over the internet with real TLS. The interop harness runs the same kits nightly (`INFERNO-BULK-SERVER-001`, see [docs/interop/davinci.md](../interop/davinci.md)), but on a plain-HTTP network, so the SMART Backend Services group and its token-endpoint TLS test are not run there (gap-matrix G-60). This page puts fhir-service and smart-auth-service on two public hostnames of the demo's Cloudflare Tunnel for the length of a run.

The demo holds synthetic data only and trusts the public development key unless production security is configured ([GCP-DEMO.md](GCP-DEMO.md)). Every tunnel hostname stays behind Cloudflare Access. Only the paths a suite needs bypass it, and only while the suite runs.

## What the deploy changes

With `CONFORMANCE_FHIR_HOST` and `CONFORMANCE_AUTH_HOST` set, `scripts/deploy-gke.sh` renders:

| Service | Setting | Value |
| --- | --- | --- |
| smart-auth-service | `SmartAuth__Issuer` | `https://<CONFORMANCE_AUTH_HOST>` |
| smart-auth-service | `SmartAuth__FhirBaseUrl` | `https://<CONFORMANCE_FHIR_HOST>/fhir/r4` |
| smart-auth-service | `SmartAuth__TrustedProxyNetworks__n` | the cluster's pod range (`TRUSTED_PROXY_NETWORKS`, else read with gcloud) |
| fhir-service | `Fhir__ServerBaseUrl` | `https://<CONFORMANCE_FHIR_HOST>/fhir/r4` |
| fhir-service | `SmartAuth__Mode` | `ExternalIssuer`, with one `TrustedIssuers` entry: `https://<CONFORMANCE_AUTH_HOST>/`, audience `fhir-api`, tenant `demo`, `IssuesChoClientRegistrations` |

cloudflared hands the tunnel's HTTPS requests to the pods as plain HTTP. smart-auth-service (OpenIddict) refuses non-HTTPS requests to its endpoints, so it takes the scheme from `X-Forwarded-Proto`, believed only from the pod range cloudflared runs in. fhir-service fetches smart-auth-service's discovery document and keys from the public issuer URL, which is why `/.well-known/` on the auth host bypasses Access below.

Without the two variables nothing changes.

## One-time setup

1. **Cloudflare Access first.** In Zero Trust → Access → Applications, add a self-hosted application for each hostname (for example `fhir-test.cloudhealthoffice.com` and `auth-test.cloudhealthoffice.com`) with the same Allow policy as the demo. Then add these path-scoped applications, each with a single **Bypass** policy for Everyone:
   - `auth-test…/.well-known/*`: discovery and keys, public by definition. fhir-service needs it to validate tokens, so leave this one on.
   - `auth-test…/connect/token`: the token endpoint. It authenticates every caller itself (`private_key_jwt` or a client secret). Turn it on only for a run.
   - `fhir-test…/fhir/r4/*`: the FHIR API. Every data route requires a token, but fhir-service runs as a Testing host and returns exception details. Turn it on only for a run.

   Leave `/connect/authorize` behind Access. In demo security, smart-auth-service offers the development sign-in there.
2. **Tunnel routes.** In Zero Trust → Networks → Tunnels → `cho-demo` → Public hostnames, add `fhir-test…` → `http://fhir-service.cloudhealthoffice.svc.cluster.local:80` and `auth-test…` → `http://smart-auth-service.cloudhealthoffice.svc.cluster.local:80`.
3. **Bot protection.** Inferno is a script on a cloud IP, so Cloudflare's bot features answer it with a challenge page (`cf-mitigated: challenge`, HTTP 403) before it reaches CHO. On the Free plan, Bot Fight Mode cannot be skipped per hostname: turn it off for the run. Otherwise add a WAF custom rule, first in order, for `(http.host in {"fhir-test…" "auth-test…"})` with action **Skip** (remaining custom rules, Security Level, Browser Integrity Check, Super Bot Fight Mode).
4. **TLS.** In SSL/TLS → Edge Certificates, set **Minimum TLS Version** to 1.2. Inferno's TLS test expects TLS 1.0 and 1.1 to be refused, and Cloudflare's default accepts them.
5. **Repository variables.** Set `CONFORMANCE_FHIR_HOST` and `CONFORMANCE_AUTH_HOST` (bare hostnames), and optionally `TRUSTED_PROXY_NETWORKS` (comma-separated CIDRs). For the Bulk Data export groups, also set `BULK_EXPORT_BUCKET` and `FHIR_SERVICE_GOOGLE_ACCOUNT`. Then run **Deploy demo to GKE**.
6. **Check discovery.** `curl https://fhir-test…/fhir/r4/.well-known/smart-configuration` should show `token_endpoint` `https://auth-test…/connect/token`, `private_key_jwt`, and `client-confidential-asymmetric`. A 503 means fhir-service could not read the issuer's discovery document: check the `/.well-known/*` bypass.

## Each run

1. **Register Inferno** as a backend client of the demo tenant. In demo security, the development token works:

   ```bash
   kubectl port-forward -n cloudhealthoffice svc/smart-auth-service 5024:80 &
   TOKEN=$(./scripts/dev-token.sh --tenant demo --hours 1)
   curl -sS -X POST http://localhost:5024/api/admin/smart/clients \
     -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
     -d '{"clientId":"inferno-hosted","kind":"backend","displayName":"Inferno hosted test kit",
          "scopes":["system/*.read"],"providerId":"<a provider id in the demo tenant>",
          "jwksUri":"https://inferno.healthit.gov/suites/custom/bulk_data_v200/.well-known/jwks.json"}'
   ```

   The response says `"tokenEndpointAuthMethod": "private_key_jwt"`. `providerId` matters only for the export groups (it is the Group exported). With production security, the development token is refused; use a demo TenantAdmin's CHO token instead.
2. **Open the bypasses** for `/connect/token` and `/fhir/r4/*`.
3. **Run Inferno.** On the Bulk Data test kit page, start a **Bulk Data Access v2.0.0 Server** session with Bulk Data FHIR URL `https://fhir-test…/fhir/r4`, client ID `inferno-hosted`, encryption algorithm ES384, scope `system/*.read`, and the key ID blank. Run **SMART Backend Services**. With bulk export on, also run the Group and Patient export groups (Group ID = the `providerId` above). Skip system export, which CHO answers with 501 by design.
4. **Close up.** Turn the two bypasses off (and Bot Fight Mode back on if you turned it off), delete the client (`DELETE /api/admin/smart/clients/inferno-hosted`), and keep the Inferno session's results as the G-60 evidence.

## If a test fails

| Symptom | Likely cause |
| --- | --- |
| Token endpoint TLS test fails or warns | Minimum TLS version is below 1.2 |
| `invalid_request` mentioning HTTPS (ID2083) from the token endpoint | `X-Forwarded-Proto` not believed: the pod range in `SmartAuth__TrustedProxyNetworks__*` does not cover cloudflared |
| Token request refused for `aud` | The token URL Inferno used is not `https://<CONFORMANCE_AUTH_HOST>/connect/token` |
| FHIR calls answer 401 after a token is issued | fhir-service could not read discovery or keys, or the token's tenant is not `demo` |
| An HTML login page instead of JSON | A path still sits behind Access |
| 403 with `cf-mitigated: challenge` | Cloudflare bot protection challenged Inferno: see step 3 |
| 502 from Cloudflare | The tunnel route's service URL is wrong; cloudflared's log names the host it could not resolve |
| 500 "The X509 certificate could not be added to the store" | smart-auth-service has no writable `HOME` (fixed in the rendered manifests) |
| Settings changed but discovery shows the old issuer | Pods predate the change: `kubectl rollout restart deployment/smart-auth-service deployment/fhir-service -n cloudhealthoffice` |
