"""Checks for deploy/gcp/render_manifests.py. Run: python3 -m pytest deploy/gcp"""

import subprocess
import sys
from pathlib import Path

import yaml

SCRIPT = Path(__file__).with_name("render_manifests.py")
REGISTRY = "us-west1-docker.pkg.dev/demo-project/cho"


def render(*extra: str) -> list[dict]:
    out = subprocess.run(
        [sys.executable, str(SCRIPT), "--registry", REGISTRY, "--tag", "abc123",
         "--kafka-bootstrap", "kafka:9092", *extra],
        check=True, capture_output=True, text=True,
    ).stdout
    return [d for d in yaml.safe_load_all(out) if d]


def deployments(docs):
    return {d["metadata"]["name"]: d for d in docs if d["kind"] == "Deployment"}


def env(deployment):
    container = deployment["spec"]["template"]["spec"]["containers"][0]
    return {e["name"]: e.get("value") for e in container.get("env", [])}


def test_no_azure_specific_settings_remain():
    text = yaml.safe_dump_all(render())
    for marker in ("azurecr.io", "azurefile-csi", "managed-csi", "kubernetes.azure.com/", "ReadWriteMany"):
        assert marker not in text, marker


def test_service_images_come_from_artifact_registry():
    for name, dep in deployments(render()).items():
        for container in dep["spec"]["template"]["spec"]["containers"]:
            image = container["image"]
            if "cloudhealthoffice-" in image:
                assert image.startswith(REGISTRY + "/cloudhealthoffice-"), (name, image)
                assert image.endswith(":abc123"), (name, image)


def test_every_listed_image_has_a_dockerfile():
    root = SCRIPT.parents[2]
    listed = subprocess.run([sys.executable, str(SCRIPT), "--list-images"],
                            check=True, capture_output=True, text=True).stdout.split("\n")
    for line in filter(None, listed):
        name, dockerfile = line.split()
        assert (root / dockerfile).is_file(), (name, dockerfile)


def test_managed_secrets_and_autoscalers_are_dropped():
    docs = render()
    secrets = {d["metadata"]["name"] for d in docs if d["kind"] == "Secret"}
    assert "attachment-service-secret" not in secrets
    assert "member-document-service-secret" not in secrets
    assert not [d for d in docs if d["kind"] == "HorizontalPodAutoscaler"]


def test_host_environments():
    deps = deployments(render())
    for name in ("claims-service", "provider-service", "fhir-service"):
        assert env(deps[name])["ASPNETCORE_ENVIRONMENT"] == "Testing", name
    assert env(deps["member-service"])["ASPNETCORE_ENVIRONMENT"] == "Development"
    assert env(deps["portal"])["ASPNETCORE_ENVIRONMENT"] == "Development"
    for name, dep in deps.items():
        if name == "portal":
            continue
        env_from = dep["spec"]["template"]["spec"]["containers"][0].get("envFrom", [])
        assert any(e.get("configMapRef", {}).get("name") == "cho-service-tokens" for e in env_from), name


def test_ai_examiner_is_off_unless_requested():
    off = deployments(render())
    assert off["claims-examiner-service"]["spec"]["replicas"] == 0
    assert env(off["claims-service"])["Adjudication__Enforcement__AiMode"] == "Disabled"
    on = deployments(render("--ai-examiner"))
    assert on["claims-examiner-service"]["spec"]["replicas"] == 1
    assert env(on["claims-service"])["Adjudication__Enforcement__AiMode"] == "BestEffort"


def test_kafka_bootstrap_is_set_on_every_service():
    for name, dep in deployments(render()).items():
        assert env(dep)["Kafka__BootstrapServers"] == "kafka:9092", name
