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


def collect_codes(report) -> set:
    return {(result.code, result.severity.value) for result in report.results}


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
