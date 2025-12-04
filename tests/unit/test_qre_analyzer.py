import json
import sys
from pathlib import Path
from typing import Iterable, Optional, Sequence

import pytest

ANALYZER_DIR = Path(__file__).resolve().parents[2] / "scripts" / "qre-analyzer"
MODULE_PATH = ANALYZER_DIR / "x12_278_qre_analyzer.py"

if str(ANALYZER_DIR) not in sys.path:
    sys.path.insert(0, str(ANALYZER_DIR))

spec = None
module = None
if MODULE_PATH.exists():
    import importlib.util

    spec = importlib.util.spec_from_file_location("x12_278_qre_analyzer", MODULE_PATH)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(module)
else:  # pragma: no cover - defensive check
    raise FileNotFoundError(f"Expected analyzer module at {MODULE_PATH}")

X12_278_QRE_Analyzer = module.X12_278_QRE_Analyzer


def load_config(overrides: Optional[dict] = None) -> dict:
    config_path = ANALYZER_DIR / "qre-analyzer.config.json"
    with config_path.open("r", encoding="utf-8") as handle:
        config = json.load(handle)
    if overrides:
        for key, value in overrides.items():
            if isinstance(value, dict) and key in config:
                config[key].update(value)
            else:
                config[key] = value
    return config


def write_config(tmp_path: Path, overrides: Optional[dict] = None) -> Path:
    config = load_config(overrides)
    path = tmp_path / "qre-config.json"
    path.write_text(json.dumps(config), encoding="utf-8")
    return path


BASE_SEGMENTS: Sequence[str] = (
    "ISA*00*          *00*          *ZZ*SENDER         *ZZ*RECEIVER       *210101*1253*^*00501*000000905*0*T*:~",
    "GS*HI*SENDER*RECEIVER*20210101*1253*1*X*005010X215~",
    "ST*278*0001*005010X215~",
    "BHT*0007*13*AUTHREQ*20210101*1253*13~",
    "HL*1**20*1~",
    "NM1*IL*1*DOE*JOHN***MI*MEM123456~",
    "TRN*1*TRACK12345*901234567~",
    "UM*HS*I~",
    "HCR*I1*AA~",
    "REF*D9*AUTH12345~",
    "DMG*D8*19850615~",
    "SE*11*0001~",
    "GE*1*1~",
    "IEA*1*000000905~",
)


def build_edi(
    remove: Optional[Iterable[str]] = None,
    replacements: Optional[dict] = None,
) -> str:
    remove = set(remove or ())
    replacements = replacements or {}
    segments = []
    for segment in BASE_SEGMENTS:
        seg_id = segment.split("*")[0]
        if seg_id in remove:
            continue
        if seg_id in replacements:
            replacement = replacements[seg_id]
            if replacement is None:
                continue
            segments.append(replacement)
        else:
            segments.append(segment)
    return "".join(segments)


def analyze(tmp_path: Path, edi: str, overrides: Optional[dict] = None):
    config_path = write_config(tmp_path, overrides)
    edi_path = tmp_path / "sample.edi"
    edi_path.write_text(edi, encoding="utf-8")
    analyzer = X12_278_QRE_Analyzer(str(config_path))
    report = analyzer.analyze_file(str(edi_path))
    return report


def analyze_with_analyzer(tmp_path: Path, edi: str, overrides: Optional[dict] = None):
    config_path = write_config(tmp_path, overrides)
    edi_path = tmp_path / "sample.edi"
    edi_path.write_text(edi, encoding="utf-8")
    analyzer = X12_278_QRE_Analyzer(str(config_path))
    report = analyzer.analyze_file(str(edi_path))
    return analyzer, report


def collect_codes(report) -> set:
    return {(result.code, result.severity.value) for result in report.results}


def add_pwk_segment(edi: str, pwk_segment: str = "PWK*AA*EL~") -> str:
    anchor = "HCR*I1*AA~"
    if anchor not in edi:
        raise ValueError("Anchor segment for insertion not found in EDI payload")
    return edi.replace(anchor, f"{anchor}{pwk_segment}")


