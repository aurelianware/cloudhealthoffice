#!/usr/bin/env python3
"""Texas Medicaid demo for Cloud Health Office (synthetic data only).

Builds and loads a Texas Medicaid shaped demo into the GKE demo cluster's
`demo` tenant: a plan's core sends a daily CPCDS file drop, CHO serves it
through the CMS-0057-F APIs, shadow-adjudicates the claims against the core's
answers, and runs prior authorizations on the Texas Medicaid decision clocks
(Tex. Gov't Code ch. 540, PR #1863). The runbook and the click-through script
are in docs/deploy/TEXAS-MEDICAID-DEMO.md.

Standard library only. Talks to the services through the local ports that
`scripts/demo/texas/demo.sh connect` opens.

  tx_demo.py generate [--out DIR] [--date YYYY-MM-DD]
                        Write one day's synthetic drop: members, coverages,
                        medical and pharmacy claims, UM authorization history
  tx_demo.py setup      Benefit plans (STAR, STAR Kids, CHIP), the network and
                        its providers, plan code mappings, the fee schedule,
                        and the tenant's drop and shadow settings. Idempotent
  tx_demo.py drop-run   Load what is waiting in the drop now and print the run
                        (--expect NAMES: wait until those files are loaded, by any run)
  tx_demo.py seed-pa    Open prior authorizations on each Texas decision clock
  tx_demo.py report     Drop freshness, the latest shadow run, the PA clocks
  tx_demo.py fhir       Patient Access reads for one synthetic member

Every name, ID, NPI, address and amount here is invented. The plan is
"Sample Texas Community Health Plan (synthetic)", not any real plan, and the
fee schedule is not TMHP's. The token is signed with the public development
key, which only the demo cluster's Development and Testing hosts accept.
"""

from __future__ import annotations

import argparse
import csv
import datetime as dt
import io
import json
import os
import random
import sys
import time
import uuid
from pathlib import Path
from zoneinfo import ZoneInfo

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "gcp"))
import cho_demo  # noqa: E402  (mint_token, request: shared with the GKE demo helper)

TENANT = os.environ.get("TENANT_ID", "demo")
CLAIMS_URL = os.environ.get("CLAIMS_URL", "http://localhost:5001")
BENEFIT_URL = os.environ.get("BENEFIT_URL", "http://localhost:5002")
PROVIDER_URL = os.environ.get("PROVIDER_URL", "http://localhost:5004")
AUTH_URL = os.environ.get("AUTH_URL", "http://localhost:5006")
FHIR_URL = os.environ.get("FHIR_URL", "http://localhost:5023")
PORTAL_URL = os.environ.get("PORTAL_URL", "http://localhost:8080")

FIXTURES = HERE / "fixtures"
CENTRAL = ZoneInfo("America/Chicago")

PAYER = "Sample Texas Community Health Plan (synthetic)"
SOURCE_SYSTEM = "sample-core"
GROUP_ID = "HHSC-DALLAS-SYN"
GROUP_NAME = "Texas HHSC Dallas service area (synthetic)"
NETWORK_NAME = "Sample Texas Medicaid network (synthetic)"
EFFECTIVE = "2026-01-01"

# Core plan code -> CHO benefit plan (fixtures/benefit-plans.json).
PROGRAMS = {
    "STAR-DAL": {"planId": "TX-STAR-SYN", "name": "STAR (synthetic)", "copay_em": 0.0},
    "STARKIDS-DAL": {"planId": "TX-STARKIDS-SYN", "name": "STAR Kids (synthetic)", "copay_em": 0.0},
    "CHIP-DAL": {"planId": "TX-CHIP-SYN", "name": "CHIP (synthetic)", "copay_em": 0.0},
}

# Texas state holidays used by the demo's business-day clock. Illustrative: a
# plan's compliance team supplies the real list (PriorAuthDeadlines:TexasMedicaid:Holidays).
DEMO_HOLIDAYS = ["2026-11-26", "2026-11-27", "2026-12-24", "2026-12-25", "2026-12-26", "2027-01-01", "2027-01-18"]


# ── synthetic identities ─────────────────────────────────────────────────────

def npi_check_digit(first9: str) -> str:
    """NPI check digit: Luhn over the 80840 prefix plus the first nine digits."""
    digits = [int(c) for c in "80840" + first9]
    total = 0
    for i, d in enumerate(reversed(digits)):
        if i % 2 == 0:
            d *= 2
            if d > 9:
                d -= 9
        total += d
    return str((10 - total % 10) % 10)


def synthetic_npi(n: int) -> str:
    # Below 1003000000, where NPPES started issuing numbers, so no real provider has one.
    first9 = f"100{n:06d}"
    return first9 + npi_check_digit(first9)


def is_valid_npi(npi: str) -> bool:
    return len(npi) == 10 and npi.isdigit() and npi_check_digit(npi[:9]) == npi[9]


# The network. Kinds drive which services each provider bills.
PROVIDERS = [
    {"key": "peds", "npi": synthetic_npi(1001), "name": "Trinity Pediatrics (synthetic)", "taxonomy": "208000000X", "specialty": "Pediatrics", "kind": "primary"},
    {"key": "fqhc", "npi": synthetic_npi(1002), "name": "Elm Fork Community Clinic FQHC (synthetic)", "taxonomy": "261QF0400X", "specialty": "FQHC", "kind": "primary"},
    {"key": "family", "npi": synthetic_npi(1003), "name": "Cedar Hill Family Medicine (synthetic)", "taxonomy": "207Q00000X", "specialty": "Family Medicine", "kind": "primary"},
    {"key": "therapy", "npi": synthetic_npi(1004), "name": "Bluestem Pediatric Therapy (synthetic)", "taxonomy": "235Z00000X", "specialty": "Speech Therapy", "kind": "therapy"},
    {"key": "imaging", "npi": synthetic_npi(1005), "name": "North Texas Imaging Center (synthetic)", "taxonomy": "261QR0200X", "specialty": "Radiology", "kind": "imaging"},
    {"key": "hospital", "npi": synthetic_npi(1006), "name": "Prairie Regional Hospital (synthetic)", "taxonomy": "282N00000X", "specialty": "Acute Care Hospital", "kind": "hospital"},
]
PHARMACY_NPI = synthetic_npi(2001)
PRESCRIBER_NPI = synthetic_npi(3001)
RENDERING_NPIS = [synthetic_npi(3000 + i) for i in range(2, 8)]

