#!/usr/bin/env python3
"""Render Cloud Health Office's Kubernetes manifests for the GKE demo.

Reads the same per-service manifests the local deploy (scripts/deploy-local.sh)
applies and writes one multi-document YAML stream to stdout with the changes a
GKE demo needs. The source manifests are not modified, so the Azure
and local paths are unaffected.

What it changes:
  - Images built by the GKE workflow are pointed at Artifact Registry.
  - Azure-only storage (azurefile-csi, ReadWriteMany) and GKE's SSD-backed
    standard-rwo become --storage-class (default cho-pd-standard, a
    standard-disk class scripts/deploy-gke.sh creates; new billing accounts
    have only 250 GB of SSD quota).
  - The Azure spot-node toleration is removed.
  - HorizontalPodAutoscalers and Secrets this deploy creates itself are dropped.
  - Services run as Development hosts, as the local deploy runs them, except
    claims-service, provider-service and fhir-service, which the local deploy
    keeps off Development (scope-validation issues) and which run here as
    deliberate in-cluster Testing hosts (CHO_TESTING=1,
    CHO_ALLOW_TESTING_ENVIRONMENT=true). Development hosts need
    CHO_ALLOW_DEV_ON_DEPLOYED_HOST=true to start on Kubernetes, so every
    Deployment gets it. Both accept the public development
    signing key (deploy/gcp/cho-service-tokens.demo.yaml), so the cluster must
    hold synthetic data only and must not be exposed publicly. The demo is
    reached by port-forward.
  - Every service loads the demo cho-service-tokens ConfigMap.
  - Replicas are set to 1 (claims-service and benefit-plan-service configurable).
  - --resource-profile small lowers CPU requests so every service fits on one
    8-vCPU node. Limits and memory requests are unchanged, so services can
    still burst; use the default profile for benchmark runs on a larger cluster.
  - Kafka and messaging settings, probe paths and per-service settings that
    scripts/deploy-local.sh patches after applying are set here up front.
  - With --documents-bucket and --documents-service-account, the services
    that store documents (DOCUMENT_SERVICES) use Google Cloud Storage: each
    gets its own Kubernetes service account bound to the Google service
    account infrastructure/gcp creates (Workload Identity), and
    DocumentStore__Provider=Gcs with the bucket. Without them they keep the
    manifests' Azure Blob settings.

Usage:
  render_manifests.py --registry REGION-docker.pkg.dev/PROJECT/cho --tag SHA \
      --kafka-bootstrap cloudhealthoffice-kafka-kafka-bootstrap.kafka.svc:9092 \
      [--ai-examiner] [--claims-replicas 2] [--benefit-plan-replicas 2] \
      [--storage-class cho-pd-standard] [--resource-profile small] \
      [--documents-bucket BUCKET --documents-service-account GSA_EMAIL] [--list-images]
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

import yaml

REPO_ROOT = Path(__file__).resolve().parents[2]

# Service manifests, in the same set scripts/deploy-local.sh applies.
EXTRA_MANIFESTS = [
    "infrastructure/k8s/services/attachment-service.yaml",
    "infrastructure/k8s/sponsor-service-deployment.yaml",
    "infrastructure/k8s/provider-verification-service-deployment.yaml",
    "k8s/idcard-service.yaml",
    "src/portal/CloudHealthOffice.Portal/k8s/portal-deployment.yaml",
]

# Images the GKE workflow builds, keyed by the image name used in manifests
# (cloudhealthoffice-<name>) and mapped to the Dockerfile that builds it.
BUILT_IMAGES = {
    "accumulator-service": "src/services/accumulator-service/Dockerfile",
    "appeals-service": "src/services/appeals-service/Dockerfile",
    "ar-service": "src/services/ar-service/Dockerfile",
    "attachment-service": "src/services/attachment-service/Dockerfile",
    "authorization-service": "src/services/authorization-service/Dockerfile",
    "benefit-plan-service": "src/services/benefit-plan-service/Dockerfile",
    "capitation-service": "src/services/capitation-service/Dockerfile",
    "claims-examiner-service": "src/services/claims-examiner-service/Dockerfile",
    "claims-service": "src/services/claims-service/Dockerfile",
    "consent-service": "src/services/consent-service/Dockerfile",
    "coverage-service": "src/services/coverage-service/Dockerfile",
    "eligibility-service": "src/services/eligibility-service/Dockerfile",
    "encounter-service": "src/services/encounter-service/Dockerfile",
    "encounter-submission-service": "src/services/encounter-submission-service/Dockerfile",
    "enrollment-import-service": "src/services/enrollment-import-service/Dockerfile",
    "fhir-service": "src/services/fhir-service/Dockerfile",
    "idcard-service": "src/services/idcard-service/Dockerfile",
    "member-document-service": "src/services/member-document-service/Dockerfile",
    "member-service": "src/services/member-service/Dockerfile",
    "payment-service": "src/services/payment-service/Dockerfile",
    "personal-representative-service": "src/services/personal-representative-service/Dockerfile",
    "portal": "src/portal/CloudHealthOffice.Portal/Dockerfile",
    "premium-billing-service": "src/services/premium-billing-service/Dockerfile",
    "pricing-api": "src/services/CloudHealthOffice.PricingApi/Dockerfile",
    "provider-contracts-service": "src/services/provider-contracts-service/Dockerfile",
    "provider-service": "src/services/provider-service/Dockerfile",
    "provider-verification-service": "src/services/provider-verification-service/Dockerfile",
    "reference-data-service": "src/services/reference-data-service/Dockerfile",
    "rfai-service": "src/services/rfai-service/Dockerfile",
    "risk-adjustment-service": "src/services/risk-adjustment-service/Dockerfile",
    "smart-auth-service": "src/services/smart-auth-service/Dockerfile",
    "sponsor-service": "src/services/sponsor-service/Dockerfile",
    "tenant-service": "src/services/tenant-service/Dockerfile",
    "terminology-service": "src/services/CHO.TerminologyService/Dockerfile",
    "token-service": "src/services/token-service/Dockerfile",
    "trading-partner-service": "src/services/trading-partner-service/Dockerfile",
    "work-management-service": "src/services/work-management-service/Dockerfile",
}

AKS_TAINT_DOMAIN = "kubernetes.azure.com"

SOURCE_REGISTRIES = ("clouhealthoffice.azurecr.io/", "ghcr.io/aurelianware/")
IMAGE_PREFIX = "cloudhealthoffice-"

# Secrets deploy-gke.sh creates. Inline copies in the manifests carry
# placeholders and would overwrite the real values, so they are dropped.
MANAGED_SECRETS = {
    "attachment-service-secret",
    "member-document-service-secret",
    "provider-verification-service-secrets",
}

# Services that store document bytes through IDocumentStore. Keep in step with
# var.document_service_accounts in infrastructure/gcp: each name is both the
# Deployment and the Kubernetes service account Workload Identity trusts.
DOCUMENT_SERVICES = ("attachment-service", "member-document-service")
WORKLOAD_IDENTITY_ANNOTATION = "iam.gke.io/gcp-service-account"

# Probe paths scripts/deploy-local.sh patches to /health/live.
READINESS_TO_LIVE = {"appeals-service", "consent-service", "personal-representative-service"}
# fhir-service: its /health includes the SMART issuer-discovery readiness
# check, which needs an external identity provider the demo doesn't have.
BOTH_PROBES_TO_LIVE = {"encounter-service", "sponsor-service", "fhir-service"}

# Kept off Development by scripts/deploy-local.sh; run as Testing here.
TESTING_HOSTS = {"claims-service", "provider-service", "fhir-service"}

# work-management-service keeps work items in step with claims-service from the
# claims.work.v1 events (Kafka). In the demo the periodic reconcile is also on,
# whatever the Kafka setting: it picks up claims pended before the service
# started (seeded data) and repairs anything an in-memory or absent bus lost.
WORK_MANAGEMENT_DEMO_ENV = {
    "WorkManagement__Reconcile__Enabled": "true",
    "WorkManagement__Reconcile__Tenants__0": "demo",
    # The portal's local demo sign-in (its CHO token subject): a primary
    # membership in every claim queue, so Get Next works without setup.
    "WorkManagement__SeedMemberUserIds__0": "local-demo-admin",
}

# Not needed in the demo: services sign their own tokens with the development
# key (Source=LocalKey) instead of fetching them from token-service.
SCALED_TO_ZERO = {"token-service"}

DEMO_ENV = {
    "CHO_TESTING": "1",
    "CHO_ALLOW_TESTING_ENVIRONMENT": "true",
    # Development hosts refuse to start on a deployed platform without this
    # (ChoTestingEnvironment). The demo is port-forward only and holds no real data.
    "CHO_ALLOW_DEV_ON_DEPLOYED_HOST": "true",
    "SecretProvider__Provider": "None",
}


# --resource-profile small: CPU request per container, in millicores.
SMALL_CPU_REQUEST_M = 50
SMALL_CPU_REQUEST_OVERRIDES_M = {"claims-service": 250}

DEFAULT_STORAGE_CLASS = "cho-pd-standard"
REPLACED_STORAGE_CLASSES = {"azurefile-csi", "azurefile", "managed-csi", "managed-csi-premium", "standard-rwo"}


def cpu_millicores(value) -> int:
    text = str(value)
    return int(float(text[:-1])) if text.endswith("m") else int(float(text) * 1000)


def host_environment(service: str) -> str:
    return "Testing" if service in TESTING_HOSTS else "Development"


def manifest_paths() -> list[Path]:
    paths = []
    for k8s_dir in sorted((REPO_ROOT / "src/services").glob("*/k8s")):
        found = sorted(list(k8s_dir.glob("*.yaml")) + list(k8s_dir.glob("*.yml")))
        if found:
            paths.append(found[0])
    paths.extend(REPO_ROOT / p for p in EXTRA_MANIFESTS)
    return paths


def rewrite_image(image: str, registry: str, tag: str) -> str:
    for source in SOURCE_REGISTRIES:
        if image.startswith(source + IMAGE_PREFIX):
            name = image[len(source) + len(IMAGE_PREFIX):].split(":", 1)[0].split("@", 1)[0]
            if name in BUILT_IMAGES:
                return f"{registry}/{IMAGE_PREFIX}{name}:{tag}"
    return image


def set_env(container: dict, name: str, value: str) -> None:
    env = container.setdefault("env", [])
    for entry in env:
        if entry.get("name") == name:
            entry.pop("valueFrom", None)
            entry["value"] = value
            return
    env.append({"name": name, "value": value})


def pod_spec(doc: dict) -> dict | None:
    if doc.get("kind") in ("Deployment", "StatefulSet", "Job"):
        return doc.get("spec", {}).get("template", {}).get("spec")
    return None


def fix_storage(doc: dict, storage_class: str) -> None:
    def fix(spec: dict) -> None:
        if spec.get("storageClassName", "standard-rwo") in REPLACED_STORAGE_CLASSES:
            spec["storageClassName"] = storage_class
        modes = spec.get("accessModes")
        if modes and "ReadWriteMany" in modes:
            spec["accessModes"] = ["ReadWriteOnce"]

    if doc.get("kind") == "PersistentVolumeClaim":
        fix(doc.get("spec", {}))
    for template in doc.get("spec", {}).get("volumeClaimTemplates", []) or []:
        fix(template.get("spec", {}))


def transform(doc: dict, args: argparse.Namespace) -> dict | None:
    kind = doc.get("kind")
    name = doc.get("metadata", {}).get("name", "")

    if kind in ("HorizontalPodAutoscaler", "Ingress"):
        return None
    if kind == "Secret" and name in MANAGED_SECRETS:
        return None

    fix_storage(doc, args.storage_class)

    if kind == "ConfigMap":
        data = doc.get("data") or {}
        if "ASPNETCORE_ENVIRONMENT" in data:
            data["ASPNETCORE_ENVIRONMENT"] = host_environment(name.removesuffix("-config"))
        for key, value in list(data.items()):
            if key == "Messaging__Backend" and value == "Auto":
                data[key] = "InMemory"
        if name == "fhir-service-config":
            # The manifest labels its data production PHI; the demo cluster
            # holds synthetic members only, and its FHIR responses must say so.
            data["FhirAdapters__DataClassification"] = "synthetic"
        if name == "smart-auth-service-config":
            data["SmartAuth__DevMode"] = "true"
            # Placeholder CIDRs fail parsing at startup; there is no ingress proxy in the demo.
            for key in [k for k in data if k.startswith("SmartAuth__TrustedProxyNetworks__")]:
                del data[key]
        return doc

    spec = pod_spec(doc)
    if spec is None:
        return doc

    if kind == "Deployment":
        replicas = 1
        if name == "claims-service":
            replicas = args.claims_replicas
        elif name == "benefit-plan-service":
            replicas = args.benefit_plan_replicas
        elif name == "claims-examiner-service":
            replicas = 1 if args.ai_examiner else 0
        elif name in SCALED_TO_ZERO:
            replicas = 0
        doc["spec"]["replicas"] = replicas

    # Drop tolerations for AKS node taints (keys in the kubernetes.azure.com
    # domain, e.g. the spot scale-set taint). Compare the key's domain part
    # exactly rather than by substring.
    tolerations = [
        t for t in spec.get("tolerations", []) or []
        if str(t.get("key", "")).split("/", 1)[0] != AKS_TAINT_DOMAIN
    ]
    if tolerations:
        spec["tolerations"] = tolerations
    else:
        spec.pop("tolerations", None)

    for container in spec.get("containers", []) + spec.get("initContainers", []):
        container["image"] = rewrite_image(container.get("image", ""), args.registry, args.tag)
        container["imagePullPolicy"] = "IfNotPresent"

    if kind != "Deployment":
        return doc

    for container in spec.get("containers", []):
        set_env(container, "ASPNETCORE_ENVIRONMENT", host_environment(name))
        for key, value in DEMO_ENV.items():
            set_env(container, key, value)
        if name != "portal":
            env_from = container.setdefault("envFrom", [])
            if not any(e.get("configMapRef", {}).get("name") == "cho-service-tokens" for e in env_from):
                env_from.append({"configMapRef": {"name": "cho-service-tokens"}})
        if args.kafka_bootstrap:
            set_env(container, "Kafka__BootstrapServers", args.kafka_bootstrap)
        for entry in container.get("env", []) or []:
            if entry.get("name") == "Messaging__Backend" and entry.get("value") == "Auto":
                entry["value"] = "InMemory"

        if name in READINESS_TO_LIVE and container.get("readinessProbe", {}).get("httpGet"):
            container["readinessProbe"]["httpGet"]["path"] = "/health/live"
        if name in BOTH_PROBES_TO_LIVE:
            for probe in ("livenessProbe", "readinessProbe"):
                if container.get(probe, {}).get("httpGet"):
                    container[probe]["httpGet"]["path"] = "/health/live"

        if args.resource_profile == "small":
            requests = container.setdefault("resources", {}).setdefault("requests", {})
            target = SMALL_CPU_REQUEST_OVERRIDES_M.get(name, SMALL_CPU_REQUEST_M)
            if "cpu" not in requests or cpu_millicores(requests["cpu"]) > target:
                requests["cpu"] = f"{target}m"

        if name == "claims-service":
            set_env(container, "Adjudication__Enforcement__AiMode", "BestEffort" if args.ai_examiner else "Disabled")
        if name == "work-management-service":
            for key, value in WORK_MANAGEMENT_DEMO_ENV.items():
                set_env(container, key, value)
        if name == "portal":
            set_env(container, "XDG_DATA_HOME", "/tmp")
        if name in DOCUMENT_SERVICES and args.documents_bucket:
            spec["serviceAccountName"] = name
            set_env(container, "DocumentStore__Provider", "Gcs")
            set_env(container, "DocumentStore__Gcs__Bucket", args.documents_bucket)
        if name == "smart-auth-service":
            # The only service whose manifest and Development settings name no
            # service-token client id; the demo token config requires one.
            set_env(container, "ChoAuth__ServiceToken__ClientId", "smart-auth-service")

    return doc


def document_service_accounts(args: argparse.Namespace) -> list[dict]:
    """Kubernetes service accounts for DOCUMENT_SERVICES, bound to the Google one."""
    return [
        {
            "apiVersion": "v1",
            "kind": "ServiceAccount",
            "metadata": {
                "name": name,
                "namespace": "cloudhealthoffice",
                "annotations": {WORKLOAD_IDENTITY_ANNOTATION: args.documents_service_account},
            },
        }
        for name in DOCUMENT_SERVICES
    ]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--registry", help="Artifact Registry prefix, e.g. us-west1-docker.pkg.dev/PROJECT/cho")
    parser.add_argument("--tag", default="latest")
    parser.add_argument("--kafka-bootstrap", default="")
    parser.add_argument("--ai-examiner", action="store_true", help="Run the AI claims examiner (needs ANTHROPIC_API_KEY)")
    parser.add_argument("--claims-replicas", type=int, default=2)
    parser.add_argument("--benefit-plan-replicas", type=int, default=2)
    parser.add_argument("--storage-class", default=DEFAULT_STORAGE_CLASS,
                        help="StorageClass for the services' volumes (default: %(default)s)")
    parser.add_argument("--resource-profile", choices=("default", "small"), default="default",
                        help="small lowers CPU requests to fit one 8-vCPU node")
    parser.add_argument("--documents-bucket", default="",
                        help="Google Cloud Storage bucket for documents (terraform output documents_bucket)")
    parser.add_argument("--documents-service-account", default="",
                        help="Google service account the document services act as (terraform output documents_service_account)")
    parser.add_argument("--list-images", action="store_true", help="Print 'name dockerfile' for each image to build, then exit")
    args = parser.parse_args()

    if args.list_images:
        for name, dockerfile in sorted(BUILT_IMAGES.items()):
            print(f"{name} {dockerfile}")
        return 0
    if not args.registry:
        parser.error("--registry is required")
    if bool(args.documents_bucket) != bool(args.documents_service_account):
        parser.error("--documents-bucket and --documents-service-account go together")

    rendered = document_service_accounts(args) if args.documents_bucket else []
    for path in manifest_paths():
        if not path.exists():
            print(f"warning: manifest not found: {path.relative_to(REPO_ROOT)}", file=sys.stderr)
            continue
        for doc in yaml.safe_load_all(path.read_text()):
            if not doc:
                continue
            out = transform(doc, args)
            if out is not None:
                rendered.append(out)

    yaml.safe_dump_all(rendered, sys.stdout, sort_keys=False)
    return 0


if __name__ == "__main__":
    sys.exit(main())
