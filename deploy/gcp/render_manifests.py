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
  - With --security production (deploy-gke.sh chooses it when the token
    signing key and Cloudflare Access settings are configured), the demo runs
    with production security instead of development keys:
      * every service is a --host-environment host (default Staging): no
        development key, LocalDemo sign-in or Testing override is accepted;
      * token-service runs, signs with the Cloud KMS key
        (--token-signing-key-version) as its Google service account
        (--token-service-google-account, Workload Identity), and issues service
        tokens to Kubernetes service accounts (--kubernetes-issuer);
      * each service runs as its own Kubernetes service account and proves
        its identity with a projected token (audience cho-token-service);
      * every service trusts only token-service's public key
        (--token-public-key-file), in a generated cho-service-tokens ConfigMap;
      * the portal signs people in through Cloudflare Access
        (--access-team-domain, --access-audience), and token-service gives
        Access users tokens for --access-tenant only.
    The default, --security demo, is the development-key demo described above.
  - With --documents-bucket and --documents-service-account, the services
    that store documents (DOCUMENT_SERVICES) use Google Cloud Storage: each
    gets its own Kubernetes service account bound to the Google service
    account infrastructure/gcp creates (Workload Identity), and
    DocumentStore__Provider=Gcs with the bucket. Without them they keep the
    manifests' Azure Blob settings.
  - With --managed-fhir-store and --managed-fhir-store-service-account,
    fhir-service publishes finalized claims' ExplanationOfBenefit to that
    Cloud Healthcare API FHIR store (ADR 023): its service account is bound
    to the Google one (Workload Identity) and ManagedFhirStore__* names the
    store and the one tenant it holds (--managed-fhir-store-tenant, default
    demo). The store streams to BigQuery by its own configuration.

Usage:
  render_manifests.py --registry REGION-docker.pkg.dev/PROJECT/cho --tag SHA \
      --kafka-bootstrap cloudhealthoffice-kafka-kafka-bootstrap.kafka.svc:9092 \
      [--ai-examiner] [--claims-replicas 2] [--benefit-plan-replicas 2] \
      [--storage-class cho-pd-standard] [--resource-profile small] \
      [--documents-bucket BUCKET --documents-service-account GSA_EMAIL] [--list-images]
      [--managed-fhir-store projects/P/locations/L/datasets/D/fhirStores/S
       --managed-fhir-store-service-account GSA_EMAIL [--managed-fhir-store-tenant demo]]
      [--security production --token-signing-key-version projects/.../cryptoKeyVersions/1
       --token-service-google-account GSA_EMAIL --kubernetes-issuer URL
       --token-public-key-file token-service.pub.pem
       --access-team-domain TEAM.cloudflareaccess.com --access-audience AUD_TAG
       [--access-tenant demo] [--host-environment Staging] [--platform-tenant-id ENTRA_TID]]
