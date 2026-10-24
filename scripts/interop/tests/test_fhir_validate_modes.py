"""Exercise validation mode gating with a synthetic validator, never a conformance claim."""

import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


class ValidationModesTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        for directory in ("scripts/interop", "interop", "conformance/golden", "cache", "bin"):
            (self.root / directory).mkdir(parents=True, exist_ok=True)
        script = Path(__file__).resolve().parents[1] / "fhir-validate.sh"
        self.script = self.root / "scripts/interop/fhir-validate.sh"
        shutil.copyfile(script, self.script)
        jar = b"synthetic validator, not an HL7 jar"
        (self.root / "cache/validator_cli-test.jar").write_bytes(jar)
        (self.root / "interop/versions.json").write_text(json.dumps({"targets": [{
            "key": "hl7-validator", "pin": {"tag": "test", "asset": "fake.jar",
            "sha256": hashlib.sha256(jar).hexdigest(), "reference": "https://invalid.example/no-download"}
        }]}))
        (self.root / "conformance/golden/example.json").write_text(json.dumps({"resourceType": "Patient"}))
        (self.root / "conformance/validator-manifest.json").write_text(json.dumps({"goldens": {"example": {"igs": []}}}))
        (self.root / "conformance/validator-baseline.txt").write_text("example: Patient: Error - synthetic defect\n")
        java = self.root / "bin/java"
        java.write_text("#!/bin/sh\nprintf '%s\\n' 'Success: synthetic validator verdict'\n"
                        "if [ \"${SIMULATE_ERRORS:-1}\" = 1 ]; then\n"
                        "printf '%s\\n' '[1, 1] Patient: Error - synthetic defect'\nfi\n"
                        "exit \"${SIMULATE_EXIT:-0}\"\n")
        java.chmod(0o755)

    def run_mode(self, names=(), **settings):
        env = os.environ.copy()
        # Keep developer/CI mode settings out of the test's default case.
        env.pop("FHIR_VALIDATION_STRICT", None)
        env.pop("FHIR_TERMINOLOGY_SERVER", None)
        env.update({"PATH": str(self.root / "bin") + os.pathsep + env["PATH"],
                    "FHIR_VALIDATOR_CACHE": str(self.root / "cache"),
                    "FHIR_VALIDATE_OUT": str(self.root / "out")})
        env.update(settings)
        return subprocess.run(["bash", str(self.script), *names], env=env,
                              capture_output=True, text=True, timeout=20)

    def test_regression_mode_preserves_recorded_baseline(self):
        result = self.run_mode()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_strict_mode_cannot_disable_terminology(self):
        result = self.run_mode(FHIR_VALIDATION_STRICT="1")
        self.assertEqual(result.returncode, 2)
        self.assertIn("terminology cannot be disabled", result.stderr)

    def test_strict_mode_rejects_baselined_errors(self):
        result = self.run_mode(FHIR_VALIDATION_STRICT="1", FHIR_TERMINOLOGY_SERVER="https://tx.example/r4")
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("synthetic defect", result.stdout)

    def test_strict_mode_accepts_clean_verdict(self):
        result = self.run_mode(FHIR_VALIDATION_STRICT="1", FHIR_TERMINOLOGY_SERVER="https://tx.example/r4", SIMULATE_ERRORS="0")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_strict_mode_rejects_failed_validator_even_with_clean_text(self):
        result = self.run_mode(FHIR_VALIDATION_STRICT="1", FHIR_TERMINOLOGY_SERVER="https://tx.example/r4",
                               SIMULATE_ERRORS="0", SIMULATE_EXIT="1")
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)

    def test_invalid_mode_is_refused(self):
        self.assertEqual(self.run_mode(FHIR_VALIDATION_STRICT="yes").returncode, 2)

    def test_focused_run_does_not_compare_unselected_goldens(self):
        with (self.root / "conformance/validator-baseline.txt").open("a") as baseline:
            baseline.write("other: Patient: Error - separate fixture\n")
        result = self.run_mode(names=("example",))
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        # Full runs must still detect a stale baseline entry.
        self.assertEqual(self.run_mode().returncode, 1)


if __name__ == "__main__":
    unittest.main()
