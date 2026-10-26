#!/usr/bin/env python3
"""Initialize an EMPTY appeal-key secret once; never print key material or rotate v1.

Terraform creates the secret and narrowly scoped reader identity first. A new
logical key version must use a new named secret and an explicit migration plan.
"""
import argparse
import base64
import json
import os
import re
import secrets
import subprocess

SECRET = "appeal-body-encryption-key-v1"


def gcloud(args, payload=None):
    env = dict(os.environ)
    env.update(CLOUDSDK_CORE_LOG_HTTP="false", CLOUDSDK_CORE_VERBOSITY="error",
               CLOUDSDK_CORE_DISABLE_USAGE_REPORTING="true")
    result = subprocess.run(["gcloud", *args, "--quiet"], input=payload,
                            capture_output=True, env=env, check=False)
    if result.returncode:
        # Do not replay stderr/request data that might include a secret payload.
        raise RuntimeError("Secret Manager operation failed; no key material is displayed.")
    return result.stdout


def initialize(project):
    if not re.fullmatch(r"[a-z][a-z0-9-]{4,28}[a-z0-9]", project):
        raise ValueError("A valid existing GCP project ID is required.")
    versions = json.loads(gcloud(["secrets", "versions", "list", SECRET,
                                 "--project", project, "--format=json(name,state)"]))
    if versions:
        return False  # Refuse automatic rotation, even if old versions are disabled/destroyed.
    key = base64.b64encode(secrets.token_bytes(32))
    gcloud(["secrets", "versions", "add", SECRET, "--project", project,
            "--data-file=-", "--format=value(name)"], payload=key)
    return True


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", required=True)
    args = parser.parse_args()
    created = initialize(args.project)
    print("Initial 256-bit appeal key created." if created else "Existing key versions found; key left unchanged.")


if __name__ == "__main__":
    main()
