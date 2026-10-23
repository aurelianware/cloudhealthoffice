#!/usr/bin/env python3
"""Asks CMS's own IPPS pricer what each reference stay pays, and writes it down.

Run it through run.sh, which starts the pricer first. It talks to a pricer
already listening on localhost and does nothing else: no CMS artifact is read
from disk here and none is written.

The output is pipe-separated so it stays readable in a diff and needs no parser
on the .NET side. Every column is something CMS returned, including the rate
figures: the standardized amount's labor and non-labor split, the capital
federal rate and the DRG's weight and geometric mean length of stay. That is
deliberate. ADR 024 says no rate belongs in CHO's code, and it would be a poor
answer to that to hard-code a rate into the test that checks it. So the parity
test builds its rate set out of what CMS reported for these stays, and the file
is the whole of the fiscal year it knows about.
"""

from __future__ import annotations

import json
import sys
import urllib.error
import urllib.request

# One column per thing worth comparing. The intermediates matter as much as the
# total: two calculators can agree on what to pay and disagree about why, and a
# harness comparing only totals calls that a pass and then breaks on the next
# fiscal year.
COLUMNS = [
    "case",
    "return_code",
    "total_payment",
    "wage_index",
    "average_length_of_stay",
    "drg_weight",
    "drg_weight_fraction",
    "discharge_fraction",
    "national_labor_cost",
    "national_non_labor_cost",
    "operating_base_drg_payment",
    "operating_federal_specific_portion",
    "operating_ime_adjustment",
    "operating_dsh_adjustment",
    "operating_outlier_payment",
    "uncompensated_care_payment",
    "capital_federal_rate",
    "capital_geographic_adjustment_factor",
    "capital_federal_specific_portion",
    "capital_outlier",
    "capital_total_payment",
    "cost_threshold",
    # The hospital-specific portion, and the percentage that says CMS
    # recognised the hospital as one of the three categories that have one. The
    # percentage is reported because a portion of zero is ambiguous: it is what
    # a sole community hospital whose own rate lost the comparison is paid, and
    # also what a hospital whose provider type the harness got wrong is paid.
    "hospital_specific_portion",
    "hospital_specific_portion_percent",
    # Always empty, and reported anyway. CMS computes the pass-through only for
    # a request marked as coming from its web pricer, and this endpoint never
    # takes that path, so the column records the boundary: the one amount CHO
    # pays that this oracle cannot confirm. A corpus row carries a pass-through
    # amount on the provider record purely to keep that fact checked.
    "passthrough_total_plus_misc",
    "outlier_days",
    "regular_days_used",
    # The four hospital-level adjustments. They are taken on the assembled
    # payment rather than on the DRG amount, and in an order 42 CFR does not
    # give, so each one is reported separately: a total that agrees says
    # nothing about whether the order was right.
    "low_volume_payment",
    "readmissions_adjustment",
    "value_based_purchasing_adjustment",
    "hospital_acquired_condition_payment",
]


def post(base_url: str, path: str, body: dict) -> dict:
    request = urllib.request.Request(
        f"{base_url}{path}",
        data=json.dumps(body).encode("utf-8"),
        headers={"Content-Type": "application/json", "Accept": "application/json"},
        method="POST",
    )
    with urllib.request.urlopen(request, timeout=60) as response:
        return json.load(response)


def get(base_url: str, path: str) -> dict | None:
    try:
        with urllib.request.urlopen(f"{base_url}{path}", timeout=60) as response:
            return json.load(response)
    except urllib.error.HTTPError as error:
        if error.code == 404:
            return None
        raise


def build_request(spec: dict, defaults: dict, case: dict) -> dict:
    """Assembles one pricing request.

    `claimData` deliberately omits the five code arrays rather than sending
    them empty: CMS's validator rejects a zero-length array, so an empty list
    is a 400 and a missing key is fine.
    """
    provider = dict(defaults)
    provider.update(
        {
            "providerCcn": case["ccn"],
            "cbsaActualGeographicLocation": case["cbsa"],
            "cbsaWageIndexLocation": case["cbsa"],
            "cbsaStandardizedAmountLocation": case["cbsa"],
            "stateCode": case["stateCode"],
            "countyCode": case["countyCode"],
        }
    )
    provider.update(case.get("provider", {}))

    return {
        "claimData": {
            "providerCcn": case["ccn"],
            "diagnosisRelatedGroup": case["drg"],
            "dischargeDate": spec["dischargeDate"],
            "lengthOfStay": case["lengthOfStay"],
            "coveredDays": case["coveredDays"],
            "lifetimeReserveDays": case.get("lifetimeReserveDays", 0),
            "coveredCharges": case["coveredCharges"],
            "reviewCode": case["reviewCode"],
        },
        "providerData": provider,
    }