FIRST = ["Mateo", "Sofia", "Liam", "Camila", "Noah", "Valentina", "Elijah", "Isabella", "Josiah", "Mia",
         "Santiago", "Aaliyah", "Diego", "Zoe", "Gael", "Ximena", "Isaiah", "Luna", "Adrian", "Naomi"]
LAST = ["Garza", "Hernandez", "Nguyen", "Johnson", "Martinez", "Williams", "Tran", "Robinson", "Lopez", "Okafor",
        "Ramirez", "Davis", "Flores", "Jackson", "Villarreal", "Pham", "Brooks", "Castillo", "Mendoza", "Harris"]
STREETS = ["Bluebonnet Ln", "Pecan St", "Mesquite Dr", "Live Oak Ave", "Prairie View Rd", "Cottonwood Ct"]
CITIES = [("Dallas", "75216"), ("Dallas", "75217"), ("Garland", "75040"), ("Irving", "75061"),
          ("Mesquite", "75149"), ("Grand Prairie", "75051")]

# (code, description, non-facility rate, facility rate, kinds that bill it, diagnosis)
SERVICES = [
    ("99213", "Office visit, established patient, low", 52.40, 38.10, {"primary"}, ("J06.9", "Acute upper respiratory infection, unspecified")),
    ("99214", "Office visit, established patient, moderate", 78.90, 57.30, {"primary"}, ("J45.909", "Unspecified asthma, uncomplicated")),
    ("99392", "Well-child visit, age 1-4 (Texas Health Steps)", 96.20, 96.20, {"primary"}, ("Z00.129", "Routine child health exam without abnormal findings")),
    ("99393", "Well-child visit, age 5-11 (Texas Health Steps)", 94.10, 94.10, {"primary"}, ("Z00.129", "Routine child health exam without abnormal findings")),
    ("87880", "Rapid strep test", 16.10, 16.10, {"primary"}, ("J02.0", "Streptococcal pharyngitis")),
    ("90460", "Vaccine administration, first component", 14.85, 14.85, {"primary"}, ("Z23", "Encounter for immunization")),
    ("97110", "Therapeutic exercise, 15 minutes", 21.30, 21.30, {"therapy"}, ("R26.89", "Other abnormalities of gait and mobility")),
    ("97530", "Therapeutic activities, 15 minutes", 23.90, 23.90, {"therapy"}, ("F82", "Specific developmental disorder of motor function")),
    ("71046", "Chest X-ray, 2 views", 27.60, 27.60, {"imaging"}, ("R05.9", "Cough, unspecified")),
]
SERVICE_BY_CODE = {s[0]: s for s in SERVICES}

# The core's "answers" differ from CHO's on purpose in one pattern, so the
# shadow run has a real finding to show: the core still pays 97110 at last
# year's rate, so on those claims the core paid more than the loaded schedule.
# Member cost sharing is $0 in all three synthetic plans.
CORE_STALE_RATES = {"97110": 23.45}

# Format-valid NDCs, as the CPCDS sample uses; the importer checks the format only.
NDCS = [("00093-7146-56", 30, 18.00), ("00093-4155-73", 10, 9.50), ("00054-3297-63", 30, 6.75), ("00378-1805-01", 30, 22.40)]


def _money(x: float) -> str:
    return f"{x:.2f}"


def _date(d: dt.date) -> str:
    return d.isoformat()


# ── generate ─────────────────────────────────────────────────────────────────

CLAIM_COLUMNS = [
    "Claim unique identifier", "Member id", "Coverage id", "Claim type", "Claim service start date",
    "Claim service end date", "Claim received date", "Claim paid date", "Member admission date",
    "Member discharge date", "Claim bill facility type code", "Claim service classification type code",
    "Claim frequency code", "Claim diagnosis related group", "Claim inpatient admission type code",
    "Claim source inpatient admission code", "Patient discharge status code", "Claim processing status code",
    "Claim payment status code", "Claim payment denial code", "Claim billing provider NPI",
    "Claim billing provider network status", "Claim performing provider NPI", "Claim total submitted amount",
    "Claim total allowed amount", "Claim payment amount", "Member paid deductible", "Co-insurance liability amount",
    "Copay amount", "Member liability", "Line number", "Service (from) date", "Service to date",
    "Place of service code", "Revenue center code", "Number of units", "Line submitted amount",
    "Line allowed amount", "Line payment amount", "Line member liability", "Tooth number", "Tooth surface",
    "Diagnosis code", "Diagnosis description", "Present on admission", "Diagnosis code type", "Diagnosis type",
    "Procedure code", "Procedure description", "Procedure code type", "Procedure type", "Modifier Code-1",
]
PHARMACY_COLUMNS = [
    "Claim unique identifier", "Member id", "Coverage id", "Claim type", "Claim service start date",
    "Claim received date", "Claim paid date", "Claim processing status code", "Claim payment status code",
    "Claim payment denial code", "Claim billing provider NPI", "Claim billing provider network status",
    "Claim prescribing provider NPI", "RX service reference number", "Days supply", "DAW product selection code",
    "Refill number", "Refills authorized", "Prescription origin code", "Plan reported brand-generic code",
    "Compound code", "Pharmacy service type code", "Claim total submitted amount", "Claim total allowed amount",
    "Claim payment amount", "Copay amount", "Member liability", "Line number", "Service (from) date",
    "National drug code", "Quantity dispensed", "Line submitted amount", "Line allowed amount",
    "Line payment amount", "Line member liability",
]
MEMBER_COLUMNS = ["Member id", "First name", "Last name", "Middle name", "Date of birth", "Gender code",
                  "Home_Address line", "Home_City", "Home_State", "Home_Zip code", "Subscriber id",
                  "Relationship to subscriber"]
COVERAGE_COLUMNS = ["Coverage id", "Member id", "Subscriber id", "Dependent number", "Coverage type",
                    "Coverage status", "Start date", "End date", "Group id", "Group name", "Plan identifier",
                    "Plan name", "Payer identifier", "Relationship to subscriber"]
