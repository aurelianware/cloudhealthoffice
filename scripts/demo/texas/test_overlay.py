"""demo.sh overlay against the deployments the GKE deploy renders.

Runs `demo.sh overlay` with a stub kubectl that records each patch, applies
the patches to deploy/gcp/render_manifests.py's output the way a
strategic-merge patch does (lists merged by name, null removes a key), and
checks the result is a Deployment the API server accepts: no env entry with
both value and valueFrom (the failure of the first live `up`).

    python3 -m pytest scripts/demo/texas/test_overlay.py
"""

import json
import os
import subprocess
import sys
from pathlib import Path

import pytest

yaml = pytest.importorskip("yaml")

ROOT = Path(__file__).resolve().parents[3]
DEMO = ROOT / "scripts/demo/texas/demo.sh"


def _patches(tmp_path):
    log = tmp_path / "patches.jsonl"
    stub = tmp_path / "bin" / "kubectl"
    stub.parent.mkdir()
    stub.write_text(
        "#!/usr/bin/env bash\n"
        "if [[ $1 == patch ]]; then\n"
        "  for a in \"$@\"; do last=\"$a\"; done\n"
        f"  printf '%s\\t%s\\n' \"$2\" \"$(printf '%s' \"$last\" | tr -d '\\n')\" >> {log}\n"
        "fi\n")
    stub.chmod(0o755)
    env = dict(os.environ, PATH=f"{stub.parent}:{os.environ['PATH']}")
    subprocess.run(["bash", str(DEMO), "overlay"], check=True, env=env, capture_output=True)
    out = {}
    for line in log.read_text().splitlines():
        target, body = line.split("\t", 1)
        out[target.split("/", 1)[1]] = json.loads(body)
    return out


def _deployments():
    rendered = subprocess.run(
        [sys.executable, str(ROOT / "deploy/gcp/render_manifests.py"), "--registry", "r/p/cho", "--tag", "t",
         "--kafka-bootstrap", "k:9092", "--resource-profile", "small"],
        check=True, capture_output=True, text=True, cwd=ROOT).stdout
    return {d["metadata"]["name"]: d for d in yaml.safe_load_all(rendered)
            if d and d.get("kind") == "Deployment"}


def _merge_named(base: list, patch: list) -> list:
    by_name = {item["name"]: item for item in base}
    for item in patch:
        current = by_name.get(item["name"])
        if current is None:
            base.append({k: v for k, v in item.items() if v is not None})
            continue
        for key, value in item.items():
            if value is None:
                current.pop(key, None)
            elif isinstance(value, list) and isinstance(current.get(key), list):
                _merge_named(current[key], value)
            else:
                current[key] = value
    return base


def test_every_patch_targets_a_rendered_container(tmp_path):
    deployments = _deployments()
    for name, patch in _patches(tmp_path).items():
        spec = patch["spec"]["template"]["spec"]
        rendered = {c["name"] for c in deployments[name]["spec"]["template"]["spec"]["containers"]}
        assert {c["name"] for c in spec["containers"]} <= rendered


def test_patched_env_never_has_both_value_and_value_from(tmp_path):
    deployments = _deployments()
    for name, patch in _patches(tmp_path).items():
        pod = deployments[name]["spec"]["template"]["spec"]
        spec = patch["spec"]["template"]["spec"]
        _merge_named(pod.setdefault("volumes", []), spec.get("volumes", []))
        _merge_named(pod["containers"], spec["containers"])
        for container in pod["containers"]:
            for entry in container.get("env", []):
                assert not ("value" in entry and "valueFrom" in entry), f"{name}: {entry}"
            mounts = {m["name"] for m in container.get("volumeMounts", [])}
            assert mounts <= {v["name"] for v in pod.get("volumes", [])}, name


def test_the_texas_settings_land(tmp_path):
    deployments = _deployments()
    patches = _patches(tmp_path)
    claims = deployments["claims-service"]["spec"]["template"]["spec"]
    _merge_named(claims["containers"], patches["claims-service"]["spec"]["template"]["spec"]["containers"])
    env = {e["name"]: e.get("value") for e in claims["containers"][0]["env"]}
    assert env["CpcdsDrop__Enabled"] == "true" and env["Adjudication__Shadow__Enabled"] == "true"
    auth = patches["authorization-service"]["spec"]["template"]["spec"]["containers"][0]["env"]
    assert {"name": "PriorAuthDeadlines__TexasMedicaid__Tenants__0", "value": "demo", "valueFrom": None} in auth
