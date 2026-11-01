"""Tests for deploy/gcp/hosted/apply-ingress.sh, with a stub kubectl.

Run: python3 -m unittest deploy/gcp/hosted/test_apply_ingress.py
"""
import os
import stat
import subprocess
import tempfile
import unittest
from pathlib import Path

import yaml

REPO = Path(__file__).resolve().parents[3]
SCRIPT = REPO / "deploy/gcp/hosted/apply-ingress.sh"

STUB = """#!/usr/bin/env bash
log="$STUB_DIR/calls.log"
echo "$*" >> "$log"
case "$1" in
  config) echo "${STUB_CONTEXT:-gke_cho-hosted-prod_us-central1_cho-hosted}" ;;
  get) [[ -f "$STUB_DIR/secret" ]] || exit 1 ;;
  apply)
    input="$(cat)"
    n=$(ls "$STUB_DIR" | grep -c '^applied-' || true)
    printf '%s' "$input" > "$STUB_DIR/applied-$n.yaml"
    [[ "$input" == *'"name": "iap-oauth"'* ]] && touch "$STUB_DIR/secret"
    ;;
esac
exit 0
"""

ENV = {
    "PORTAL_HOST": "portal.plan.example",
    "FHIR_HOST": "fhir.plan.example",
    "AUTH_HOST": "auth.plan.example",
    "ADDRESS_NAME": "cho-hosted-ingress",
    "SSL_POLICY": "cho-hosted-tls",
    "SECURITY_POLICY": "cho-hosted-edge",
}


class ApplyIngressTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.stub = Path(self.dir.name)
        kubectl = self.stub / "kubectl"
        kubectl.write_text(STUB)
        kubectl.chmod(kubectl.stat().st_mode | stat.S_IEXEC)

    def tearDown(self):
        self.dir.cleanup()

    def run_script(self, **overrides):
        env = {**os.environ, **ENV, "STUB_DIR": str(self.stub), "PATH": f"{self.stub}:{os.environ['PATH']}"}
        env.update(overrides)
        return subprocess.run(["bash", str(SCRIPT)], cwd=REPO, env=env, capture_output=True, text=True)

    def applied(self):
        return [p.read_text() for p in sorted(self.stub.glob("applied-*.yaml"))]

    def calls(self):
        log = self.stub / "calls.log"
        return log.read_text() if log.exists() else ""

    def test_fills_every_value_and_puts_iap_on_the_portal_only(self):
        result = self.run_script(IAP_CLIENT_ID="client-1", IAP_CLIENT_SECRET="s3cret")
        self.assertEqual(result.returncode, 0, result.stderr)

        docs = [d for d in yaml.safe_load_all(self.applied()[-1]) if d]
        kinds = {d["kind"]: d for d in docs if d["kind"] != "BackendConfig"}
        backends = {d["metadata"]["name"]: d for d in docs if d["kind"] == "BackendConfig"}
        self.assertNotIn("${", self.applied()[-1])
        self.assertEqual(kinds["ManagedCertificate"]["spec"]["domains"],
                         ["portal.plan.example", "fhir.plan.example", "auth.plan.example"])
        self.assertEqual(kinds["FrontendConfig"]["spec"]["sslPolicy"], "cho-hosted-tls")
        self.assertEqual(kinds["Ingress"]["metadata"]["annotations"]["kubernetes.io/ingress.global-static-ip-name"],
                         "cho-hosted-ingress")
        self.assertTrue(backends["cho-portal"]["spec"]["iap"]["enabled"])
        self.assertNotIn("iap", backends["cho-public-api"]["spec"])
        for b in backends.values():
            self.assertEqual(b["spec"]["securityPolicy"]["name"], "cho-hosted-edge")

        calls = self.calls()
        self.assertIn('annotate service portal', calls)
        self.assertIn('"default": "cho-portal"', calls)
        self.assertIn('annotate service fhir-service', calls)
        self.assertNotIn("s3cret", calls)  # the secret goes over stdin, never as an argument

    def test_refuses_the_demo_cluster(self):
        result = self.run_script(STUB_CONTEXT="gke_cho-demo_us-west1-a_cho-demo")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("demo cluster", result.stderr)
        self.assertEqual(self.applied(), [])

    def test_needs_the_iap_oauth_client_once(self):
        result = self.run_script()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("iap-oauth is missing", result.stderr)

    def test_refuses_a_host_name_that_is_not_one(self):
        result = self.run_script(FHIR_HOST="https://fhir.plan.example/path")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("FHIR_HOST", result.stderr)


if __name__ == "__main__":
    unittest.main()