AUTH_COLUMNS = ["authorization_number", "member_id", "status", "received_date", "decision_date",
                "requesting_provider_npi", "requesting_provider_name", "servicing_provider_npi", "facility_npi",
                "request_type", "service_type_code", "level_of_service", "line_of_business",
                "requested_from_date", "requested_to_date", "approved_from_date", "approved_to_date",
                "diagnosis_codes", "denial_reason_code", "denial_reason", "line_number", "procedure_code",
                "modifiers", "requested_units", "approved_units", "unit_type", "place_of_service",
                "line_status", "line_denial_reason"]


def build_members(rng: random.Random, count: int, as_of: dt.date):
    """Members and their coverage. Medicaid enrolls each person in their own
    right, so every member is their own subscriber (relationship self)."""
    members, coverages = [], []
    codes = ["STAR-DAL"] * 6 + ["CHIP-DAL"] * 3 + ["STARKIDS-DAL"]
    for i in range(count):
        code = codes[i % len(codes)]
        child = code != "STAR-DAL" or i % 3 == 0
        age = rng.randint(1, 17) if child else rng.randint(19, 44)
        dob = dt.date(as_of.year - age, rng.randint(1, 12), rng.randint(1, 28))
        city, zip_code = CITIES[i % len(CITIES)]
        member_id = f"TXM{7100001 + i}"
        members.append({
            "Member id": member_id, "First name": FIRST[i % len(FIRST)], "Last name": LAST[(i * 7) % len(LAST)],
            "Middle name": "", "Date of birth": _date(dob), "Gender code": "F" if i % 2 else "M",
            "Home_Address line": f"{100 + i * 3} {STREETS[i % len(STREETS)]}", "Home_City": city,
            "Home_State": "TX", "Home_Zip code": zip_code, "Subscriber id": member_id,
            "Relationship to subscriber": "self",
        })
        coverages.append({
            "Coverage id": f"COV-{member_id}", "Member id": member_id, "Subscriber id": member_id,
            "Dependent number": "00", "Coverage type": "HMO", "Coverage status": "active",
            "Start date": EFFECTIVE, "End date": "", "Group id": GROUP_ID, "Group name": GROUP_NAME,
            "Plan identifier": code, "Plan name": PROGRAMS[code]["name"], "Payer identifier": "TX-SYN-PAYER",
            "Relationship to subscriber": "self",
        })
    return members, coverages


def _claim_rows(claim: dict, lines: list[dict]) -> list[dict]:
    header = {k: "" for k in CLAIM_COLUMNS}
    header.update({
        "Claim unique identifier": claim["id"], "Member id": claim["member"], "Coverage id": f"COV-{claim['member']}",
        "Claim type": claim["type"], "Claim service start date": _date(claim["from"]),
        "Claim service end date": _date(claim["to"]), "Claim received date": _date(claim["received"]),
        "Claim paid date": _date(claim["paid"]), "Claim frequency code": "1", "Claim processing status code": "active",
        "Claim payment status code": claim["status"], "Claim payment denial code": claim.get("denial", ""),
        "Claim billing provider NPI": claim["billing"], "Claim billing provider network status": "innetwork",
        "Claim performing provider NPI": claim["rendering"],
        "Claim total submitted amount": _money(sum(l["billed"] for l in lines)),
        "Claim total allowed amount": _money(sum(l["allowed"] for l in lines)),
        "Claim payment amount": _money(sum(l["paid"] for l in lines)),
        "Member paid deductible": "0.00", "Co-insurance liability amount": "0.00",
        "Copay amount": _money(sum(l["member"] for l in lines)),
        "Member liability": _money(sum(l["member"] for l in lines)),
    })
    if claim["type"] == "outpatient-facility":
        header.update({"Claim bill facility type code": "13", "Claim service classification type code": "1"})
    rows = []
    for n, line in enumerate(lines, start=1):
        row = dict(header)
        row.update({
            "Line number": str(n), "Service (from) date": _date(claim["from"]), "Service to date": _date(claim["to"]),
            "Place of service code": line.get("pos", ""), "Revenue center code": line.get("revenue", ""),
            "Number of units": str(line["units"]), "Line submitted amount": _money(line["billed"]),
            "Line allowed amount": _money(line["allowed"]), "Line payment amount": _money(line["paid"]),
            "Line member liability": _money(line["member"]),
            "Diagnosis code": line["dx"][0], "Diagnosis description": line["dx"][1],
            "Diagnosis code type": "icd10", "Diagnosis type": "principal" if n == 1 else "secondary",
            "Procedure code": line["code"], "Procedure description": line["desc"], "Procedure code type": "CPT",
        })
        rows.append(row)
    return rows


