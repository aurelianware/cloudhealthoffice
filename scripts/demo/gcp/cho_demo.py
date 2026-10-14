#!/usr/bin/env python3
"""Cloud Health Office demo helper for the GKE demo environment.

Standard library only. Talks to the services through the local ports that
scripts/demo/gcp/port-forward.sh opens.

  cho_demo.py token                 Print a development user token for the demo tenant
  cho_demo.py smoke                 End-to-end checks; exits non-zero on any failure
  cho_demo.py claims                Narrate a paid claim and a pended claim with its AI examination
  cho_demo.py adapter-status        Show the FHIR adapter labels (demo / hybrid / live)

The token is signed with the public development key in ChoDevelopmentAuth.
It is accepted only by Development and Testing hosts, which is how the demo
cluster runs. Never use it against an environment with real data.
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import hmac
import json
import os
import sys
import time
import urllib.error
import urllib.request
import uuid

# ChoDevelopmentAuth (src/services/shared/CloudHealthOffice.Infrastructure/Security/ChoDevelopmentAuth.cs)
DEV_KEY_B64 = "Q0hPLWRldmVsb3BtZW50LW9ubHktc2lnbmluZy1rZXktZG8tbm90LXVzZS0yMDI2"
DEV_USER_ISSUER = "cho-portal-dev"
AUDIENCE = "cho-api"

# ChoRolePermissions: TenantAdmin grants *:* within the tenant.
ROLES = ["TenantAdmin"]
PERMISSIONS = ["*:*", "users:manage", "roles:manage", "settings:manage", "operating-mode:manage"]

TENANT = os.environ.get("TENANT_ID", "demo")
CLAIMS_URL = os.environ.get("CLAIMS_URL", "http://localhost:5001")
BENEFIT_URL = os.environ.get("BENEFIT_URL", "http://localhost:5002")
FHIR_URL = os.environ.get("FHIR_URL", "http://localhost:5023")
PORTAL_URL = os.environ.get("PORTAL_URL", "http://localhost:8080")


# ClaimStatus (claims-service Models/Claim.cs); the API serializes it as a number.
CLAIM_STATUS = {1: "Submitted", 2: "Received", 3: "InAdjudication", 4: "Pended", 5: "Approved",
                6: "Denied", 7: "Paid", 8: "Voided", 9: "PartiallyPaid"}


def status_name(value) -> str:
    return CLAIM_STATUS.get(value, str(value)) if isinstance(value, int) else str(value)


def b64url(data: bytes) -> str:
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode()


def mint_token(tenant: str = TENANT, hours: float = 12) -> str:
    now = int(time.time())
    header = {"alg": "HS256", "typ": "JWT"}
    payload = {
        "iss": DEV_USER_ISSUER,
        "aud": AUDIENCE,
        "sub": f"demo-user-{tenant}",
        "tenant_id": tenant,
        "roles": ROLES,
        "permissions": PERMISSIONS,
        "name": "Demo Presenter",
        "jti": uuid.uuid4().hex,
        "iat": now,
        "nbf": now,
        "exp": now + int(hours * 3600),
    }
    signing_input = f"{b64url(json.dumps(header, separators=(',', ':')).encode())}.{b64url(json.dumps(payload, separators=(',', ':')).encode())}"
    signature = hmac.new(base64.b64decode(DEV_KEY_B64), signing_input.encode(), hashlib.sha256).digest()
    return f"{signing_input}.{b64url(signature)}"


def request(method: str, url: str, token: str | None = None, body: dict | None = None):
    headers = {"Accept": "application/json", "X-Tenant-ID": TENANT}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    data = None
    if body is not None:
        data = json.dumps(body).encode()
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(url, data=data, method=method, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            raw = resp.read()
            return resp.status, dict(resp.headers), (json.loads(raw) if raw[:1] in (b"{", b"[") else raw.decode(errors="replace"))
    except urllib.error.HTTPError as e:
        raw = e.read()
        return e.code, dict(e.headers), raw.decode(errors="replace")
    except (urllib.error.URLError, ConnectionError, TimeoutError) as e:
        return 0, {}, str(e)


def heading(text: str) -> None:
    print(f"\n\033[1;36m▶ {text}\033[0m")


def money(value) -> str:
    try:
        return f"${float(value):,.2f}"
    except (TypeError, ValueError):
        return "n/a"


# ── commands ─────────────────────────────────────────────────────────────────

def cmd_token(_args) -> int:
    print(mint_token())
    return 0


def cmd_adapter_status(_args) -> int:
    status, headers, body = request("GET", f"{FHIR_URL}/fhir/r4/adapter-status")
    if status != 200:
        print(f"adapter-status returned {status}: {body}")
        return 1
    heading("FHIR adapter labels")
    print(f"  effective mode:  {body.get('effectiveMode')}")
    print(f"  configured mode: {body.get('configuredMode')}")
    print(f"  data class:      {body.get('dataClassification')}")
    for resource in body.get("resources", []):
        print(f"  {resource.get('resource', '?'):<22} {resource.get('mode', '?')}")
    for name in ("X-CHO-Adapter-Mode", "X-CHO-Data-Class", "X-CHO-Adapter-Label"):
        value = next((v for k, v in headers.items() if k.lower() == name.lower()), None)
        print(f"  header {name}: {value}")
    return 0


def cmd_claims(_args) -> int:
    token = mint_token()

    # Adjudication ends at Approved or Denied; Paid comes later, with remittance.
    heading("An approved claim")
    status, _, paid = request("GET", f"{CLAIMS_URL}/api/claims/search?status=Approved&pageSize=1", token)
    if status != 200 or not paid:
        print(f"  no approved claim found (HTTP {status}). Run scripts/demo/gcp/run-mcc.sh first.")
    else:
        claim = paid[0]
        print(f"  claim {claim.get('claimNumber') or claim.get('id')}  member {claim.get('memberId')}  status {status_name(claim.get('status'))}")
        result = claim.get("adjudicationResult") or {}
        print(f"  billed {money(claim.get('totalChargeAmount'))}  allowed {money(result.get('allowedAmount'))}"
              f"  plan pays {money(result.get('payerPayment'))}"
              f"  member owes {money(result.get('patientResponsibility'))}")
        status, _, detail = request("GET", f"{CLAIMS_URL}/api/claims/{claim['id']}/adjudication-detail", token)
        if status == 200 and isinstance(detail, dict):
            print("  adjudication detail:")
            print("   " + json.dumps(detail, indent=2)[:1500].replace("\n", "\n   "))

    heading("A pended claim and the AI examiner's advisory recommendation")
    status, _, items = request("GET", f"{CLAIMS_URL}/api/claims/work-queue/items?limit=50", token)
    if status != 200 or not items:
        print(f"  no pended claims found (HTTP {status}).")
        return 0
    ncci = [i for i in items if "NCCI" in json.dumps(i).upper()] or items
    item = ncci[0]
    claim_id = item.get("claimId")
    print(f"  claim {claim_id}  pend {item.get('queueReasonCode', '')} {item.get('queueReason', '')}".rstrip())
    for reason in item.get("pendReasons") or []:
        print(f"    - {reason}")
    status, _, audit = request("GET", f"{CLAIMS_URL}/api/claims/{claim_id}/ai-examination/audit", token)
    if status == 200 and audit:
        latest = audit[-1] if isinstance(audit, list) else audit
        print("  AI examination (advisory):")
        print("   " + json.dumps(latest, indent=2)[:2000].replace("\n", "\n   "))
    else:
        print(f"  no AI examination recorded (HTTP {status}). Is the examiner enabled (ANTHROPIC_API_KEY)?")
    status, _, claim = request("GET", f"{CLAIMS_URL}/api/claims/{claim_id}", token)
    if status == 200 and isinstance(claim, dict):
        print(f"  claim status is still {status_name(claim.get('status'))}: the recommendation never changes a claim; an examiner decides.")
    return 0


def cmd_smoke(_args) -> int:
    token = mint_token()
    failures = []

    def check(name: str, ok: bool, detail: str = "") -> None:
        print(f"  {'✓' if ok else '✗'} {name}{f'  ({detail})' if detail else ''}")
        if not ok:
            failures.append(name)

    heading("Smoke test")
    for label, url in (("claims-service", CLAIMS_URL), ("benefit-plan-service", BENEFIT_URL), ("fhir-service", FHIR_URL)):
        status, _, _ = request("GET", f"{url}/health/live")
        check(f"{label} live", status == 200, f"HTTP {status}")

    status, headers, body = request("GET", f"{FHIR_URL}/fhir/r4/adapter-status")
    lower = {k.lower(): v for k, v in headers.items()}
    check("adapter-status reachable", status == 200, f"HTTP {status}")
    check("data is labeled synthetic", isinstance(body, dict) and str(body.get("dataClassification", "")).lower() == "synthetic",
          str(body.get("dataClassification")) if isinstance(body, dict) else "")
    status, headers, _ = request("GET", f"{FHIR_URL}/fhir/r4/metadata")
    check("FHIR CapabilityStatement", status == 200, f"HTTP {status}")

    status, _, _ = request("GET", f"{CLAIMS_URL}/api/claims/recent")
    check("claims API rejects a request without a token", status in (401, 403), f"HTTP {status}")
    status, _, _ = request("GET", f"{CLAIMS_URL}/api/claims/recent", token)
    check("claims API accepts a development token", status == 200, f"HTTP {status}")

    status, headers, _ = request("GET", f"{FHIR_URL}/fhir/r4/Patient?_count=1", token)
    lower = {k.lower(): v for k, v in headers.items()}
    check("FHIR responses carry adapter labels", "x-cho-adapter-mode" in lower and "x-cho-data-class" in lower,
          f"HTTP {status}")

    status, _, _ = request("GET", PORTAL_URL)
    check("portal reachable", status in (200, 302), f"HTTP {status}")

    print()
    if failures:
        print(f"FAILED: {len(failures)} check(s): {', '.join(failures)}")
        return 1
    print("All smoke checks passed.")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("token").set_defaults(func=cmd_token)
    sub.add_parser("smoke").set_defaults(func=cmd_smoke)
    sub.add_parser("claims").set_defaults(func=cmd_claims)
    sub.add_parser("adapter-status").set_defaults(func=cmd_adapter_status)
    args = parser.parse_args()
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
