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
    assert "provider-verification-service-secrets" not in secrets
    assert not [d for d in docs if d["kind"] == "Ingress"]
    assert not [d for d in docs if d["kind"] == "HorizontalPodAutoscaler"]


def test_host_environments():
    deps = deployments(render())
    for name in ("claims-service", "provider-service", "fhir-service"):
        assert env(deps[name])["ASPNETCORE_ENVIRONMENT"] == "Testing", name
    assert env(deps["member-service"])["ASPNETCORE_ENVIRONMENT"] == "Development"
    assert env(deps["portal"])["ASPNETCORE_ENVIRONMENT"] == "Development"
    for name, dep in deps.items():
        # Development (and Testing) hosts refuse to start in a pod without their override.
        assert env(dep)["CHO_ALLOW_DEV_ON_DEPLOYED_HOST"] == "true", name
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


def test_each_deployment_is_rendered_once():
    names = [d["metadata"]["name"] for d in render() if d["kind"] == "Deployment"]
    assert len(names) == len(set(names)), sorted(n for n in names if names.count(n) > 1)


def test_smart_auth_and_fhir_start_without_external_identity():
    docs = render()
    config = next(d for d in docs if d["kind"] == "ConfigMap" and d["metadata"]["name"] == "smart-auth-service-config")
    assert not [k for k in config["data"] if k.startswith("SmartAuth__TrustedProxyNetworks__")]
    deps = deployments(docs)
    assert env(deps["smart-auth-service"])["ChoAuth__ServiceToken__ClientId"] == "smart-auth-service"
    fhir = deps["fhir-service"]["spec"]["template"]["spec"]["containers"][0]
    for probe in ("livenessProbe", "readinessProbe"):
        if fhir.get(probe, {}).get("httpGet"):
            assert fhir[probe]["httpGet"]["path"] == "/health/live"


def test_volumes_use_standard_disk_by_default():
    docs = render()
    pvc = next(d for d in docs if d["kind"] == "PersistentVolumeClaim")
    assert pvc["spec"]["storageClassName"] == "cho-pd-standard"
    for d in docs:
        for t in d.get("spec", {}).get("volumeClaimTemplates", []) or []:
            assert t["spec"]["storageClassName"] == "cho-pd-standard", d["metadata"]["name"]


def test_small_profile_fits_one_node():
    def millicores(v):
        v = str(v)
        return int(float(v[:-1])) if v.endswith("m") else int(float(v) * 1000)

    docs = render("--resource-profile", "small", "--claims-replicas", "1", "--benefit-plan-replicas", "1")
    total = 0
    for d in docs:
        if d["kind"] not in ("Deployment", "StatefulSet"):
            continue
        for c in d["spec"]["template"]["spec"]["containers"]:
            total += d["spec"].get("replicas", 1) * millicores(c["resources"]["requests"]["cpu"])
    # Leaves room on an e2-standard-8 (~7.9 vCPU allocatable) for MongoDB,
    # Kafka, Redis and GKE system pods.
    assert total <= 3000, total
    deps = deployments(docs)
    assert deps["claims-service"]["spec"]["template"]["spec"]["containers"][0]["resources"]["requests"]["cpu"] == "250m"
    # Limits are untouched, so services can still burst.
    assert deps["claims-service"]["spec"]["template"]["spec"]["containers"][0]["resources"]["limits"]["cpu"] == "4000m"


def test_default_profile_keeps_manifest_requests():
    deps = deployments(render())
    assert deps["claims-service"]["spec"]["template"]["spec"]["containers"][0]["resources"]["requests"]["cpu"] == "1000m"


def test_pricing_api_reads_mongo_from_its_own_section():
    # Program.cs overwrites MongoDb:* from PricingApi:*, so the connection
    # string must arrive as PricingApi__MongoConnectionString.
    names = set(env(deployments(render())["pricing-api"]))
    assert "PricingApi__MongoConnectionString" in names
    assert "MongoDb__ConnectionString" not in names


def test_work_management_service_is_built_and_deployed():
    root = SCRIPT.parents[2]
    listed = subprocess.run([sys.executable, str(SCRIPT), "--list-images"],
                            check=True, capture_output=True, text=True).stdout.split("\n")
    assert "work-management-service src/services/work-management-service/Dockerfile" in listed
    assert (root / "src/services/work-management-service/Dockerfile").is_file()

    docs = render()
    dep = deployments(docs)["work-management-service"]
    container = dep["spec"]["template"]["spec"]["containers"][0]
    assert container["image"] == f"{REGISTRY}/cloudhealthoffice-work-management-service:abc123"
    assert env(dep)["ASPNETCORE_ENVIRONMENT"] == "Development"
    assert env(dep)["Kafka__BootstrapServers"] == "kafka:9092"
    # The demo reconciles periodically even with Kafka (seeded pends, lost events).
    assert env(dep)["WorkManagement__Reconcile__Enabled"] == "true"
    assert env(dep)["WorkManagement__Reconcile__Tenants__0"] == "demo"
    assert env(dep)["WorkManagement__SeedMemberUserIds__0"] == "local-demo-admin"
    service = next(d for d in docs if d["kind"] == "Service" and d["metadata"]["name"] == "work-management-service")
    assert service["spec"]["ports"][0]["port"] == 80
    assert service["spec"]["ports"][0]["targetPort"] == 8080


def test_portal_knows_the_work_management_service():
    docs = render()
    config = next(d for d in docs if d["kind"] == "ConfigMap" and d["metadata"]["name"] == "portal-config")
    assert config["data"]["Services__WorkManagementService"] == "http://work-management-service.cloudhealthoffice/api/v1"