"""

from __future__ import annotations

import argparse
import json
import re
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
    "care-management-service": "src/services/care-management-service/Dockerfile",
    "contact-service": "src/services/contact-service/Dockerfile",
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
    "correspondence-service": "src/services/correspondence-service/Dockerfile",
    "coverage-service": "src/services/coverage-service/Dockerfile",
    "document-service": "src/services/document-service/Dockerfile",
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
    "correspondence-service-secret",
    "member-document-service-secret",
    "provider-verification-service-secrets",
}

# Services that store document bytes through IDocumentStore (the catalog; the
# others file through it). Keep in step with
# var.document_service_accounts in infrastructure/gcp: each name is both the
# Deployment and the Kubernetes service account Workload Identity trusts.
DOCUMENT_SERVICES = ("document-service",)
WORKLOAD_IDENTITY_ANNOTATION = "iam.gke.io/gcp-service-account"
# fhir-service's Kubernetes service account (its manifest defines it), bound
# to the managed FHIR store's Google service account when publishing is on.
MANAGED_FHIR_STORE_SERVICE = "fhir-service"
MANAGED_FHIR_STORE_ACCOUNT = "fhir-service-sa"
MANAGED_FHIR_STORE_ID = re.compile(
    r"^projects/(?P<project>[a-z][a-z0-9-]{4,28}[a-z0-9])/locations/(?P<location>[a-z0-9-]+)"
    r"/datasets/(?P<dataset>[A-Za-z0-9_.-]{1,256})/fhirStores/(?P<store>[A-Za-z0-9_.-]{1,256})$")

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


# ── Production security (--security production) ─────────────────────────────

NAMESPACE = "cloudhealthoffice"

# The projected service-account token each service presents to token-service.
TOKEN_SERVICE_AUDIENCE = "cho-token-service"
PROJECTED_TOKEN_VOLUME = "cho-token-service"
PROJECTED_TOKEN_DIR = "/var/run/secrets/cho/token-service"
PROJECTED_TOKEN_SECONDS = 3600

# Deployments that never ask token-service for a service token: the portal
# (user tokens only) and token-service itself (signs its own).
NO_SERVICE_IDENTITY = {"portal", "token-service"}

# Issuers token-service signs under, all with the one Cloud KMS key.
USER_ISSUER = "cho-token-service"
SERVICE_ISSUER = "cho-token-service-svc"
WORKLOAD_ISSUER = "cho-workload"

# Azure workload identity markers the source manifests carry; meaningless on GKE.
AZURE_WORKLOAD_IDENTITY_PREFIX = "azure.workload.identity/"

PRODUCTION_ENV = {
    "SecretProvider__Provider": "None",
    # The demo cluster holds synthetic data only: mock and synthetic-data
    # components (ChoSyntheticData) may run on these non-Development hosts.
    "SyntheticData__Allowed": "true",
}

KMS_VERSION_PATTERN = re.compile(r"^projects/[^/]+/locations/[^/]+/keyRings/[^/]+/cryptoKeys/[^/]+/cryptoKeyVersions/[0-9]+$")


# --resource-profile small: CPU request per container, in millicores.
SMALL_CPU_REQUEST_M = 50
SMALL_CPU_REQUEST_OVERRIDES_M = {"claims-service": 250}

DEFAULT_STORAGE_CLASS = "cho-pd-standard"
REPLACED_STORAGE_CLASSES = {"azurefile-csi", "azurefile", "managed-csi", "managed-csi-premium", "standard-rwo"}


def cpu_millicores(value) -> int:
    text = str(value)
    return int(float(text[:-1])) if text.endswith("m") else int(float(text) * 1000)


def production(args: argparse.Namespace) -> bool:
    return getattr(args, "security", "demo") == "production"


def host_environment(service: str, args: argparse.Namespace | None = None) -> str:
    if args is not None and production(args):
        return args.host_environment
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
    if kind == "ServiceAccount" and name == MANAGED_FHIR_STORE_ACCOUNT and args.managed_fhir_store:
        annotations = doc.setdefault("metadata", {}).get("annotations") or {}
        annotations[WORKLOAD_IDENTITY_ANNOTATION] = args.managed_fhir_store_service_account
        doc["metadata"]["annotations"] = annotations
    if kind == "ServiceAccount" and production(args):
        annotations = doc.get("metadata", {}).get("annotations") or {}
        for key in [k for k in annotations if k.startswith(AZURE_WORKLOAD_IDENTITY_PREFIX)]:
            del annotations[key]
        if name == args.token_service_account_name:
            annotations[WORKLOAD_IDENTITY_ANNOTATION] = args.token_service_google_account
        if annotations:
            doc["metadata"]["annotations"] = annotations
        else:
            doc["metadata"].pop("annotations", None)
        return doc

    fix_storage(doc, args.storage_class)

    if kind == "ConfigMap":
        data = doc.get("data") or {}
        if "ASPNETCORE_ENVIRONMENT" in data:
            data["ASPNETCORE_ENVIRONMENT"] = host_environment(name.removesuffix("-config"), args)
        if production(args):
            if name == "token-service-service-clients":
                return None  # generated from the rendered Deployments (service_clients_config)
            if name == "token-service-config":
                token_service_config(data, args)
        for key, value in list(data.items()):
            if key == "Messaging__Backend" and value == "Auto":
                data[key] = "InMemory"
        if name == "fhir-service-config":
            # The manifest labels its data production PHI; the demo cluster
            # holds synthetic members only, and its FHIR responses must say so.
            data["FhirAdapters__DataClassification"] = "synthetic"
        if name == "smart-auth-service-config":
            if production(args):
                data.pop("SmartAuth__DevMode", None)
            else:
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
        elif name in SCALED_TO_ZERO and not production(args):
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

    if production(args):
        labels = doc["spec"]["template"].setdefault("metadata", {}).setdefault("labels", {})
        for key in [k for k in labels if k.startswith(AZURE_WORKLOAD_IDENTITY_PREFIX)]:
            del labels[key]

    for container in spec.get("containers", []):
        set_env(container, "ASPNETCORE_ENVIRONMENT", host_environment(name, args))
        for key, value in (PRODUCTION_ENV if production(args) else DEMO_ENV).items():
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
                # The LocalDemo user does not exist with production security.
                if not (production(args) and key.startswith("WorkManagement__SeedMemberUserIds")):
                    set_env(container, key, value)
        if name == "portal":
            set_env(container, "XDG_DATA_HOME", "/tmp")
            set_env(container, "Services__ContactService", "http://contact-service/api")
            set_env(container, "Services__DocumentService", "http://document-service")
        if name == "contact-service":
            for downstream, setting in (("claims", "Claims"), ("appeals", "Appeals"),
                                        ("authorization", "Authorization"), ("work-management", "WorkManagement")):
                set_env(container, f"Services__{setting}ServiceUrl", f"http://{downstream}-service")
        if name in {"appeals-service", "authorization-service", "work-management-service"}:
            # Owners read the persisted intake from contact-service. Override
            # Development's localhost callback inside the GKE containers.
            set_env(container, "Services__ContactServiceUrl", "http://contact-service")
        if name == "document-service":
            set_env(container, "Documents__AllowUnscannedContent", "false")
            set_env(container, "Documents__Scanner__Provider", "ClamAv")
            set_env(container, "Documents__Scanner__ClamAv__Host", "document-scanner")
        if name in DOCUMENT_SERVICES and args.documents_bucket:
            spec["serviceAccountName"] = name
            set_env(container, "DocumentStore__Provider", "Gcs")
            set_env(container, "DocumentStore__Gcs__Bucket", args.documents_bucket)
        if name == "appeals-service" and args.appeals_secrets_project:
            spec["serviceAccountName"] = "appeals-service"
            set_env(container, "SecretProvider__Provider", "GoogleSecretManager")
            set_env(container, "SecretProvider__GoogleSecretManagerProjectId", args.appeals_secrets_project)
            set_env(container, "SecretProvider__GoogleSecretManagerSecretNames__0", "appeal-body-encryption-key-v1")
            if container.get("readinessProbe", {}).get("httpGet"):
                container["readinessProbe"]["httpGet"]["path"] = "/health/ready"
        if name == MANAGED_FHIR_STORE_SERVICE and args.managed_fhir_store:
            spec["serviceAccountName"] = MANAGED_FHIR_STORE_ACCOUNT
            for key, value in managed_fhir_store_env(args).items():
                set_env(container, key, value)
        if name == "smart-auth-service":
            # The only service whose manifest and Development settings name no
            # service-token client id; the demo token config requires one.
            set_env(container, "ChoAuth__ServiceToken__ClientId", "smart-auth-service")
        if name == "portal" and production(args):
            portal_access_settings(container, args)

    if production(args) and name not in NO_SERVICE_IDENTITY:
        add_service_identity(doc, spec, name, args)

    return doc


def env_value(container: dict, name: str) -> str | None:
    for entry in container.get("env", []) or []:
        if entry.get("name") == name and "value" in entry:
            return str(entry["value"])
    return None


def token_service_config(data: dict, args: argparse.Namespace) -> None:
    """token-service with production security on GKE (Cloud KMS, Kubernetes identity, Cloudflare Access)."""
    for key in [k for k in data if k.startswith(("TokenSigning__KeyVaultKeyId", "ServiceTokens__", "WorkloadTokens__"))]:
        del data[key]
    data.update({
        "ASPNETCORE_ENVIRONMENT": args.host_environment,
        "TokenSigning__CloudKmsKeyVersion": args.token_signing_key_version,
        "TokenSigning__Issuer": USER_ISSUER,
        "TokenSigning__Audience": "cho-api",
        # Empty turns platform administration off (no Entra directory in the demo).
        "TokenService__PlatformTenantId": args.platform_tenant_id,
        # No Argo workflows in the demo.
        "WorkloadTokens__Enabled": "false",
        "ServiceTokens__Enabled": "true",
        "ServiceTokens__IdentityProvider": "Kubernetes",
        "ServiceTokens__Kubernetes__KubernetesIssuer": args.kubernetes_issuer,
        "ServiceTokens__Kubernetes__Audience": TOKEN_SERVICE_AUDIENCE,
        "ServiceTokens__Issuer": SERVICE_ISSUER,
        "ServiceTokens__TokenLifetime": "00:05:00",
        "ServiceTokens__ServiceClientsFile": "/etc/cho/service-clients/service-clients.json",
        "CloudflareAccess__Enabled": "true",
        "CloudflareAccess__TeamDomain": args.access_team_domain,
        "CloudflareAccess__Audiences__0": args.access_audience,
    })
    for i, tenant in enumerate(args.access_tenant):
        data[f"CloudflareAccess__Tenants__{i}"] = tenant


def portal_access_settings(container: dict, args: argparse.Namespace) -> None:
    """The portal signs people in from the Cloudflare Access header; no Entra, no LocalDemo."""
    set_env(container, "Authentication__Mode", "CloudflareAccess")
    set_env(container, "Authentication__CloudflareAccess__TeamDomain", args.access_team_domain)
    set_env(container, "Authentication__CloudflareAccess__Audiences__0", args.access_audience)
    set_env(container, "Services__TokenService", "http://token-service")
    # Synthetic claims for benefit plan validation (demo data only).
    set_env(container, "Features__BenefitPlanSyntheticValidationEnabled", "true")


def add_service_identity(doc: dict, spec: dict, name: str, args: argparse.Namespace) -> None:
    """
    The service runs as its own Kubernetes service account and presents the
    projected token for token-service to obtain its service tokens. The
    registration (namespace/service account -> client id) is collected for
    token-service's registry.
    """
    account = spec.get("serviceAccountName") or f"{name}-sa"
    spec["serviceAccountName"] = account

    containers = spec.get("containers", [])
    client_id = next((v for c in containers if (v := env_value(c, "ChoAuth__ServiceToken__ClientId"))), None) or name
    for container in containers:
        set_env(container, "ChoAuth__ServiceToken__ClientId", client_id)
        set_env(container, "ChoAuth__ServiceToken__Credential", "KubernetesServiceAccount")
        set_env(container, "ChoAuth__ServiceToken__ServiceAccountTokenFile", f"{PROJECTED_TOKEN_DIR}/token")
        mounts = container.setdefault("volumeMounts", [])
        if not any(m.get("name") == PROJECTED_TOKEN_VOLUME for m in mounts):
            mounts.append({"name": PROJECTED_TOKEN_VOLUME, "mountPath": PROJECTED_TOKEN_DIR, "readOnly": True})

    volumes = spec.setdefault("volumes", [])
    if not any(v.get("name") == PROJECTED_TOKEN_VOLUME for v in volumes):
        volumes.append({
            "name": PROJECTED_TOKEN_VOLUME,
            "projected": {"sources": [{"serviceAccountToken": {
                "audience": TOKEN_SERVICE_AUDIENCE,
                "expirationSeconds": PROJECTED_TOKEN_SECONDS,
                "path": "token",
            }}]},
        })

    args.service_clients.append({"clientId": client_id, "namespace": NAMESPACE, "serviceAccount": account})


def service_clients_config(args: argparse.Namespace) -> dict:
    """token-service's registry: which service account is which service. One account per client id."""
    clients, seen_accounts, seen_ids = [], set(), set()
    for entry in args.service_clients:
        if entry["serviceAccount"] in seen_accounts or entry["clientId"] in seen_ids:
            raise SystemExit(
                f"error: service account {entry['serviceAccount']} or client id {entry['clientId']} "
                "is used by two Deployments; token-service requires one account per service")
        seen_accounts.add(entry["serviceAccount"])
        seen_ids.add(entry["clientId"])
        clients.append(entry)
    return {
        "apiVersion": "v1",
        "kind": "ConfigMap",
        "metadata": {"name": "token-service-service-clients", "namespace": NAMESPACE},
        "data": {"service-clients.json": json.dumps(clients, indent=2) + "\n"},
    }