def build_claims(rng: random.Random, members: list[dict], coverages: list[dict], as_of: dt.date, days: int):
    """Professional claims the core adjudicated and paid in the last `days`
    days, a few outpatient ER facility claims, and pharmacy claims.

    The rolling window repeats earlier days' claims, as a core's nightly
    extract does; reloading the same claim changes nothing in CHO.
    """
    plan_of = {c["Member id"]: c["Plan identifier"] for c in coverages}
    primary = [p for p in PROVIDERS if p["kind"] == "primary"]
    by_kind = {p["kind"]: p for p in PROVIDERS}
    rows, rx_rows = [], []
    seq = 0
    for day in range(days, 0, -1):
        paid = as_of - dt.timedelta(days=day)
        if paid.weekday() >= 5:
            continue  # the core's payment cycle runs on weekdays
        for _ in range(rng.randint(6, 10)):
            seq += 1
            member = members[rng.randrange(len(members))]
            mid = member["Member id"]
            program = plan_of[mid]
            kids_or_chip = program in ("STARKIDS-DAL", "CHIP-DAL")
            roll = rng.random()
            if roll < 0.15 and kids_or_chip:
                provider, codes = by_kind["therapy"], ["97110"] if rng.random() < 0.7 else ["97110", "97530"]
            elif roll < 0.22:
                provider, codes = by_kind["imaging"], ["71046"]
            else:
                provider = primary[rng.randrange(len(primary))]
                first = rng.choice(["99213", "99214", "99392", "99393"])
                codes = [first] + (["87880"] if first == "99213" and rng.random() < 0.4 else []) \
                    + (["90460"] if first in ("99392", "99393") else [])
            service = paid - dt.timedelta(days=rng.randint(6, 20))
            received = service + dt.timedelta(days=rng.randint(1, 4))
            lines = []
            for code in codes:
                _, desc, rate, _facility, _, dx = SERVICE_BY_CODE[code]
                units = 2 if code in ("97110", "97530") else 1
                allowed = round(CORE_STALE_RATES.get(code, rate) * units, 2)
                member_share = min(PROGRAMS[program]["copay_em"], allowed) if code in ("99213", "99214") else 0.0
                billed = round(allowed * 1.65, 2)
                lines.append({"code": code, "desc": desc, "units": units, "billed": billed, "allowed": allowed,
                              "paid": round(allowed - member_share, 2), "member": member_share, "pos": "11", "dx": dx})
            rows += _claim_rows({
                "id": f"TXC-{paid:%y%m%d}-{seq:05d}", "member": mid, "type": "professional-nonclinician",
                "from": service, "to": service, "received": received, "paid": paid, "status": "paid",
                "billing": provider["npi"], "rendering": RENDERING_NPIS[seq % len(RENDERING_NPIS)],
            }, lines)

            if seq % 9 == 0:
                # An ER visit billed by the hospital. No facility contract is in
                # the demo, so shadow adjudication lists these under "what to load next".
                er_lines = [
                    {"code": "99283", "desc": "Emergency department visit, moderate", "units": 1, "billed": 1840.00,
                     "allowed": 412.35, "paid": 412.35, "member": 0.0, "revenue": "0450", "dx": ("R10.9", "Unspecified abdominal pain")},
                    {"code": "74177", "desc": "CT abdomen and pelvis with contrast", "units": 1, "billed": 2960.00,
                     "allowed": 288.10, "paid": 288.10, "member": 0.0, "revenue": "0350", "dx": ("R10.9", "Unspecified abdominal pain")},
                ]
                rows += _claim_rows({
                    "id": f"TXF-{paid:%y%m%d}-{seq:05d}", "member": mid, "type": "outpatient-facility",
                    "from": service, "to": service, "received": received, "paid": paid, "status": "paid",
                    "billing": by_kind["hospital"]["npi"], "rendering": RENDERING_NPIS[0],
                }, er_lines)

            if seq % 4 == 0:
                ndc, days_supply, allowed = NDCS[seq % len(NDCS)]
                copay = 0.0  # Texas Medicaid and CHIP pharmacy copays are $0 in this synthetic plan
                row = {k: "" for k in PHARMACY_COLUMNS}
                row.update({
                    "Claim unique identifier": f"TXR-{paid:%y%m%d}-{seq:05d}", "Member id": mid,
                    "Coverage id": f"COV-{mid}", "Claim type": "pharmacy", "Claim service start date": _date(paid),
                    "Claim received date": _date(paid), "Claim paid date": _date(paid),
                    "Claim processing status code": "active", "Claim payment status code": "paid",
                    "Claim billing provider NPI": PHARMACY_NPI, "Claim billing provider network status": "innetwork",
                    "Claim prescribing provider NPI": PRESCRIBER_NPI, "RX service reference number": f"RX{8800000 + seq}",
                    "Days supply": str(days_supply), "DAW product selection code": "0", "Refill number": "0",
                    "Refills authorized": "2", "Prescription origin code": "1", "Plan reported brand-generic code": "2",
                    "Compound code": "1", "Pharmacy service type code": "1",
                    "Claim total submitted amount": _money(allowed * 1.4), "Claim total allowed amount": _money(allowed),
                    "Claim payment amount": _money(allowed - copay), "Copay amount": _money(copay),
                    "Member liability": _money(copay), "Line number": "1", "Service (from) date": _date(paid),
                    "National drug code": ndc, "Quantity dispensed": str(days_supply),
                    "Line submitted amount": _money(allowed * 1.4), "Line allowed amount": _money(allowed),
                    "Line payment amount": _money(allowed - copay), "Line member liability": _money(copay),
                })
                rx_rows.append(row)
    return rows, rx_rows


def build_authorization_history(rng: random.Random, members: list[dict], as_of: dt.date, count: int = 24):
    """Decided prior authorizations from the plan's UM platform, for the
    Prior Authorization API and the CMS-0057-F metrics. Imported records are
    read-only in CHO; the open requests on the Texas clocks come from seed-pa."""
    rows = []
    menu = [
        ("97110", "Bluestem Pediatric Therapy (synthetic)", "therapy", "1", "standard", "R26.89", 24),
        ("92507", "Bluestem Pediatric Therapy (synthetic)", "therapy", "1", "standard", "F80.2", 24),
        ("70553", "North Texas Imaging Center (synthetic)", "imaging", "4", "standard", "G40.909", 1),
        ("E0601", "Cedar Hill Family Medicine (synthetic)", "family", "12", "standard", "G47.33", 1),
        ("74177", "Prairie Regional Hospital (synthetic)", "hospital", "4", "urgent", "R10.9", 1),
    ]
    npi_of = {p["key"]: p["npi"] for p in PROVIDERS}
    for i in range(count):
        code, prov_name, key, stc, level, dx, units = menu[i % len(menu)]
        received = as_of - dt.timedelta(days=rng.randint(5, 80))
        decided = received + dt.timedelta(days=rng.randint(0, 2) if level == "urgent" else rng.randint(1, 3))
        status = "denied" if i % 7 == 3 else "modified" if i % 5 == 2 and units > 1 else "approved"
        approved_units = 0 if status == "denied" else units // 2 if status == "modified" else units
        start = received + dt.timedelta(days=7)
        row = {k: "" for k in AUTH_COLUMNS}
        row.update({
            "authorization_number": f"TXPA-H{i + 1:03d}", "member_id": members[(i * 5) % len(members)]["Member id"],
            "status": status, "received_date": _date(received), "decision_date": _date(decided),
            "requesting_provider_npi": npi_of[key], "requesting_provider_name": prov_name,
            "servicing_provider_npi": npi_of[key], "request_type": "prior", "service_type_code": stc,
            "level_of_service": level, "line_of_business": "Medicaid", "requested_from_date": _date(start),
            "requested_to_date": _date(start + dt.timedelta(days=90)),
            "approved_from_date": "" if status == "denied" else _date(start),
            "approved_to_date": "" if status == "denied" else _date(start + dt.timedelta(days=90)),
            "diagnosis_codes": dx, "denial_reason_code": "NOTMEDNEC" if status == "denied" else "",
            "denial_reason": "Not medically necessary: criteria not met" if status == "denied" else "",
            "line_number": "1", "procedure_code": code, "requested_units": str(units),
            "approved_units": "" if status == "denied" else str(approved_units), "unit_type": "UN",
            "place_of_service": "11" if key != "hospital" else "22", "line_status": status,
            "line_denial_reason": {"denied": "Not medically necessary",
                                   "modified": f"{approved_units} of {units} units approved"}.get(status, ""),
        })
        rows.append(row)
    return rows


