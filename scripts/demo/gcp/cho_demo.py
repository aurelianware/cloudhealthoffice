#!/usr/bin/env python3
"""Cloud Health Office demo helper for the GKE demo environment.

Standard library only. Talks to the services through the local ports that
scripts/demo/gcp/port-forward.sh opens.

  cho_demo.py token                 Print a development user token for the demo tenant
  cho_demo.py token --platform-admin
                                    Same, plus PlatformAdmin (needed by the MCC validator,
                                    which seeds platform prior-auth rules)
  cho_demo.py smoke                 End-to-end checks; exits non-zero on any failure
  cho_demo.py claims                Narrate a paid claim and a pended claim with its AI examination
  cho_demo.py adapter-status        Show the FHIR adapter labels (demo / hybrid / live)
  cho_demo.py seed-auths            Load a CMS-0057-F prior-authorization scenario for the
                                    portal's CMS-0057-F Console (needs port-forward's 5006)

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
AUTH_URL = os.environ.get("AUTH_URL", "http://localhost:5006")
PORTAL_URL = os.environ.get("PORTAL_URL", "http://localhost:8080")


# ClaimStatus (claims-service Models/Claim.cs); the API serializes it as a number.
CLAIM_STATUS = {1: "Submitted", 2: "Received", 3: "InAdjudication", 4: "Pended", 5: "Approved",
                6: "Denied", 7: "Paid", 8: "Voided", 9: "PartiallyPaid"}


def status_name(value) -> str:
    return CLAIM_STATUS.get(value, str(value)) if isinstance(value, int) else str(value)


def b64url(data: bytes) -> str:
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode()


def mint_token(tenant: str = TENANT, hours: float = 12, platform_admin: bool = False) -> str:
    now = int(time.time())
    header = {"alg": "HS256", "typ": "JWT"}
    payload = {
        "iss": DEV_USER_ISSUER,
        "aud": AUDIENCE,
        "sub": f"demo-user-{tenant}",
        "tenant_id": tenant,
        # platform:admin is reserved and not covered by *:* (ChoRolePermissions);
        # only PlatformAdmin holds it.
        "roles": ROLES + (["PlatformAdmin"] if platform_admin else []),
        "permissions": PERMISSIONS + (["platform:admin"] if platform_admin else []),
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

def cmd_token(args) -> int:
    print(mint_token(platform_admin=args.platform_admin))
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


# ── CMS-0057-F prior-authorization scenario ─────────────────────────────────

# (number suffix, level of service, status, hours since submission, decided after hours,
#  decided by: None = reviewer / "automated:<rule>", reason, denial code)
_OPEN = [
    ("E01", "U", "Submitted", 5, None),      # expedited, fresh
    ("E02", "U", "InReview", 20, None),
    ("E03", "U", "InReview", 52, None),      # past two-thirds: warning
    ("E04", "U", "Pended", 66, None),        # critical
    ("E05", "U", "Pended", 78, None),        # past the 72-hour deadline
    ("S01", "E", "Submitted", 18, None),     # standard, fresh
    ("S02", "E", "InReview", 50, None),
    ("S03", "E", "InReview", 90, None),
    ("S04", "E", "Pended", 132, None),       # warning (past 5 days)
    ("S05", "E", "Pended", 150, None),       # critical
    ("S06", "E", "InReview", 180, None),     # past the 7-day deadline
]

_DECIDED = [
    # recent decisions (days ago, level, status, hours to decide, decided by, reason, code)
    (1, "E", "Approved", 0.1, "automated:AutoApproveList", "Service is on the plan's auto-approve list", None),
    (1, "U", "Approved", 0.08, "automated:AutoApproveList", "Service is on the plan's auto-approve list", None),
    (2, "E", "Approved", 41, None, "Meets InterQual criteria for outpatient MRI", None),
    (3, "U", "Approved", 30, None, "Meets criteria; expedited per treating physician attestation", None),
    (4, "E", "Modified", 70, None, "Approved 6 of 12 requested PT visits; reassess after 6", None),
    (5, "E", "Denied", 96, None, "Conservative therapy not documented for 6 weeks", "N350"),
    (6, "U", "Denied", 80, None, "Clinical criteria not met for inpatient level of care", "N386"),
    (8, "E", "Approved", 190, None, "Approved after additional records received", None),
    (9, "E", "Denied", 0.2, "benefit-exclusion", "Cosmetic procedure excluded from the benefit plan", "N130"),
    (12, "E", "Approved", 26, None, "Meets criteria", None),
    (15, "U", "Approved", 12, None, "Meets criteria", None),
    (20, "E", "Approved", 0.1, "automated:AutoApproveList", "Service is on the plan's auto-approve list", None),
]

_MEMBERS = [("MBR001", "Jane", "Doe"), ("MBR002", "Luis", "Garcia"), ("MBR003", "Aisha", "Khan"),
            ("MBR004", "Tom", "Nguyen"), ("MBR005", "Grace", "Okafor")]
_SERVICES = [("72148", "MRI lumbar spine w/o contrast"), ("97110", "Therapeutic exercise"),
             ("29881", "Knee arthroscopy/meniscectomy"), ("99223", "Initial hospital care"),
             ("E0601", "CPAP device"), ("15823", "Blepharoplasty")]


def _iso(dt) -> str:
    return dt.strftime("%Y-%m-%dT%H:%M:%S.%fZ")


def build_cms0057_fixtures(now=None, seasonal_count: int = 36) -> list[dict]:
    """Synthetic prior authorizations that exercise the CMS-0057-F console.

    Open requests at every point of the 72-hour and 7-day clocks, recent
    decisions by reviewers and automated rules (some late), and older decisions
    across the year so the public metrics have a full period. Dates are
    relative to now, so re-running refreshes them (numbers are stable, and
    dev-seed updates by number).
    """
    import datetime as _dt
    now = now or _dt.datetime.now(_dt.timezone.utc)
    fixtures = []

    def base(number: str, i: int, level: str, status: str, submitted) -> dict:
        member = _MEMBERS[i % len(_MEMBERS)]
        code, description = _SERVICES[i % len(_SERVICES)]
        return {
            "authorizationNumber": f"CMS0057-{number}",
            "memberId": member[0], "patientFirstName": member[1], "patientLastName": member[2],
            "patientDateOfBirth": "1980-04-12T00:00:00Z",
            "lineOfBusiness": "Medicaid", "authorizationType": "PreAuthorization",
            "requestingProviderNPI": "1234567893", "requestingProviderName": "Lakeside Orthopedics",
            "serviceTypeCode": "1", "levelOfService": level,
            "requestedServiceDateFrom": _iso(submitted + _dt.timedelta(days=10)),
            "requestedServices": [{"procedureCode": code, "procedureDescription": description, "requestedUnits": 1}],
            "diagnosisCodes": [{"code": "M54.50", "codeQualifier": "BK", "description": "Low back pain"}],
            "status": status, "submittedDate": _iso(submitted), "createdDate": _iso(submitted),
        }

    for i, (number, level, status, hours_open, _) in enumerate(_OPEN):
        fixtures.append(base(number, i, level, status, now - _dt.timedelta(hours=hours_open)))

    def decided(number: str, i: int, level: str, status: str, decided_at, hours, by, reason, code) -> dict:
        submitted = decided_at - _dt.timedelta(hours=hours)
        fx = base(number, i, level, status, submitted)
        fx["reviewedDate"] = _iso(decided_at)
        # 278 HCR01: A1 certified, A3 not certified, A6 modified.
        hcr = {"Approved": "A1", "Denied": "A3", "Modified": "A6"}[status]
        fx["reviewDecision"] = hcr
        if by is None:
            fx["reviewerName"] = "Dr. Priya Raman, MD"
        if status == "Denied":
            fx["denialReason"] = reason
            fx["denialReasonCode"] = code
        fx["statusHistory"] = [
            {"status": "Submitted", "changedAt": _iso(submitted), "changedBy": "provider-portal"},
            {"status": status, "reviewDecision": hcr, "reason": reason, "changedAt": _iso(decided_at),
             "changedBy": "system" if by else "dr.raman", "source": by},
        ]
        return fx

    for i, (days_ago, level, status, hours, by, reason, code) in enumerate(_DECIDED):
        fixtures.append(decided(f"D{i + 1:02d}", i, level, status, now - _dt.timedelta(days=days_ago), hours, by, reason, code))

    # Older decisions spread across the year to date, so the metrics cover a period.
    start = _dt.datetime(now.year, 1, 5, 9, tzinfo=_dt.timezone.utc)
    span = max((now - _dt.timedelta(days=25) - start).total_seconds(), 0)
    for k in range(seasonal_count):
        decided_at = start + _dt.timedelta(seconds=span * k / max(seasonal_count - 1, 1))
        level = "U" if k % 4 == 0 else "E"
        status = ("Denied" if k % 6 == 0 else "Modified" if k % 9 == 0 else "Approved")
        hours = (6 + (k * 7) % 60) if level == "U" else (12 + (k * 23) % 150)
        by = "automated:AutoApproveList" if k % 5 == 0 and status == "Approved" else None
        reason = {"Denied": "Clinical criteria not met", "Modified": "Approved a reduced number of units"}.get(status, "Meets criteria")
        fixtures.append(decided(f"H{k + 1:02d}", k, level, status, decided_at, hours if not by else 0.1, by, reason,
                                "N350" if status == "Denied" else None))
    return fixtures


def cmd_seed_auths(_args) -> int:
    fixtures = build_cms0057_fixtures()
    status, _, body = request("POST", f"{AUTH_URL}/api/authorizations/dev-seed", mint_token(), {"authorizations": fixtures})
    if status != 200:
        print(f"dev-seed returned HTTP {status}: {str(body)[:500]}")
        if status == 0:
            print("Is scripts/demo/gcp/port-forward.sh running? It forwards authorization-service to localhost:5006.")
        return 1
    print(f"Seeded {len(fixtures)} prior authorizations for tenant '{TENANT}' "
          f"({body.get('created')} created, {body.get('updated')} updated).")
    print("Open the portal: Compliance → CMS-0057-F Console.")
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
    token = sub.add_parser("token")
    token.add_argument("--platform-admin", action="store_true",
                       help="include PlatformAdmin (platform:admin), e.g. for the MCC validator")
    token.set_defaults(func=cmd_token)
    sub.add_parser("smoke").set_defaults(func=cmd_smoke)
    sub.add_parser("claims").set_defaults(func=cmd_claims)
    sub.add_parser("adapter-status").set_defaults(func=cmd_adapter_status)
    sub.add_parser("seed-auths").set_defaults(func=cmd_seed_auths)
    args = parser.parse_args()
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