def add_hsd_segment(edi: str, hsd_segment: str = "HSD*VS*6~") -> str:
    anchor = "DMG*D8*19850615~"
    if anchor not in edi:
        raise ValueError("Anchor segment for insertion not found in EDI payload")
    return edi.replace(anchor, f"{hsd_segment}{anchor}")


def test_authorization_number_query_detected(tmp_path):
    report = analyze(tmp_path, build_edi())
    assert report.is_valid is True
    assert report.error_count == 0
    assert report.query_method == "ByAuthorizationNumber"
    codes = collect_codes(report)
    assert ("QRE005", "INFO") in codes


@pytest.mark.parametrize(
    "missing_segment",
    [
        "ISA",
        "GS",
        "ST",
        "BHT",
        "HL",
        "NM1",
        "TRN",
        "UM",
        "HCR",
        "SE",
        "GE",
        "IEA",
    ],
)
def test_missing_required_segment_raises_error(tmp_path, missing_segment):
    edi = build_edi(remove={missing_segment})
    report = analyze(tmp_path, edi)
    codes = collect_codes(report)
    assert ("QRE001", "ERROR") in codes
    assert report.is_valid is False
    assert any(result.segment == missing_segment for result in report.results)


def test_member_demographics_query_detected(tmp_path):
    edi = build_edi(remove={"REF"})
    report = analyze(tmp_path, edi)
    assert report.is_valid is True
    assert report.query_method == "ByMemberDemographics"
    codes = collect_codes(report)
    assert ("QRE006", "INFO") in codes


def test_unknown_query_method_generates_warning(tmp_path):
    edi = build_edi(remove={"REF", "DMG"})
    report = analyze(tmp_path, edi)
    assert report.is_valid is True
    assert report.query_method == "Unknown"
    codes = collect_codes(report)
    assert ("QRE007", "WARNING") in codes


def test_missing_um_segment_sets_error_and_warning(tmp_path):
    edi = build_edi(remove={"UM"})
    report = analyze(tmp_path, edi)
    codes = collect_codes(report)
    assert ("QRE001", "ERROR") in codes
    assert ("QRE003", "WARNING") in codes


def test_fail_on_warnings_marks_report_invalid(tmp_path):
    replacements = {"BHT": "BHT*0006*13*AUTHREQ*20210101*1253*13~"}
    edi = build_edi(replacements=replacements)
    default_report = analyze(tmp_path, edi)
    assert default_report.is_valid is True
    overrides = {"errorHandling": {"failOnWarnings": True}}
    strict_report = analyze(tmp_path, edi, overrides=overrides)
    assert strict_report.is_valid is False
    codes = collect_codes(strict_report)
    assert ("QRE002", "WARNING") in codes


def test_bht_inquiry_code_warning(tmp_path):
    edi = build_edi(replacements={"BHT": "BHT*0006*13*AUTHREQ*20210101*1253*13~"})
    report = analyze(tmp_path, edi)
    codes = collect_codes(report)
    assert ("QRE002", "WARNING") in codes


def test_missing_file_returns_system_error(tmp_path):
    config_path = write_config(tmp_path)
    analyzer = X12_278_QRE_Analyzer(str(config_path))
    missing_path = tmp_path / "missing.edi"
    report = analyzer.analyze_file(str(missing_path))
    assert report.is_valid is False
    codes = collect_codes(report)
    assert ("SYS001", "ERROR") in codes


def test_duplicate_isa_segment_triggers_env002(tmp_path):
    duplicate_edi = build_edi() + BASE_SEGMENTS[0]
    report = analyze(tmp_path, duplicate_edi)
    codes = collect_codes(report)
    assert ("ENV002", "WARNING") in codes


def test_invalid_transaction_code_emits_env005_error(tmp_path):
    edi = build_edi(replacements={"ST": "ST*123*0001*005010X215~"})
    report = analyze(tmp_path, edi)
    codes = collect_codes(report)
    assert ("ENV005", "ERROR") in codes
    assert report.is_valid is False


def test_hcr_non_inquiry_code_prompts_qre004_info(tmp_path):
    edi = build_edi(replacements={"HCR": "HCR*ZZ*AA~"})
    report = analyze(tmp_path, edi)
    codes = collect_codes(report)
    assert ("QRE004", "INFO") in codes