def write_csv(path: Path, columns: list[str], rows: list[dict]) -> None:
    with path.open("w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=columns, lineterminator="\n")
        writer.writeheader()
        writer.writerows(rows)


def generate(out: Path, as_of: dt.date, members_count: int = 120, window_days: int = 30, seed: int = 48) -> dict:
    """One day's drop. Deterministic for a given date and seed, so a rehearsal
    and the live demo load the same files."""
    rng = random.Random(f"{seed}-{as_of.isoformat()}")
    # The roster of members is the same every day (same seed, no date).
    members, coverages = build_members(random.Random(seed), members_count, as_of)
    claims, rx = build_claims(rng, members, coverages, as_of, window_days)
    auths = build_authorization_history(random.Random(seed + 1), members, as_of)
    out.mkdir(parents=True, exist_ok=True)
    stamp = as_of.strftime("%Y%m%d")
    files = {
        f"{stamp}_members.csv": (MEMBER_COLUMNS, members),
        f"{stamp}_coverages.csv": (COVERAGE_COLUMNS, coverages),
        f"{stamp}_medical_claims.csv": (CLAIM_COLUMNS, claims),
        f"{stamp}_pharmacy_claims.csv": (PHARMACY_COLUMNS, rx),
        f"{stamp}_um_authorizations.csv": (AUTH_COLUMNS, auths),
    }
    for name, (columns, rows) in files.items():
        write_csv(out / name, columns, rows)
    return {
        "folder": str(out), "files": sorted(files),
        "members": len(members), "claims": len({r["Claim unique identifier"] for r in claims}),
        "pharmacyClaims": len(rx), "authorizations": len(auths),
    }


def cmd_generate(args) -> int:
    as_of = dt.date.fromisoformat(args.date) if args.date else dt.datetime.now(CENTRAL).date()
    summary = generate(Path(args.out) / as_of.strftime("%Y%m%d"), as_of)
    print(f"Wrote the {as_of} drop to {summary['folder']}:")
    for name in summary["files"]:
        print(f"  {name}")
    print(f"  {summary['members']} members, {summary['claims']} medical claims, "
          f"{summary['pharmacyClaims']} pharmacy claims, {summary['authorizations']} decided authorizations")
    return 0


# ── setup ────────────────────────────────────────────────────────────────────

def _call(method: str, url: str, token: str, body=None):
    return cho_demo.request(method, url, token, body)


def _multipart(url: str, token: str, field: str, filename: str, content: bytes):
    import urllib.error
    import urllib.request
    boundary = uuid.uuid4().hex
    body = (f"--{boundary}\r\nContent-Disposition: form-data; name=\"{field}\"; filename=\"{filename}\"\r\n"
            f"Content-Type: text/csv\r\n\r\n").encode() + content + f"\r\n--{boundary}--\r\n".encode()
    req = urllib.request.Request(url, data=body, method="POST", headers={
        "Authorization": f"Bearer {token}", "X-Tenant-ID": TENANT, "Accept": "application/json",
        "Content-Type": f"multipart/form-data; boundary={boundary}"})
    try:
        with urllib.request.urlopen(req, timeout=120) as resp:
            raw = resp.read()
            return resp.status, json.loads(raw) if raw[:1] in (b"{", b"[") else raw.decode(errors="replace")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode(errors="replace")
    except (urllib.error.URLError, ConnectionError, TimeoutError) as e:
        return 0, str(e)


class Steps:
    def __init__(self):
        self.failed = 0

    def ok(self, text: str) -> None:
        print(f"  ✓ {text}")

    def fail(self, text: str, status, body) -> None:
        self.failed += 1
        print(f"  ✗ {text} (HTTP {status}: {str(body)[:300]})")
        if status == 0:
            print("    Is 'scripts/demo/texas/demo.sh connect' running?")


def cmd_setup(_args) -> int:
    token = cho_demo.mint_token(TENANT)
    s = Steps()
    print(f"Setting up the Texas Medicaid demo in tenant '{TENANT}'")

    print("1. Service categories")
    status, _, body = _call("POST", f"{BENEFIT_URL}/api/v1/service-category-mappings/seed-system-defaults", token)
    if status in (200, 201, 204):
        s.ok("standard categories applied")
    else:
        s.ok(f"not saved ({status}); claims use the system defaults. demo.sh overlay sets ServiceCategoryMapping__AdminWriteEnabled")

    print("2. Network and providers")
    status, _, body = _call("GET", f"{PROVIDER_URL}/api/v1/networks?pageSize=200", token)
    items = body.get("items", []) if isinstance(body, dict) else []
    network_id = next((n.get("organizationId") for n in items if n.get("name") == NETWORK_NAME), None)
    if not network_id:
        status, _, body = _call("POST", f"{PROVIDER_URL}/api/v1/networks", token,
                                {"name": NETWORK_NAME, "networkType": "HMO", "lineOfBusiness": "Medicaid"})
        network_id = body.get("organizationId") if isinstance(body, dict) else None
        if not network_id:
            s.fail("network not created", status, body)
            return 1
        s.ok("network created")
    else:
        s.ok("network already there")

    plans = [p["planId"] for p in PROGRAMS.values()]
    for p in PROVIDERS:
        status, _, body = _call("GET", f"{PROVIDER_URL}/api/v1/providers/npi/{p['npi']}", token)
        if status == 200:
            provider_id = body.get("providerId")
            s.ok(f"{p['name']} already there")
        else:
            status, _, body = _call("POST", f"{PROVIDER_URL}/api/v1/providers", token, {
                "npi": p["npi"], "providerType": "Organization", "organizationName": p["name"],
                "taxonomyCode": p["taxonomy"], "primarySpecialty": p["specialty"],
                "networkParticipations": [{"networkId": network_id, "planId": plan, "lineOfBusiness": "Medicaid",
                                           "networkTier": "Tier1", "effectiveDate": f"{EFFECTIVE}T00:00:00Z"}
                                          for plan in plans]})
            if status != 201:
                s.fail(f"{p['name']} not created", status, body)
                continue
            provider_id = body.get("providerId")
            s.ok(f"{p['name']} ({p['npi']}) created, in the network")
        if not provider_id:
            continue
        status, _, body = _call("GET", f"{PROVIDER_URL}/api/v1/providers/{provider_id}/credentialing/status-as-of"
                                       f"?asOfDate={EFFECTIVE}T12:00:00Z", token)
        if status == 200 and isinstance(body, dict) and body.get("status") == "Approved":
            continue
        status, _, body = _call("POST", f"{PROVIDER_URL}/api/v1/providers/{provider_id}/credentialing/decisions", token, {
            "decision": "Approved", "decidedAt": f"{EFFECTIVE}T00:00:00Z", "credentialingDate": f"{EFFECTIVE}T00:00:00Z",
            "decisionAuthorityType": "DelegatedAuthority", "decisionAuthorityId": "tx-demo-setup"})
        if status not in (200, 201):
            s.fail(f"{p['name']} not credentialed", status, body)

    print("3. Benefit plans")
    status, _, existing = _call("GET", f"{BENEFIT_URL}/api/v1/plans", token)
    have = {p.get("planId") for p in existing} if isinstance(existing, list) else set()
    for plan in json.loads((FIXTURES / "benefit-plans.json").read_text()):
        if plan["planId"] in have:
            s.ok(f"{plan['planId']} already there")
            continue
        plan = dict(plan, tenantId=TENANT,
                    networkTiers=[{"tierName": "In-Network", "tierLevel": 1, "networkId": network_id}])
        status, _, body = _call("POST", f"{BENEFIT_URL}/api/v1/plans", token, plan)
        if status == 201:
            s.ok(f"{plan['planId']} created")
        else:
            s.fail(f"{plan['planId']} not created", status, body)

    print("4. Plan code mappings")
    for code, program in PROGRAMS.items():
        status, _, body = _call("POST", f"{BENEFIT_URL}/api/v1/plan-code-mappings", token, {
            "groupNumber": GROUP_ID, "insuranceLineCode": "HLT", "externalPlanCode": code, "planId": program["planId"]})
        if status in (201, 409):
            s.ok(f"{code} -> {program['planId']}" + (" (already mapped)" if status == 409 else ""))
        else:
            s.fail(f"{code} not mapped", status, body)

    print("5. Fee schedule")
    csv_bytes = (FIXTURES / "fee-schedule.csv").read_bytes()
    for program in PROGRAMS.values():
        name = f"Sample Texas Medicaid {program['name']} {EFFECTIVE[:4]}".replace(" ", "%20").replace("(", "%28").replace(")", "%29")
        status, body = _multipart(
            f"{BENEFIT_URL}/api/v1/fee-schedules/import/csv?name={name}&effectiveDate={EFFECTIVE}&type=Medicaid"
            f"&planId={program['planId']}&jurisdiction=TX&dryRun=false", token, "file", "fee-schedule.csv", csv_bytes)
        if status == 200:
            s.ok(f"{program['planId']}: {body.get('linesLoaded', '?') if isinstance(body, dict) else '?'} rates")
        elif status == 409:
            s.ok(f"{program['planId']}: schedule already loaded")
        else:
            s.fail(f"{program['planId']} fee schedule not loaded", status, body)

    print("6. Daily file drop and shadow adjudication")
    status, _, body = _call("PUT", f"{CLAIMS_URL}/api/v1/claims/import/cpcds/drops/settings", token, {
        "enabled": True, "sourceSystem": SOURCE_SYSTEM, "lineOfBusiness": "Medicaid",
        "shadowAfterImport": True, "expectedEveryHours": 24, "staleGraceHours": 6})
    if status == 200:
        loading = body.get("loading") if isinstance(body, dict) else None
        s.ok(f"drop settings saved (loading: {loading}; folder {body.get('inboundFolder') if isinstance(body, dict) else '?'})")
        if loading is False:
            print("    The deployment has the drop off: run 'demo.sh overlay' first.")
    else:
        s.fail("drop settings not saved", status, body)
    status, _, body = _call("PUT", f"{CLAIMS_URL}/api/v1/claims/shadow-adjudication/settings", token, {"enabled": True})
    if status == 200:
        s.ok("shadow adjudication on for the tenant")
    else:
        s.fail("shadow adjudication not turned on", status, body)

    print("Done." if s.failed == 0 else f"{s.failed} step(s) failed; see above.")
    return 0 if s.failed == 0 else 1


# ── drop-run ─────────────────────────────────────────────────────────────────

_EPOCH = dt.datetime(1970, 1, 1, tzinfo=dt.timezone.utc)


def _run_summary(run: dict) -> str:
    lines = [f"Run {run.get('id')}: {run.get('status')}"]
    for step in run.get("steps") or []:
        counts = {k: v for k, v in (step.get("counts") or {}).items() if v}
        lines.append(f"  {step.get('name')}: {step.get('status')} {json.dumps(counts)}")
        for err in (step.get("errors") or [])[:3]:
            lines.append(f"    ! {str(err)[:200]}")
    for note in run.get("notes") or []:
        lines.append(f"  note: {note}")
    return "\n".join(lines)


def cmd_drop_run(args) -> int:
    """Load what is waiting. With --expect, wait until every named file is in a finished run.

    The drop's own worker polls the folder too, so a run asked for here and the
    worker's run can each take part of the drop. Waiting on the file names
    reports the drop whichever run loaded it.
    """
    token = cho_demo.mint_token(TENANT)
    expect = set(args.expect or [])
    deadline = time.monotonic() + args.wait
    # Only runs from this release: the same date's drop may have loaded earlier today.
    since = (dt.datetime.now(dt.timezone.utc) - dt.timedelta(seconds=90))
    while True:
        status, _, body = _call("POST", f"{CLAIMS_URL}/api/v1/claims/import/cpcds/drops/run", token)
        if status not in (200, 204):
            print(f"drop run returned HTTP {status}: {str(body)[:500]}")
            return 1
        if not expect:
            print(json.dumps(body, indent=2)[:4000] if status == 200 else
                  "Nothing ready in the drop yet (a file loads once it has been still for the quiet period).")
            return 0
        status, _, runs = _call("GET", f"{CLAIMS_URL}/api/v1/claims/import/cpcds/drops?limit=20", token)
        runs = runs if isinstance(runs, list) else []
        mine = [r for r in runs if r.get("completedAt") and (_parse_utc(r.get("startedAt")) or _EPOCH) >= since
                and expect & {f.get("name") for f in r.get("files") or []}]
        loaded = {f.get("name") for r in mine for f in r.get("files") or []}
        if expect <= loaded:
            for run in sorted(mine, key=lambda r: r.get("startedAt") or ""):
                print(_run_summary(run))
            if len(mine) > 1:
                print(f"Note: the drop loaded in {len(mine)} runs.")
            return 0 if all(r.get("status") == "Succeeded" for r in mine) else 1
        if time.monotonic() > deadline:
            print(f"Still waiting on {sorted(expect - loaded)} after {args.wait}s.")
            for run in mine:
                print(_run_summary(run))
            return 1
        time.sleep(5)


# ── seed-pa: the Texas decision clocks ───────────────────────────────────────

def add_business_days(received_utc: dt.datetime, days: int, holidays: set[dt.date]) -> dt.datetime:
    """Mirror of TexasMedicaidDecisionClock.AddBusinessDays (authorization-service)."""
    local = received_utc.astimezone(CENTRAL)
    day, time_of_day = local.date(), local.timetz().replace(tzinfo=None)

    def business(d: dt.date) -> bool:
        return d.weekday() < 5 and d not in holidays

    if not business(day):
        while not business(day):
            day += dt.timedelta(days=1)
        time_of_day = dt.time(0, 0)
    counted = 0
    while counted < days:
        day += dt.timedelta(days=1)
        if business(day):
            counted += 1
    return dt.datetime.combine(day, time_of_day, tzinfo=CENTRAL).astimezone(dt.timezone.utc)


def texas_due(received_utc: dt.datetime, scenario: str, holidays: set[dt.date]) -> dt.datetime:
    if scenario == "PoststabilizationOrLifeThreatening":
        return received_utc + dt.timedelta(hours=1)
    if scenario == "InpatientDischarge":
        return received_utc + dt.timedelta(hours=72)
    if scenario == "Hospitalized":
        return add_business_days(received_utc, 1, holidays)
    return add_business_days(received_utc, 3, holidays)


def federal_due(received_utc: dt.datetime, level: str) -> dt.datetime:
    return received_utc + dt.timedelta(hours=72 if level == "U" else 168)


# (key, stated scenario or None, authorization type, level, hours since receipt,
#  member index, code, description, diagnosis, provider key, what it shows)
PA_SCENARIOS = [
    ("poststab", "PoststabilizationOrLifeThreatening", "ConcurrentReview", "U", 0.3, 0, "99223",
     "Inpatient care after emergency stabilization", ("I21.4", "Non-ST elevation myocardial infarction"), "hospital",
     "1 hour: poststabilization care for a hospitalized member (§540.0302(b)(3))"),
    ("discharge", "InpatientDischarge", "ConcurrentReview", "U", 20, 1, "E0470",
     "BiPAP device needed for discharge", ("J96.11", "Chronic respiratory failure with hypoxia"), "hospital",
     "72 hours: equipment an inpatient provider needs to discharge (§540.0302(b)(2))"),
    ("hospitalized", "Hospitalized", "ConcurrentReview", "U", 3, 2, "99232",
     "Continued inpatient stay, day 4", ("J18.9", "Pneumonia, unspecified organism"), "hospital",
     "1 business day: other request for a hospitalized member (§540.0302(b)(1))"),
    ("inferred-concurrent", None, "ConcurrentReview", "U", 5, 3, "99233",
     "Continued inpatient stay, day 6", ("A41.9", "Sepsis, unspecified organism"), "hospital",
     "No scenario stated: a concurrent review is taken as hospitalized (+inferred)"),
    ("outpatient-mri", "NotHospitalized", "PreAuthorization", "E", 26, 4, "70553",
     "MRI brain with and without contrast", ("G40.909", "Epilepsy, unspecified"), "imaging",
     "3 business days: member not hospitalized (§540.0303(b)(1)); federal would allow 7 days"),
    ("inferred-therapy", None, "PreAuthorization", "E", 2, 5, "92507",
     "Speech therapy, 24 visits", ("F80.2", "Mixed receptive-expressive language disorder"), "therapy",
     "No scenario stated: an outpatient request is taken as not hospitalized (+inferred)"),
    ("overdue", "NotHospitalized", "PreAuthorization", "E", 24 * 6, 6, "E0601",
     "CPAP device", ("G47.33", "Obstructive sleep apnea"), "family",
     "Past the Texas clock and still open: the console flags it before the federal 7 days run out"),
]


def build_pa_fixtures(now_utc: dt.datetime, members: list[dict], holidays: set[dt.date]) -> list[dict]:
    npi_of = {p["key"]: (p["npi"], p["name"]) for p in PROVIDERS}
    run = now_utc.astimezone(CENTRAL).strftime("%m%d%H%M")
    fixtures = []
    for key, scenario, auth_type, level, hours, mi, code, desc, dx, prov, shows in PA_SCENARIOS:
        received = now_utc - dt.timedelta(hours=hours)
        member = members[mi % len(members)]
        npi, prov_name = npi_of[prov]
        fx = {
            "id": f"txpa-{key}-{run}", "authorizationNumber": f"TXPA-{run}-{key.upper()}",
            "memberId": member["Member id"], "patientFirstName": member["First name"],
            "patientLastName": member["Last name"], "patientDateOfBirth": f"{member['Date of birth']}T00:00:00Z",
            "lineOfBusiness": "Medicaid", "authorizationType": auth_type, "levelOfService": level,
            "requestingProviderNPI": npi, "requestingProviderName": prov_name, "serviceTypeCode": "1",
            "requestedServiceDateFrom": cho_demo._iso(received + dt.timedelta(days=1)),
            "requestedServices": [{"procedureCode": code, "procedureDescription": desc, "requestedUnits": 1}],
            "diagnosisCodes": [{"code": dx[0], "codeQualifier": "BK", "description": dx[1]}],
            "status": "Submitted", "submittedDate": cho_demo._iso(received), "createdDate": cho_demo._iso(received),
        }
        if scenario:
            fx["decisionScenario"] = scenario
        effective = scenario or ("Hospitalized" if auth_type == "ConcurrentReview" else "NotHospitalized")
        fx["_expected"] = {
            "texasDue": texas_due(received, effective, holidays), "federalDue": federal_due(received, level),
            "scenario": effective + ("" if scenario else "+inferred"), "shows": shows,
        }
        fixtures.append(fx)
    return fixtures


def _parse_utc(value) -> dt.datetime | None:
    if not value:
        return None
    return dt.datetime.fromisoformat(str(value).replace("Z", "+00:00")).astimezone(dt.timezone.utc)


def _ct(value: dt.datetime) -> str:
    return value.astimezone(CENTRAL).strftime("%a %b %d %I:%M %p CT")


def cmd_seed_pa(_args) -> int:
    token = cho_demo.mint_token(TENANT)
    now = dt.datetime.now(dt.timezone.utc)
    members, _ = build_members(random.Random(48), 120, now.date())
    holidays = {dt.date.fromisoformat(d) for d in DEMO_HOLIDAYS}
    fixtures = build_pa_fixtures(now, members, holidays)
    payload = [{k: v for k, v in fx.items() if not k.startswith("_")} for fx in fixtures]
    status, _, body = _call("POST", f"{AUTH_URL}/api/authorizations/dev-seed", token, {"authorizations": payload})
    if status != 200:
        print(f"dev-seed returned HTTP {status}: {str(body)[:500]}")
        return 1
    print(f"Opened {len(fixtures)} Medicaid prior authorizations on the Texas clocks (tenant '{TENANT}'):\n")
    mismatches = 0
    for fx in fixtures:
        exp = fx["_expected"]
        status, _, stored = _call("GET", f"{AUTH_URL}/api/authorizations/{fx['id']}", token)
        due = _parse_utc(stored.get("decisionDueAt")) if status == 200 and isinstance(stored, dict) else None
        policy = stored.get("decisionDeadlinePolicy", "?") if isinstance(stored, dict) else "?"
        agrees = due is not None and abs((due - exp["texasDue"]).total_seconds()) < 60 and "tx-gc-540" in str(policy)
        mismatches += 0 if agrees else 1
        print(f"  {fx['authorizationNumber']}  {exp['shows']}")
        print(f"     received {_ct(_parse_utc(fx['submittedDate']))}; Texas due {_ct(exp['texasDue'])}; "
              f"federal would be {_ct(exp['federalDue'])}")
        print(f"     CHO: due {(_ct(due) if due else 'not set')}  policy {policy}  {'✓' if agrees else '✗ differs'}")
        print(f"     {PORTAL_URL}/authorizations/{fx['id']}")
    if mismatches:
        print(f"\n{mismatches} request(s) did not get the Texas clock. Check that 'demo.sh overlay' set "
              f"PriorAuthDeadlines__TexasMedicaid__Tenants__0={TENANT} on authorization-service.")
        return 1
    print("\nEvery request carries the Texas clock. Portal: Compliance → CMS-0057-F Console, or open one above.")
    return 0


# ── report ───────────────────────────────────────────────────────────────────

def cmd_report(_args) -> int:
    token = cho_demo.mint_token(TENANT)
    cho_demo.heading("Daily file drop")
    status, _, body = _call("GET", f"{CLAIMS_URL}/api/v1/claims/import/cpcds/drops/report?days=30", token)
    if status == 200:
        print(json.dumps(body, indent=2)[:2500])
    else:
        print(f"  HTTP {status}: {str(body)[:300]}")
    cho_demo.heading("Latest shadow adjudication run")
    status, _, runs = _call("GET", f"{CLAIMS_URL}/api/v1/claims/shadow-adjudication/runs?limit=1", token)
    run = (runs[0] if isinstance(runs, list) and runs else
           (runs.get("items") or [None])[0] if isinstance(runs, dict) else None)
    if run:
        status, _, rep = _call("GET", f"{CLAIMS_URL}/api/v1/claims/shadow-adjudication/runs/{run['id']}/report?top=10", token)
        print(json.dumps(rep, indent=2)[:3000] if status == 200 else f"  HTTP {status}: {str(rep)[:300]}")
    else:
        print(f"  no shadow run yet (HTTP {status})")
    print(f"\nPortal: {PORTAL_URL}/shadow-adjudication and {PORTAL_URL}/compliance/cms-0057f")
    return 0


# ── fhir ─────────────────────────────────────────────────────────────────────

def cmd_fhir(args) -> int:
    token = cho_demo.mint_token(TENANT)
    member = args.member
    for path in (f"Patient/{member}", f"Coverage?patient={member}", f"ExplanationOfBenefit?patient={member}",
                 f"ExplanationOfBenefit?patient={member}&type=pharmacy"):
        status, headers, body = _call("GET", f"{FHIR_URL}/fhir/r4/{path}", token)
        label = headers.get("X-CHO-Data-Class") or headers.get("x-cho-data-class") or "-"
        summary = ""
        if isinstance(body, dict):
            summary = f"{body.get('resourceType')} total={body.get('total', len(body.get('entry', []) or []))}" \
                if body.get("resourceType") == "Bundle" else f"{body.get('resourceType')} {body.get('id', '')}"
        print(f"  GET /fhir/r4/{path:<48} {status}  data-class={label}  {summary}")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    gen = sub.add_parser("generate")
    gen.add_argument("--out", default=os.environ.get("TX_DEMO_OUT", "./tx-demo-drop"))
    gen.add_argument("--date", help="the drop's date (default: today in Central time)")
    gen.set_defaults(func=cmd_generate)
    sub.add_parser("setup").set_defaults(func=cmd_setup)
    drop_run = sub.add_parser("drop-run")
    drop_run.add_argument("--expect", nargs="*", help="file names to wait for, loaded by this run or the worker's")
    drop_run.add_argument("--wait", type=int, default=180, help="seconds to wait for --expect")
    drop_run.set_defaults(func=cmd_drop_run)
    sub.add_parser("seed-pa").set_defaults(func=cmd_seed_pa)
    sub.add_parser("report").set_defaults(func=cmd_report)
    fhir = sub.add_parser("fhir")
    fhir.add_argument("--member", default="TXM7100001")
    fhir.set_defaults(func=cmd_fhir)
    args = parser.parse_args()
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