def service_tokens_config(args: argparse.Namespace) -> dict:
    """
    Replaces deploy/gcp/cho-service-tokens.demo.yaml: services trust only
    token-service's key (one key, three issuers by kind) and obtain their
    service tokens from token-service with their projected token.
    """
    pem = args.token_public_key_pem
    return {
        "apiVersion": "v1",
        "kind": "ConfigMap",
        "metadata": {"name": "cho-service-tokens", "namespace": NAMESPACE},
        "data": {
            "ChoAuth__Audience": "cho-api",
            "ChoAuth__Issuers__0__Issuer": USER_ISSUER,
            "ChoAuth__Issuers__0__Kind": "User",
            "ChoAuth__Issuers__0__PublicKeyPem": pem,
            "ChoAuth__Issuers__1__Issuer": SERVICE_ISSUER,
            "ChoAuth__Issuers__1__Kind": "Service",
            "ChoAuth__Issuers__1__PublicKeyPem": pem,
            "ChoAuth__Issuers__2__Issuer": WORKLOAD_ISSUER,
            "ChoAuth__Issuers__2__Kind": "Workload",
            "ChoAuth__Issuers__2__PublicKeyPem": pem,
            "ChoAuth__ServiceToken__Source": "TokenService",
            "ChoAuth__ServiceToken__TokenServiceUrl": "http://token-service",
            "ChoAuth__ServiceToken__Credential": "KubernetesServiceAccount",
            "ChoAuth__ServiceToken__ServiceAccountTokenFile": f"{PROJECTED_TOKEN_DIR}/token",
        },
    }