def test_envelope_sender_id_mismatch_triggers_env007(tmp_path):
    edi = build_edi(
        replacements={
            "ISA": "ISA*00*          *00*          *ZZ*WRONGID       *ZZ*RECEIVER       *210101*1253*^*00501*000000905*0*T*:~"
        }
    )
    report = analyze(tmp_path, edi)
    codes = collect_codes(report)
    assert ("ENV007", "ERROR") in codes
    assert report.is_valid is False


def test_envelope_receiver_code_mismatch_triggers_env010(tmp_path):
    edi = build_edi(
        replacements={
            "GS": "GS*HI*SENDER*BADRCVR*20210101*1253*1*X*005010X215~"
        }
    )
    report = analyze(tmp_path, edi)
    codes = collect_codes(report)
    assert ("ENV010", "ERROR") in codes
    assert report.is_valid is False


def test_minimal_data_disabled_suppresses_bht_warning(tmp_path):
    overrides = {"qreRequirements": {"minimalDataPrinciple": False}}
    edi = build_edi(replacements={"BHT": "BHT*0006*13*AUTHREQ*20210101*1253*13~"})
    report = analyze(tmp_path, edi, overrides=overrides)
    codes = collect_codes(report)
    assert ("QRE002", "WARNING") not in codes


def test_attachment_required_without_pwk_triggers_att001(tmp_path):
    overrides = {
        "qreRequirements": {
            "attachmentExpectations": {
                "requireAttachmentAtSubmission": True
            }
        }
    }
    report = analyze(tmp_path, build_edi(), overrides=overrides)
    codes = collect_codes(report)
    assert ("ATT001", "ERROR") in codes
    assert report.is_valid is False


def test_attachment_allowed_report_type_passes(tmp_path):
    overrides = {
        "qreRequirements": {
            "attachmentExpectations": {
                "requireAttachmentAtSubmission": True,
                "allowedReportTypes": ["AA"]
            }
        }
    }
    edi = add_pwk_segment(build_edi(), "PWK*AA*EL~")
    report = analyze(tmp_path, edi, overrides=overrides)
    codes = collect_codes(report)
    assert all(code not in {"ATT001", "ATT002", "ATT003"} for code, _ in codes)
    assert report.is_valid is True


def test_attachment_invalid_report_type_triggers_att002(tmp_path):
    overrides = {
        "qreRequirements": {
            "attachmentExpectations": {
                "requireAttachmentAtSubmission": True,
                "allowedReportTypes": ["AA"]
            }
        }
    }
    edi = add_pwk_segment(build_edi(), "PWK*ZZ*EL~")
    report = analyze(tmp_path, edi, overrides=overrides)
    codes = collect_codes(report)
    assert ("ATT002", "ERROR") in codes
    assert report.is_valid is False


def test_service_type_outside_allowed_set_triggers_srv001(tmp_path):
    overrides = {
        "qreRequirements": {
            "serviceExpectations": {
                "allowedServiceTypeCodes": ["SC"]
            }
        }
    }
    report = analyze(tmp_path, build_edi(), overrides=overrides)
    codes = collect_codes(report)
    assert ("SRV001", "ERROR") in codes
    assert report.is_valid is False


def test_place_of_service_outside_allowed_set_triggers_srv002(tmp_path):
    overrides = {
        "qreRequirements": {
            "serviceExpectations": {
                "allowedServiceTypeCodes": ["HS"],
                "allowedPlaceOfServiceCodes": ["21", "22"]
            }
        }
    }
    edi = build_edi(replacements={"UM": "UM*HS*I*MH*99:B**E~"})
    report = analyze(tmp_path, edi, overrides=overrides)
    codes = collect_codes(report)
    assert ("SRV002", "ERROR") in codes
    assert report.is_valid is False


def test_quantity_segment_required_missing_triggers_srv003(tmp_path):
    overrides = {
        "qreRequirements": {
            "serviceExpectations": {
                "allowedServiceTypeCodes": ["HS"],
                "requireQuantitySegment": True
            }
        }
    }
    report = analyze(tmp_path, build_edi(), overrides=overrides)
    codes = collect_codes(report)
    assert ("SRV003", "ERROR") in codes
    assert report.is_valid is False


