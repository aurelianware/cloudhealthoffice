"""The plan-hosted CPCDS trial script against stub CHO services (no real deployment)."""

import json
import os
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import subprocess
import tempfile
import threading
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / "trial.sh"


class StubCho(BaseHTTPRequestHandler):
    """Records each import call and answers with the server's canned reply for its path."""

    def do_POST(self):
        length = int(self.headers.get("Content-Length", 0))
        body = self.rfile.read(length).decode("utf-8", "replace")
        self.server.calls.append({"path": self.path, "body": body, "auth": self.headers.get("Authorization")})
        status, reply = self.server.replies.get(self.path, (200, {}))
        payload = json.dumps(reply).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def log_message(self, *args):
        pass


class TrialScriptTests(unittest.TestCase):
    def setUp(self):
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), StubCho)
        self.server.calls = []
        self.server.replies = {
            "/api/v1/enrollment/import/cpcds": (200, {"membersInFile": 2, "coverageRows": 2, "planCodes": {"unmapped": []}}),
            "/api/v1/claims/import/cpcds": (200, {"claimsInFile": 3, "created": 3, "rejected": 0}),
            "/api/v1/authorizations/import/csv": (200, {"authorizationsInFile": 1, "created": 1}),
            "/api/v1/authorizations/import/x12-278": (200, {"authorizationsInFile": 2, "created": 2}),
        }
        threading.Thread(target=self.server.serve_forever, daemon=True).start()
        self.addCleanup(self.server.shutdown)
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.folder = Path(self.temp.name) / "extracts"
        self.folder.mkdir()
        for name, content in {
            "qnxt_claims_20261101.csv": "Claim unique identifier\nC1\n",
            "CPCDS_Members.csv": "Member id\nM1\n",
            "CPCDS_Coverages.csv": "Member id\nM1\n",
            "jiva_authorizations.csv": "authorization_number\nPA-1\n",
            "um_278_20261101.x12": "ISA*00*          *00*~",
            "readme.txt": "not an extract",
        }.items():
            (self.folder / name).write_text(content)

    def run_script(self, *args, token="test-token"):
        base = f"http://127.0.0.1:{self.server.server_address[1]}"
        env = os.environ.copy()
        env.update({
            "CHO_CLAIMS_URL": base, "CHO_ENROLLMENT_URL": base, "CHO_AUTHORIZATION_URL": base,
            "CHO_SOURCE_SYSTEM": "qnxt", "CHO_LINE_OF_BUSINESS": "Medicaid",
            "CHO_TRIAL_OUT": str(Path(self.temp.name) / "out"),
        })
        env.pop("CHO_TOKEN", None)
        if token:
            env["CHO_TOKEN"] = token
        return subprocess.run(["bash", str(SCRIPT), *args], env=env, capture_output=True, text=True, timeout=30)

    def test_check_dry_runs_every_file_in_dependency_order(self):
        result = self.run_script("check", str(self.folder))

        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        paths = [c["path"] for c in self.server.calls]
        self.assertEqual(paths, [
            "/api/v1/enrollment/import/cpcds",
            "/api/v1/claims/import/cpcds",
            "/api/v1/authorizations/import/csv",
            "/api/v1/authorizations/import/x12-278",
        ])
        for call in self.server.calls:
            self.assertRegex(call["body"], r'name="dryRun"\r\n\r\ntrue')
            self.assertEqual(call["auth"], "Bearer test-token")
        enrollment = self.server.calls[0]["body"]
        self.assertIn('filename="CPCDS_Members.csv"', enrollment)
        self.assertIn('filename="CPCDS_Coverages.csv"', enrollment)
        self.assertRegex(self.server.calls[1]["body"], r'name="sourceSystem"\r\n\r\nqnxt')
        self.assertRegex(self.server.calls[1]["body"], r'name="lineOfBusiness"\r\n\r\nMedicaid')
        self.assertIn("skipped readme.txt", result.stdout)
        self.assertIn("Clean.", result.stdout)

    def test_load_writes(self):
        result = self.run_script("load", str(self.folder))

        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        for call in self.server.calls:
            self.assertRegex(call["body"], r'name="dryRun"\r\n\r\nfalse')
        self.assertIn("Loaded.", result.stdout)

    def test_refused_claims_and_unmapped_plan_codes_fail_the_check_and_say_why(self):
        self.server.replies["/api/v1/claims/import/cpcds"] = (200, {
            "claimsInFile": 3, "created": 2, "rejected": 1,
            "claims": [{"claimId": "C9", "errors": ["Member M9 is not in CHO."]}],
        })
        self.server.replies["/api/v1/enrollment/import/cpcds"] = (200, {
            "membersInFile": 2, "planCodes": {"unmapped": [{"externalPlanCode": "GOLD-PPO"}]},
        })

        result = self.run_script("check", str(self.folder))

        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("C9: Member M9 is not in CHO.", result.stdout)
        self.assertIn("unmapped plan codes: 1", result.stdout)
        self.assertIn("GOLD-PPO", result.stdout)
        self.assertIn("Some files or rows were refused", result.stdout)

    def test_an_unreadable_file_fails_the_check(self):
        self.server.replies["/api/v1/authorizations/import/csv"] = (422, {"fileErrors": ["authorization_number column is missing"]})

        result = self.run_script("check", str(self.folder))

        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("authorization_number column is missing", result.stdout)

    def test_a_token_without_permission_stops_with_a_clear_reason(self):
        self.server.replies["/api/v1/enrollment/import/cpcds"] = (403, {})

        result = self.run_script("check", str(self.folder))

        self.assertEqual(result.returncode, 2)
        self.assertIn("lacks the import permission", result.stderr)
        self.assertEqual(len(self.server.calls), 1, "nothing after a refused token")

    def test_without_a_token_nothing_is_sent(self):
        result = self.run_script("check", str(self.folder), token=None)

        self.assertEqual(result.returncode, 2)
        self.assertIn("CHO_TOKEN", result.stderr)
        self.assertEqual(self.server.calls, [])

    def test_two_member_files_are_refused_before_any_call(self):
        (self.folder / "members_2.csv").write_text("Member id\nM2\n")

        result = self.run_script("check", str(self.folder))

        self.assertEqual(result.returncode, 2)
        self.assertEqual(self.server.calls, [])


if __name__ == "__main__":
    unittest.main()
