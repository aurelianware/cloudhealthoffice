"""Tests for the Texas Medicaid demo generator and clock mirror.

    python3 -m pytest scripts/demo/texas/test_tx_demo.py
"""

import csv
import datetime as dt
import random
import sys
from collections import defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import tx_demo  # noqa: E402

UTC = dt.timezone.utc
CT = tx_demo.CENTRAL


def _read(path):
    with open(path, newline="", encoding="utf-8") as f:
        return list(csv.DictReader(f))


def _drop(tmp_path, day=dt.date(2026, 11, 23)):
    tx_demo.generate(tmp_path, day)
    stamp = day.strftime("%Y%m%d")
    return {kind: _read(tmp_path / f"{stamp}_{kind}.csv")
            for kind in ("members", "coverages", "medical_claims", "pharmacy_claims", "um_authorizations")}


def _kind(name):
    """The drop's naming rule (CpcdsDropFileKinds; trial.sh kind())."""
    n = name.rsplit(".", 1)[0].lower()
    for word, kind in (("balance", "balances"), ("roster", "rosters"), ("auth", "authorizations"),
                       ("278", "authorizations"), ("coverage", "coverages"), ("member", "members"),
                       ("claim", "claims")):
        if word in n:
            return kind
    return "unknown"


def test_file_names_route_to_the_right_import(tmp_path):
    summary = tx_demo.generate(tmp_path, dt.date(2026, 11, 23))
    kinds = sorted(_kind(name) for name in summary["files"])
    assert kinds == ["authorizations", "claims", "claims", "coverages", "members"]


def test_same_date_gives_the_same_files(tmp_path):
    a, b = tmp_path / "a", tmp_path / "b"
    tx_demo.generate(a, dt.date(2026, 11, 23))
    tx_demo.generate(b, dt.date(2026, 11, 23))
    for f in a.iterdir():
        assert f.read_bytes() == (b / f.name).read_bytes()


def test_required_columns_are_filled(tmp_path):
    d = _drop(tmp_path)
    for row in d["medical_claims"] + d["pharmacy_claims"]:
        for col in ("Claim unique identifier", "Member id", "Claim type", "Claim service start date",
                    "Claim billing provider NPI"):
            assert row[col], col
    for row in d["coverages"]:
        assert row["Member id"] and row["Plan identifier"] and row["Group id"] == tx_demo.GROUP_ID
        assert row["Plan identifier"] in tx_demo.PROGRAMS


def test_every_claim_names_a_loaded_member(tmp_path):
    d = _drop(tmp_path)
    members = {m["Member id"] for m in d["members"]}
    assert {c["Member id"] for c in d["coverages"]} == members
    for row in d["medical_claims"] + d["pharmacy_claims"]:
        assert row["Member id"] in members
    assert {a["member_id"] for a in d["um_authorizations"]} <= members


def test_claim_totals_are_the_sum_of_their_lines(tmp_path):
    lines = defaultdict(list)
    for row in _drop(tmp_path)["medical_claims"]:
        lines[row["Claim unique identifier"]].append(row)
    assert len(lines) > 100
    for rows in lines.values():
        head = rows[0]
        for total, line in (("Claim total allowed amount", "Line allowed amount"),
                            ("Claim payment amount", "Line payment amount"),
                            ("Claim total submitted amount", "Line submitted amount")):
            assert abs(float(head[total]) - sum(float(r[line]) for r in rows)) < 0.005
        assert {r["Member id"] for r in rows} == {head["Member id"]}


def test_the_core_pays_the_stale_therapy_rate(tmp_path):
    rows = [r for r in _drop(tmp_path)["medical_claims"] if r["Procedure code"] == "97110"]
    assert rows, "the shadow run needs claims with the stale rate"
    for r in rows:
        assert float(r["Line allowed amount"]) == round(tx_demo.CORE_STALE_RATES["97110"] * int(r["Number of units"]), 2)


def test_pharmacy_claims_meet_the_import_rules(tmp_path):
    rows = _drop(tmp_path)["pharmacy_claims"]
    assert rows
    for r in rows:
        assert int(r["Days supply"]) > 0
        assert len(r["National drug code"].replace("-", "")) in (10, 11) and "-" in r["National drug code"]


def test_npis_are_valid_and_below_the_issued_range():
    npis = [p["npi"] for p in tx_demo.PROVIDERS] + [tx_demo.PHARMACY_NPI, tx_demo.PRESCRIBER_NPI] + tx_demo.RENDERING_NPIS
    assert len(set(npis)) == len(npis)
    for npi in npis:
        assert tx_demo.is_valid_npi(npi)
        assert int(npi) < 1003000000  # NPPES issues from 1003000000 up
    assert tx_demo.is_valid_npi("1234567893")  # the CMS documentation example


def test_one_business_day_skips_the_weekend():
    friday_noon = dt.datetime(2026, 11, 20, 12, 0, tzinfo=CT).astimezone(UTC)
    due = tx_demo.texas_due(friday_noon, "Hospitalized", set())
    assert due.astimezone(CT) == dt.datetime(2026, 11, 23, 12, 0, tzinfo=CT)


def test_three_business_days_skip_configured_holidays():
    tuesday = dt.datetime(2026, 11, 24, 9, 30, tzinfo=CT).astimezone(UTC)
    holidays = {dt.date(2026, 11, 26), dt.date(2026, 11, 27)}
    due = tx_demo.texas_due(tuesday, "NotHospitalized", holidays)
    # Wed 25 (1), Thu and Fri are holidays, weekend, Mon 30 (2), Tue Dec 1 (3)
    assert due.astimezone(CT) == dt.datetime(2026, 12, 1, 9, 30, tzinfo=CT)


def test_a_weekend_request_starts_at_the_next_business_morning():
    saturday = dt.datetime(2026, 11, 21, 15, 0, tzinfo=CT).astimezone(UTC)
    due = tx_demo.texas_due(saturday, "Hospitalized", set())
    assert due.astimezone(CT) == dt.datetime(2026, 11, 24, 0, 0, tzinfo=CT)


def test_hour_clocks_are_wall_clock():
    t = dt.datetime(2026, 11, 21, 3, 0, tzinfo=UTC)
    assert tx_demo.texas_due(t, "PoststabilizationOrLifeThreatening", set()) == t + dt.timedelta(hours=1)
    assert tx_demo.texas_due(t, "InpatientDischarge", set()) == t + dt.timedelta(hours=72)


def test_pa_fixtures_cover_every_scenario_and_never_exceed_the_federal_clock():
    now = dt.datetime(2026, 11, 18, 18, 0, tzinfo=UTC)  # a Wednesday
    members, _ = tx_demo.build_members(random.Random(48), 120, now.date())
    fixtures = tx_demo.build_pa_fixtures(now, members, set())
    scenarios = {fx["_expected"]["scenario"] for fx in fixtures}
    assert {"PoststabilizationOrLifeThreatening", "InpatientDischarge", "Hospitalized", "NotHospitalized",
            "Hospitalized+inferred", "NotHospitalized+inferred"} <= scenarios
    for fx in fixtures:
        assert fx["lineOfBusiness"] == "Medicaid"
        assert len({f["authorizationNumber"] for f in fixtures}) == len(fixtures)
        texas, federal = fx["_expected"]["texasDue"], fx["_expected"]["federalDue"]
        assert min(texas, federal) <= federal
    overdue = next(fx for fx in fixtures if fx["authorizationNumber"].endswith("OVERDUE"))
    assert overdue["_expected"]["texasDue"] < now < overdue["_expected"]["federalDue"]