def test_quantity_type_outside_allowed_set_triggers_srv004(tmp_path):
    overrides = {
        "qreRequirements": {
            "serviceExpectations": {
                "allowedServiceTypeCodes": ["HS"],
                "allowedQuantityTypes": ["VS"],
                "requireQuantitySegment": True
            }
        }
    }
    edi = add_hsd_segment(build_edi(), "HSD*UN*6~")
    report = analyze(tmp_path, edi, overrides=overrides)
    codes = collect_codes(report)
    assert ("SRV004", "ERROR") in codes
    assert report.is_valid is False


def test_quantity_type_allowed_passes(tmp_path):
    overrides = {
        "qreRequirements": {
            "serviceExpectations": {
                "allowedServiceTypeCodes": ["HS"],
                "allowedQuantityTypes": ["VS"],
                "requireQuantitySegment": True
            }
        }
    }
    edi = add_hsd_segment(build_edi(), "HSD*VS*6~")
    report = analyze(tmp_path, edi, overrides=overrides)
    codes = collect_codes(report)
    assert all(code not in {"SRV001", "SRV002", "SRV003", "SRV004"} for code, _ in codes)
    assert report.is_valid is True


def test_is_auth_required_enabled_without_endpoint_triggers_api001(tmp_path):
    overrides = {
        "qreRequirements": {
            "apiExpectations": {
                "isAuthRequired": {
                    "enabled": True,
                    "endpoint": ""
                }
            }
        }
    }
    report = analyze(tmp_path, build_edi(), overrides=overrides)
    codes = collect_codes(report)
    assert ("API001", "ERROR") in codes
    assert report.is_valid is False


def test_provider_search_missing_endpoint_triggers_api002(tmp_path):
    overrides = {
        "qreRequirements": {
            "apiExpectations": {
                "providerSearch": {
                    "enabled": True,
                    "endpoint": ""
                }
            }
        }
    }
    report = analyze(tmp_path, build_edi(), overrides=overrides)
    codes = collect_codes(report)
    assert ("API002", "ERROR") in codes
    assert report.is_valid is False


def test_provider_search_missing_unique_id_triggers_api003(tmp_path):
    overrides = {
        "qreRequirements": {
            "apiExpectations": {
                "providerSearch": {
                    "enabled": True,
                    "endpoint": "https://example.com/provider",
                    "requiresUniqueId": True,
                    "uniqueIdField": ""
                }
            }
        }
    }
    report = analyze(tmp_path, build_edi(), overrides=overrides)
    codes = collect_codes(report)
    assert ("API003", "ERROR") in codes
    assert report.is_valid is False


def test_epa_missing_routing_endpoint_triggers_api004(tmp_path):
    overrides = {
        "qreRequirements": {
            "apiExpectations": {
                "epa": {
                    "enabled": True,
                    "requiresRoutingConfig": True,
                    "routingEndpoint": ""
                }
            }
        }
    }
    report = analyze(tmp_path, build_edi(), overrides=overrides)
    codes = collect_codes(report)
    assert ("API004", "ERROR") in codes
    assert report.is_valid is False


def test_api_expectations_satisfied_passes(tmp_path):
    overrides = {
        "qreRequirements": {
            "apiExpectations": {
                "isAuthRequired": {
                    "enabled": True,
                    "endpoint": "https://example.com/iar"
                },
                "providerSearch": {
                    "enabled": True,
                    "endpoint": "https://example.com/provider",
                    "requiresUniqueId": True,
                    "uniqueIdField": "providerId"
                },
                "epa": {
                    "enabled": True,
                    "requiresRoutingConfig": True,
                    "routingEndpoint": "https://example.com/epa"
                }
            }
        }
    }
    report = analyze(tmp_path, build_edi(), overrides=overrides)
    codes = collect_codes(report)
    assert all(code not in {"API001", "API002", "API003", "API004"} for code, _ in codes)
    assert report.is_valid is True


def test_export_report_json_persists_results(tmp_path):
    analyzer, report = analyze_with_analyzer(tmp_path, build_edi())
    output_path = tmp_path / "report.json"
    analyzer.export_report_json(report, str(output_path))
    data = json.loads(output_path.read_text(encoding="utf-8"))
    assert data["query_method"] == "ByAuthorizationNumber"
    assert any(result["code"] == "QRE005" for result in data["results"])