def missing_service_accounts(rendered: list[dict]) -> list[dict]:
    """Service accounts the Deployments run as that no manifest defines."""
    defined = {d["metadata"]["name"] for d in rendered if d.get("kind") == "ServiceAccount"}
    needed = []
    for d in rendered:
        if d.get("kind") != "Deployment":
            continue
        account = (pod_spec(d) or {}).get("serviceAccountName")
        if account and account not in defined:
            defined.add(account)
            needed.append({"apiVersion": "v1", "kind": "ServiceAccount",
                           "metadata": {"name": account, "namespace": NAMESPACE}})
    return needed


def validate_production_args(parser: argparse.ArgumentParser, args: argparse.Namespace) -> None:
    required = {
        "--token-signing-key-version": args.token_signing_key_version,
        "--token-service-google-account": args.token_service_google_account,
        "--kubernetes-issuer": args.kubernetes_issuer,
        "--token-public-key-file": args.token_public_key_file,
        "--access-team-domain": args.access_team_domain,
        "--access-audience": args.access_audience,
    }
    missing = [flag for flag, value in required.items() if not value]
    if missing:
        parser.error("--security production requires " + ", ".join(missing))
    if not KMS_VERSION_PATTERN.match(args.token_signing_key_version):
        parser.error("--token-signing-key-version must be a full Cloud KMS key version name ending in /cryptoKeyVersions/<n>")
    if not args.kubernetes_issuer.startswith("https://"):
        parser.error("--kubernetes-issuer must be the cluster's https service-account issuer")
    if args.host_environment in ("Development", "Testing"):
        parser.error("--host-environment must not be Development or Testing: those hosts accept development keys")
    if not args.access_tenant or any(not t or "*" in t for t in args.access_tenant):
        parser.error("--access-tenant must name each CHO tenant explicitly")
    pem = Path(args.token_public_key_file).read_text().strip()
    if not pem.startswith("-----BEGIN PUBLIC KEY-----") or not pem.endswith("-----END PUBLIC KEY-----"):
        parser.error("--token-public-key-file must hold one PEM public key (gcloud kms keys versions get-public-key)")
    args.token_public_key_pem = pem + "\n"