def row_for(name: str, response: dict, drg_entry: dict | None) -> dict:
    payment = response["paymentData"]
    extra = response["additionalCalculationVariables"]
    operating = extra["additionalOperatingVariables"]
    capital = extra["additionalCapitalVariables"]
    information = extra["additionalPaymentInformation"]

    return {
        "case": name,
        "return_code": response["returnCodeData"]["code"],
        "total_payment": payment["totalPayment"],
        "wage_index": payment["finalWageIndex"],
        "average_length_of_stay": payment["averageLengthOfStay"],
        # The DRG weight comes from the reference endpoint when the claim was
        # refused, because a refused claim reports a weight of zero and that
        # would silently become the figure the parity test prices with.
        "drg_weight": extra["drgRelativeWeight"]
        or (drg_entry or {}).get("weight"),
        "drg_weight_fraction": extra["drgRelativeWeightFraction"],
        "discharge_fraction": extra["dischargeFraction"],
        "national_labor_cost": extra["nationalLaborCost"],
        "national_non_labor_cost": extra["nationalNonLaborCost"],
        "operating_base_drg_payment": operating["operatingBaseDrgPayment"],
        "operating_federal_specific_portion": payment[
            "operatingFederalSpecificPortionPart"
        ],
        "operating_ime_adjustment": payment[
            "operatingIndirectMedicalEducationAdjustment"
        ],
        "operating_dsh_adjustment": payment[
            "operatingDisproportionateShareHospitalAdjustment"
        ],
        "operating_outlier_payment": payment["operatingOutlierPaymentPart"],
        "uncompensated_care_payment": information["uncompensatedCarePayment"],
        "capital_federal_rate": capital["capitalFederalRate"],
        "capital_geographic_adjustment_factor": capital[
            "capitalGeographicAdjustmentFactor"
        ],
        "capital_federal_specific_portion": capital["capitalFederalSpecificPortion"],
        "capital_outlier": capital["capitalCostOutlier"],
        "capital_total_payment": capital["capitalTotalPayment"],
        "cost_threshold": extra["costThreshold"],
        "hospital_specific_portion": payment["operatingHospitalSpecificPortionPart"],
        "hospital_specific_portion_percent": extra["hospitalSpecificPortionPercent"],
        "passthrough_total_plus_misc": extra["passthroughTotalPlusMisc"],
        "outlier_days": payment["outlierDays"],
        "regular_days_used": payment["regularDaysUsed"],
        "low_volume_payment": extra["lowVolumePayment"],
        "readmissions_adjustment": information[
            "hospitalReadmissionReductionAdjustmentPayment"
        ],
        "value_based_purchasing_adjustment": information[
            "valueBasedPurchasingAdjustmentPayment"
        ],
        "hospital_acquired_condition_payment": information[
            "hospitalAcquiredConditionPayment"
        ],
    }


def cell(value: object) -> str:
    if value is None:
        return ""
    return str(value)


def main(argv: list[str]) -> int:
    if len(argv) != 4:
        print(
            "usage: oracle.py <base-url> <reference-claims.json> <output.psv>",
            file=sys.stderr,
        )
        return 2

    base_url, corpus_path, output_path = argv[1], argv[2], argv[3]
    spec = json.load(open(corpus_path, encoding="utf-8"))
    year = spec["fiscalYear"]
    # Mid-January of the fiscal year: far enough inside it that no row is
    # accidentally testing a rate change at either boundary.
    spec["dischargeDate"] = f"{year}-01-15"
    defaults = spec["providerDefaults"]

    rows = []
    refusals = []
    for case in spec["cases"]:
        drg_entry = None
        entry = get(base_url, f"/v1/drg-entries/{year}/{case['drg']}")
        if entry and entry.get("tableEntries"):
            drg_entry = entry["tableEntries"][0]

        response = post(base_url, "/v2/price-claim", build_request(spec, defaults, case))
        code = response["returnCodeData"]
        rows.append(row_for(case["name"], response, drg_entry))
        if code["code"] not in ("00", "03", "06", "14", "16", "02"):
            refusals.append(f"{case['name']}: {code['code']} {code['description']}")

    with open(output_path, "w", encoding="utf-8") as out:
        out.write(
            f"# CMS IPPS pricer answers for FY{year}. Generated by "
            "tools/cms-ipps-pricer-oracle/run.sh — do not hand-edit.\n"
        )
        out.write("|".join(COLUMNS) + "\n")
        for row in rows:
            out.write("|".join(cell(row[column]) for column in COLUMNS) + "\n")

    print(f"wrote {len(rows)} rows to {output_path}")
    if refusals:
        # Not a failure. A refusal is an answer, and a corpus row that pins a
        # refusal is as useful as one that pins a payment — but an unintended
        # refusal is how a corpus quietly stops testing anything, so say so.
        print("\nrows CMS refused to price:", file=sys.stderr)
        for refusal in refusals:
            print(f"  {refusal}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