def managed_fhir_store_env(args: argparse.Namespace) -> dict[str, str]:
    """fhir-service settings for publishing to a Cloud Healthcare API FHIR store."""
    match = MANAGED_FHIR_STORE_ID.match(args.managed_fhir_store)
    assert match is not None  # checked in main()
    return {
        "ManagedFhirStore__Provider": "GcpHealthcare",
        "ManagedFhirStore__Gcp__ProjectId": match["project"],
        "ManagedFhirStore__Gcp__Location": match["location"],
        "ManagedFhirStore__Gcp__DatasetId": match["dataset"],
        "ManagedFhirStore__Gcp__FhirStoreId": match["store"],
        "ManagedFhirStore__TenantId": args.managed_fhir_store_tenant,
    }


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
    parser.add_argument("--managed-fhir-store", default="",
                        help="Cloud Healthcare API FHIR store fhir-service publishes to (terraform output managed_fhir_store_id)")
    parser.add_argument("--managed-fhir-store-service-account", default="",
                        help="Google service account fhir-service publishes as (terraform output managed_fhir_store_service_account)")
    parser.add_argument("--managed-fhir-store-tenant", default="demo",
                        help="The one CHO tenant whose claims are published (default: %(default)s)")
    parser.add_argument("--appeals-secrets-project", default="", help="Project containing appeal-body-encryption-key-v1")
    parser.add_argument("--appeals-secrets-service-account", default="", help="Secret-reading Google service account for appeals-service")
    parser.add_argument("--list-images", action="store_true", help="Print 'name dockerfile' for each image to build, then exit")
    parser.add_argument("--security", choices=("demo", "production"), default="demo",
                        help="demo: development keys and LocalDemo sign-in; production: Cloud KMS, Kubernetes identity, Cloudflare Access")
    parser.add_argument("--host-environment", default="Staging",
                        help="ASPNETCORE_ENVIRONMENT for --security production (default: %(default)s)")
    parser.add_argument("--token-signing-key-version", default="", help="terraform output token_signing_key_version")
    parser.add_argument("--token-service-google-account", default="", help="terraform output token_service_google_account")
    parser.add_argument("--token-service-account-name", default="token-service-sa",
                        help="Kubernetes service account token-service runs as (default: %(default)s)")
    parser.add_argument("--kubernetes-issuer", default="", help="terraform output kubernetes_service_account_issuer")
    parser.add_argument("--token-public-key-file", default="", help="PEM public key of the token signing key version")
    parser.add_argument("--access-team-domain", default="", help="Cloudflare Zero Trust team domain")
    parser.add_argument("--access-audience", default="", help="Cloudflare Access application AUD tag")
    parser.add_argument("--access-tenant", action="append", default=None,
                        help="CHO tenant that accepts Cloudflare Access sign-in (repeatable; default demo)")
    parser.add_argument("--platform-tenant-id", default="", help="Entra directory of platform administrators (default: none)")
    args = parser.parse_args()
    args.service_clients = []
    if args.access_tenant is None:
        args.access_tenant = ["demo"]

    if args.list_images:
        for name, dockerfile in sorted(BUILT_IMAGES.items()):
            print(f"{name} {dockerfile}")
        return 0
    if not args.registry:
        parser.error("--registry is required")
    if bool(args.appeals_secrets_project) != bool(args.appeals_secrets_service_account):
        parser.error("--appeals-secrets-project and --appeals-secrets-service-account go together")
    if bool(args.documents_bucket) != bool(args.documents_service_account):
        parser.error("--documents-bucket and --documents-service-account go together")
    if bool(args.managed_fhir_store) != bool(args.managed_fhir_store_service_account):
        parser.error("--managed-fhir-store and --managed-fhir-store-service-account go together")
    if args.managed_fhir_store and not MANAGED_FHIR_STORE_ID.match(args.managed_fhir_store):
        parser.error("--managed-fhir-store must be projects/P/locations/L/datasets/D/fhirStores/S")
    if args.managed_fhir_store and not args.managed_fhir_store_tenant.strip():
        parser.error("--managed-fhir-store-tenant must name a tenant")
    if production(args):
        validate_production_args(parser, args)

    rendered = document_service_accounts(args) if args.documents_bucket else []
    if args.appeals_secrets_project:
        rendered.append({"apiVersion": "v1", "kind": "ServiceAccount", "metadata": {
            "name": "appeals-service", "namespace": NAMESPACE,
            "annotations": {WORKLOAD_IDENTITY_ANNOTATION: args.appeals_secrets_service_account}}})
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

    if production(args):
        rendered.extend(missing_service_accounts(rendered))
        rendered.append(service_clients_config(args))
        rendered.append(service_tokens_config(args))

    yaml.safe_dump_all(rendered, sys.stdout, sort_keys=False)
    return 0


if __name__ == "__main__":
    sys.exit(main())
