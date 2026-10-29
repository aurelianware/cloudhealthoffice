"""Checks for deploy/gcp/render_manifests.py. Run: python3 -m pytest deploy/gcp"""

import json
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


def test_fhir_serves_cho_native_patient_and_coverage_labelled_synthetic():
    docs = render()
    config = next(d for d in docs if d["kind"] == "ConfigMap" and d["metadata"]["name"] == "fhir-service-config")
    assert config["data"]["FhirAdapters__Sources__Patient"] == "ChoNative"
    assert config["data"]["FhirAdapters__Sources__Coverage"] == "ChoNative"
    assert config["data"]["FhirAdapters__DataClassification"] == "synthetic"


def test_document_services_keep_their_manifest_storage_without_a_bucket():
    docs = render()
    names = {d["metadata"]["name"] for d in docs if d["kind"] == "ServiceAccount"}
    assert not names & {"document-service", "member-document-service"}
    for name in ("document-service",):
        dep = deployments(docs)[name]
        assert dep["spec"]["template"]["spec"].get("serviceAccountName") != name
        assert "DocumentStore__Provider" not in env(dep)


def test_document_services_use_the_bucket_through_workload_identity():
    gsa = "cho-documents@demo-project.iam.gserviceaccount.com"
    docs = render("--documents-bucket", "demo-project-cho-documents",
                  "--documents-service-account", gsa)
    accounts = [d for d in docs if d["kind"] == "ServiceAccount"]
    document_accounts = {"document-service"}
    # One account each: no manifest already defines one with these names.
    assert sorted(a["metadata"]["name"] for a in accounts if a["metadata"]["name"] in document_accounts) \
        == sorted(document_accounts)
    accounts = {a["metadata"]["name"]: a for a in accounts if a["metadata"]["name"] in document_accounts}
    deps = deployments(docs)
    for name in ("document-service",):
        assert accounts[name]["metadata"]["annotations"]["iam.gke.io/gcp-service-account"] == gsa
        assert deps[name]["spec"]["template"]["spec"]["serviceAccountName"] == name
        assert env(deps[name])["DocumentStore__Provider"] == "Gcs"
        assert env(deps[name])["DocumentStore__Gcs__Bucket"] == "demo-project-cho-documents"
    # member-document-service and attachment-service file through the catalog:
    # no bucket settings and no document identity.
    for name in ("member-document-service", "attachment-service"):
        assert "DocumentStore__Provider" not in env(deps[name])
        assert deps[name]["spec"]["template"]["spec"].get("serviceAccountName") != "document-service"
    # No other service gets the document identity.
    others = [n for n, d in deps.items()
              if n not in document_accounts
              and d["spec"]["template"]["spec"].get("serviceAccountName") in document_accounts]
    assert not others


MANAGED_STORE = "projects/demo-project/locations/us-central1/datasets/cho/fhirStores/cho-fhir-r4"
MANAGED_STORE_GSA = "cho-fhir-store@demo-project.iam.gserviceaccount.com"


def test_fhir_service_does_not_publish_to_a_managed_store_by_default():
    docs = render()
    fhir = deployments(docs)["fhir-service"]
    assert not any(k.startswith("ManagedFhirStore__") for k in env(fhir))
    account = next(d for d in docs if d["kind"] == "ServiceAccount" and d["metadata"]["name"] == "fhir-service-sa")
    assert "iam.gke.io/gcp-service-account" not in (account["metadata"].get("annotations") or {})


def test_fhir_service_publishes_to_the_managed_store_through_workload_identity():
    docs = render("--managed-fhir-store", MANAGED_STORE,
                  "--managed-fhir-store-service-account", MANAGED_STORE_GSA,
                  "--managed-fhir-store-tenant", "plan-a")
    deps = deployments(docs)
    fhir = deps["fhir-service"]
    assert fhir["spec"]["template"]["spec"]["serviceAccountName"] == "fhir-service-sa"
    assert {k: v for k, v in env(fhir).items() if k.startswith("ManagedFhirStore__")} == {
        "ManagedFhirStore__Provider": "GcpHealthcare",
        "ManagedFhirStore__Gcp__ProjectId": "demo-project",
        "ManagedFhirStore__Gcp__Location": "us-central1",
        "ManagedFhirStore__Gcp__DatasetId": "cho",
        "ManagedFhirStore__Gcp__FhirStoreId": "cho-fhir-r4",
        "ManagedFhirStore__TenantId": "plan-a",
    }
    accounts = [d for d in docs if d["kind"] == "ServiceAccount" and d["metadata"]["name"] == "fhir-service-sa"]
    assert len(accounts) == 1
    assert accounts[0]["metadata"]["annotations"]["iam.gke.io/gcp-service-account"] == MANAGED_STORE_GSA
    # No other service is given the store's identity.
    others = [n for n, d in deps.items()
              if n != "fhir-service" and d["spec"]["template"]["spec"].get("serviceAccountName") == "fhir-service-sa"]
    assert not others
    assert not any("ManagedFhirStore__Provider" in env(d) for n, d in deps.items() if n != "fhir-service")


def test_managed_store_tenant_defaults_to_demo():
    docs = render("--managed-fhir-store", MANAGED_STORE,
                  "--managed-fhir-store-service-account", MANAGED_STORE_GSA)
    assert env(deployments(docs)["fhir-service"])["ManagedFhirStore__TenantId"] == "demo"


def test_managed_store_needs_its_service_account_and_a_full_store_id():
    for extra in (["--managed-fhir-store", MANAGED_STORE],
                  ["--managed-fhir-store-service-account", MANAGED_STORE_GSA],
                  ["--managed-fhir-store", "cho-fhir-r4", "--managed-fhir-store-service-account", MANAGED_STORE_GSA]):
        result = subprocess.run(
            [sys.executable, str(SCRIPT), "--registry", REGISTRY, *extra],
            capture_output=True, text=True,
        )
        assert result.returncode != 0, extra


def test_production_keeps_the_managed_store_identity(tmp_path):
    docs = render(*production_args(tmp_path, "--managed-fhir-store", MANAGED_STORE,
                                   "--managed-fhir-store-service-account", MANAGED_STORE_GSA))
    account = next(d for d in docs if d["kind"] == "ServiceAccount" and d["metadata"]["name"] == "fhir-service-sa")
    assert account["metadata"]["annotations"]["iam.gke.io/gcp-service-account"] == MANAGED_STORE_GSA
    fhir = deployments(docs)["fhir-service"]
    assert fhir["spec"]["template"]["spec"]["serviceAccountName"] == "fhir-service-sa"
    assert env(fhir)["ManagedFhirStore__Provider"] == "GcpHealthcare"


def test_document_bucket_needs_its_service_account():
    result = subprocess.run(
        [sys.executable, str(SCRIPT), "--registry", REGISTRY, "--documents-bucket", "b"],
        capture_output=True, text=True,
    )
    assert result.returncode != 0
    assert "go together" in result.stderr


# ── --security production ───────────────────────────────────────────────────

KMS_VERSION = "projects/cho-demo/locations/us-west1/keyRings/cho/cryptoKeys/token-signing/cryptoKeyVersions/1"
TOKEN_SERVICE_GSA = "cho-token-service@cho-demo.iam.gserviceaccount.com"
CLUSTER_ISSUER = "https://container.googleapis.com/v1/projects/cho-demo/locations/us-west1/clusters/cho"
ACCESS_TEAM = "cho-demo.cloudflareaccess.com"
ACCESS_AUD = "test-access-application-aud"
PUBLIC_KEY = "-----BEGIN PUBLIC KEY-----\nMFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEexample\n-----END PUBLIC KEY-----\n"


def production_args(tmp_path, *extra):
    key = tmp_path / "token-service.pub.pem"
    key.write_text(PUBLIC_KEY)
    return ("--security", "production",
            "--token-signing-key-version", KMS_VERSION,
            "--token-service-google-account", TOKEN_SERVICE_GSA,
            "--kubernetes-issuer", CLUSTER_ISSUER,
            "--token-public-key-file", str(key),
            "--access-team-domain", ACCESS_TEAM,
            "--access-audience", ACCESS_AUD, *extra)


def config_map(docs, name):
    return next(d for d in docs if d["kind"] == "ConfigMap" and d["metadata"]["name"] == name)["data"]


def test_production_hosts_accept_no_development_keys_or_overrides(tmp_path):
    docs = render(*production_args(tmp_path))
    text = yaml.safe_dump_all(docs)
    for marker in ("CHO_TESTING", "CHO_ALLOW_TESTING_ENVIRONMENT", "CHO_ALLOW_DEV_ON_DEPLOYED_HOST",
                   "SymmetricKey", "cho-portal-dev", "cho-internal-dev", "LocalDemo",
                   "Q0hPLWRldmVsb3BtZW50", "local-demo-admin", "SmartAuth__DevMode"):
        assert marker not in text, marker
    for name, dep in deployments(docs).items():
        assert env(dep)["ASPNETCORE_ENVIRONMENT"] == "Staging", name
    for d in docs:
        if d["kind"] == "ConfigMap" and "ASPNETCORE_ENVIRONMENT" in (d.get("data") or {}):
            assert d["data"]["ASPNETCORE_ENVIRONMENT"] == "Staging", d["metadata"]["name"]


def test_production_services_trust_only_the_token_service_key(tmp_path):
    tokens = config_map(render(*production_args(tmp_path)), "cho-service-tokens")
    issuers = {(tokens[f"ChoAuth__Issuers__{i}__Issuer"], tokens[f"ChoAuth__Issuers__{i}__Kind"]) for i in range(3)}
    assert issuers == {("cho-token-service", "User"), ("cho-token-service-svc", "Service"), ("cho-workload", "Workload")}
    assert all(tokens[f"ChoAuth__Issuers__{i}__PublicKeyPem"] == PUBLIC_KEY for i in range(3))
    assert tokens["ChoAuth__ServiceToken__Source"] == "TokenService"
    assert tokens["ChoAuth__ServiceToken__Credential"] == "KubernetesServiceAccount"


def test_production_token_service_signs_with_cloud_kms_and_registers_every_service(tmp_path):
    docs = render(*production_args(tmp_path))
    deps = deployments(docs)
    assert deps["token-service"]["spec"]["replicas"] == 1

    config = config_map(docs, "token-service-config")
    assert config["TokenSigning__CloudKmsKeyVersion"] == KMS_VERSION
    assert "TokenSigning__KeyVaultKeyId" not in config
    assert config["ServiceTokens__IdentityProvider"] == "Kubernetes"
    assert config["ServiceTokens__Kubernetes__KubernetesIssuer"] == CLUSTER_ISSUER
    assert not any(k.startswith("ServiceTokens__Audiences") or k == "ServiceTokens__EntraTenantId" for k in config)
    assert config["WorkloadTokens__Enabled"] == "false"
    assert config["CloudflareAccess__TeamDomain"] == ACCESS_TEAM
    assert config["CloudflareAccess__Audiences__0"] == ACCESS_AUD
    assert config["CloudflareAccess__Tenants__0"] == "demo"
    assert config["TokenService__PlatformTenantId"] == ""

    accounts = {d["metadata"]["name"]: d["metadata"].get("annotations", {}) for d in docs if d["kind"] == "ServiceAccount"}
    assert accounts["token-service-sa"] == {"iam.gke.io/gcp-service-account": TOKEN_SERVICE_GSA}
    assert not any(k.startswith("azure.workload.identity/") for a in accounts.values() for k in a)

    registry = json.loads(config_map(docs, "token-service-service-clients")["service-clients.json"])
    by_client = {r["clientId"]: r for r in registry}
    assert by_client["claims-service"] == {"clientId": "claims-service", "namespace": "cloudhealthoffice", "serviceAccount": "claims-service-sa"}
    assert "token-service" not in by_client and "portal" not in by_client
    assert len({r["serviceAccount"] for r in registry}) == len(registry)
    for name, dep in deps.items():
        if name in ("portal", "token-service"):
            continue
        spec = dep["spec"]["template"]["spec"]
        assert spec["serviceAccountName"] in accounts, name
        assert any(r["serviceAccount"] == spec["serviceAccountName"] for r in registry), name
        assert {"azure.workload.identity/use"}.isdisjoint(dep["spec"]["template"]["metadata"].get("labels", {})), name


def test_production_services_present_a_projected_token(tmp_path):
    deps = deployments(render(*production_args(tmp_path)))
    for name, dep in deps.items():
        if name in ("portal", "token-service"):
            continue
        spec = dep["spec"]["template"]["spec"]
        volume = next(v for v in spec["volumes"] if v["name"] == "cho-token-service")
        source = volume["projected"]["sources"][0]["serviceAccountToken"]
        assert source == {"audience": "cho-token-service", "expirationSeconds": 3600, "path": "token"}, name
        for container in spec["containers"]:
            assert {"name": "cho-token-service", "mountPath": "/var/run/secrets/cho/token-service", "readOnly": True} \
                in container["volumeMounts"], name
        values = env(dep)
        assert values["ChoAuth__ServiceToken__Credential"] == "KubernetesServiceAccount", name
        assert values["ChoAuth__ServiceToken__ClientId"], name
    assert env(deps["idcard-service"])["ChoAuth__ServiceToken__ClientId"] == "idcard-service"


def test_production_portal_signs_in_through_cloudflare_access(tmp_path):
    portal = env(deployments(render(*production_args(tmp_path)))["portal"])
    assert portal["Authentication__Mode"] == "CloudflareAccess"
    assert portal["Authentication__CloudflareAccess__TeamDomain"] == ACCESS_TEAM
    assert portal["Authentication__CloudflareAccess__Audiences__0"] == ACCESS_AUD
    assert portal["Services__TokenService"] == "http://token-service"


def test_production_keeps_document_storage_identity(tmp_path):
    docs = render(*production_args(tmp_path, "--documents-bucket", "cho-docs",
                                   "--documents-service-account", "cho-documents@cho-demo.iam.gserviceaccount.com"))
    registry = json.loads(config_map(docs, "token-service-service-clients")["service-clients.json"])
    assert {"clientId": "document-service", "namespace": "cloudhealthoffice", "serviceAccount": "document-service"} in registry


def test_production_needs_every_setting(tmp_path):
    args = list(production_args(tmp_path))
    i = args.index("--access-audience")
    del args[i:i + 2]
    result = subprocess.run([sys.executable, str(SCRIPT), "--registry", REGISTRY, *args], capture_output=True, text=True)
    assert result.returncode != 0 and "--access-audience" in result.stderr


def test_production_refuses_development_hosts_and_unpinned_keys(tmp_path):
    for override in (("--host-environment", "Development"),
                     ("--token-signing-key-version", KMS_VERSION.rsplit("/", 1)[0] + "/latest")):
        args = list(production_args(tmp_path))
        if override[0] in args:
            args[args.index(override[0]) + 1] = override[1]
        else:
            args += override
        result = subprocess.run([sys.executable, str(SCRIPT), "--registry", REGISTRY, *args], capture_output=True, text=True)
        assert result.returncode != 0, override


def test_demo_mode_is_unchanged_by_default():
    deps = deployments(render())
    assert deps["token-service"]["spec"]["replicas"] == 0
    assert env(deps["claims-service"])["CHO_ALLOW_DEV_ON_DEPLOYED_HOST"] == "true"
    assert not any(d["kind"] == "ConfigMap" and d["metadata"]["name"] == "cho-service-tokens" for d in render())


def test_contact_and_care_images_are_built_and_rewritten():
    listed = subprocess.check_output([sys.executable, str(SCRIPT), "--list-images"], text=True)
    deps = deployments(render())
    for name in ("contact-service", "care-management-service"):
        assert f"{name} src/services/{name}/Dockerfile" in listed
        assert deps[name]["spec"]["template"]["spec"]["containers"][0]["image"] == f"{REGISTRY}/cloudhealthoffice-{name}:abc123"
    assert env(deps["portal"])["Services__ContactService"] == "http://contact-service/api"
    assert env(deps["portal"])["Services__DocumentService"] == "http://document-service"
    assert env(deps["contact-service"])["Services__WorkManagementServiceUrl"] == "http://work-management-service"


def test_documents_require_the_private_scanner():
    settings = env(deployments(render())["document-service"])
    assert settings["Documents__AllowUnscannedContent"] == "false"
    assert settings["Documents__Scanner__Provider"] == "ClamAv"
    assert settings["Documents__Scanner__ClamAv__Host"] == "document-scanner"


def test_appeals_secret_provider_is_opt_in_and_scoped_to_one_key():
    assert env(deployments(render())["appeals-service"])["SecretProvider__Provider"] == "None"
    rendered = render("--appeals-secrets-project", "demo-project", "--appeals-secrets-service-account", "cho-appeals@demo-project.iam.gserviceaccount.com")
    dep = deployments(rendered)["appeals-service"]
    settings = env(dep)
    assert settings["SecretProvider__Provider"] == "GoogleSecretManager"
    assert settings["SecretProvider__GoogleSecretManagerProjectId"] == "demo-project"
    assert settings["SecretProvider__GoogleSecretManagerSecretNames__0"] == "appeal-body-encryption-key-v1"
    assert dep["spec"]["template"]["spec"]["serviceAccountName"] == "appeals-service"
    assert dep["spec"]["template"]["spec"]["containers"][0]["readinessProbe"]["httpGet"]["path"] == "/health/ready"
    sa = next(d for d in rendered if d["kind"] == "ServiceAccount" and d["metadata"]["name"] == "appeals-service")
    assert sa["metadata"]["annotations"]["iam.gke.io/gcp-service-account"] == "cho-appeals@demo-project.iam.gserviceaccount.com"
    assert env(deployments(rendered)["contact-service"])["SecretProvider__Provider"] == "None"


def test_appeals_secret_provider_requires_both_project_and_identity():
    for flag, value in (("--appeals-secrets-project", "demo-project"), ("--appeals-secrets-service-account", "cho-appeals@demo-project.iam.gserviceaccount.com")):
        result = subprocess.run([sys.executable, str(SCRIPT), "--registry", REGISTRY, "--tag", "abc123", flag, value], capture_output=True, text=True)
        assert result.returncode != 0
        assert "go together" in result.stderr


def test_production_appeals_identity_preserves_secret_binding_and_token_registration(tmp_path):
    docs = render(*production_args(tmp_path, "--appeals-secrets-project", "demo-project", "--appeals-secrets-service-account", "cho-appeals@demo-project.iam.gserviceaccount.com"))
    dep = deployments(docs)["appeals-service"]
    assert dep["spec"]["template"]["spec"]["serviceAccountName"] == "appeals-service"
    assert env(dep)["SecretProvider__Provider"] == "GoogleSecretManager"
    clients = json.loads(config_map(docs, "token-service-service-clients")["service-clients.json"])
    owner = next(c for c in clients if c["clientId"] == "appeals-service")
    assert owner["serviceAccount"] == "appeals-service"
    assert owner["namespace"] == "cloudhealthoffice"


def test_contact_intake_owners_read_the_in_cluster_contact_service():
    rendered = deployments(render())
    for name in ("appeals-service", "authorization-service", "work-management-service"):
        # HttpContactIntakeReader appends api/contacts/{id} to this base URL.
        assert env(rendered[name])["Services__ContactServiceUrl"] == "http://contact-service"


def test_leie_screening_is_opt_in_and_scoped_to_verification():
    setting = "ProviderVerification__ExclusionScreening__Leie__Enabled"
    before = deployments(render())
    after = deployments(render("--leie-screening"))
    assert env(after["provider-verification-service"])[setting] == "true"
    assert env(before["provider-verification-service"]).get(setting) != "true"
    for name, dep in after.items():
        if name != "provider-verification-service":
            assert dep == before[name], name
    # No implicit SAM source, API credential, custom publisher URL or stale-data exemption.
    original = env(before["provider-verification-service"])
    changed = env(after["provider-verification-service"])
    assert {k: v for k, v in changed.items() if k != setting} == {k: v for k, v in original.items() if k != setting}


def test_sam_screening_uses_daily_public_extract_and_secret_reference():
    deps = deployments(render("--sam-screening", "--leie-screening"))
    configured = env(deps["provider-verification-service"])
    assert configured["ProviderVerification__ExclusionScreening__Sam__Enabled"] == "true"
    assert configured["ProviderVerification__ExclusionScreening__Sam__Mode"] == "Extract"
    url = configured["ProviderVerification__ExclusionScreening__Sam__ExtractDownloadUrl"]
    assert url == "https://api.sam.gov/data-services/v1/extracts?fileType=EXCLUSION&sensitivity=PUBLIC&frequency=DAILY&version=V2"
    container = deps["provider-verification-service"]["spec"]["template"]["spec"]["containers"][0]
    key = next(e for e in container["env"] if e["name"] == "ProviderVerification__SamGovApiKey")
    assert key["valueFrom"]["secretKeyRef"] == {"name": "provider-verification-service-secrets", "key": "ProviderVerification__SamGovApiKey"}
    baseline = deployments(render())
    for name, dep in deps.items():
        if name != "provider-verification-service":
            assert dep == baseline[name], name


def test_sam_secret_payload_keeps_key_out_of_renderer_and_cli_arguments():
    import base64
    import os
    script = (SCRIPT.parents[2] / "scripts/deploy-gke.sh").read_text()
    start = script.index("python3 -c '\nimport base64,json,os") + len("python3 -c '")
    end = script.index("\n' | kubectl apply -f - >/dev/null", start)
    result = subprocess.run([sys.executable, "-c", script[start:end]], check=True, capture_output=True, text=True,
                            env={**os.environ, "SAM_KEY_VALUE": "test-only-value", "SAM_MONGO_CONN": "test-only-connection", "SAM_NAMESPACE": "cloudhealthoffice"})
    secret = json.loads(result.stdout)
    assert secret["metadata"] == {"name": "provider-verification-service-secrets", "namespace": "cloudhealthoffice"}
    assert base64.b64decode(secret["data"]["ProviderVerification__SamGovApiKey"]) == b"test-only-value"
    assert "test-only-value" not in result.stdout
    assert not result.stderr


BULK_GSA = "cho-fhir-service@demo-project.iam.gserviceaccount.com"


def test_fhir_service_has_no_bulk_export_by_default():
    fhir = deployments(render())["fhir-service"]
    assert not any(k.startswith("BulkExport__") for k in env(fhir))


def test_bulk_export_writes_to_its_own_bucket_as_fhir_service():
    docs = render("--bulk-export-bucket", "demo-project-cho-bulk-exports",
                  "--fhir-service-google-account", BULK_GSA)
    deps = deployments(docs)
    fhir = deps["fhir-service"]
    assert fhir["spec"]["template"]["spec"]["serviceAccountName"] == "fhir-service-sa"
    assert {k: v for k, v in env(fhir).items() if k.startswith("BulkExport__")} == {
        "BulkExport__Enabled": "true",
        "BulkExport__DocumentStore__Provider": "Gcs",
        "BulkExport__DocumentStore__Gcs__Bucket": "demo-project-cho-bulk-exports",
    }
    account = next(d for d in docs if d["kind"] == "ServiceAccount" and d["metadata"]["name"] == "fhir-service-sa")
    assert account["metadata"]["annotations"]["iam.gke.io/gcp-service-account"] == BULK_GSA
    assert not any("BulkExport__Enabled" in env(d) for n, d in deps.items() if n != "fhir-service")


def test_bulk_export_shares_the_managed_store_identity():
    docs = render("--bulk-export-bucket", "b",
                  "--managed-fhir-store", MANAGED_STORE,
                  "--managed-fhir-store-service-account", MANAGED_STORE_GSA)
    account = next(d for d in docs if d["kind"] == "ServiceAccount" and d["metadata"]["name"] == "fhir-service-sa")
    assert account["metadata"]["annotations"]["iam.gke.io/gcp-service-account"] == MANAGED_STORE_GSA
    assert env(deployments(docs)["fhir-service"])["BulkExport__DocumentStore__Gcs__Bucket"] == "b"


def test_bulk_export_needs_one_fhir_service_identity():
    for extra in (["--bulk-export-bucket", "b"],
                  ["--bulk-export-bucket", "b", "--fhir-service-google-account", BULK_GSA,
                   "--managed-fhir-store", MANAGED_STORE, "--managed-fhir-store-service-account", MANAGED_STORE_GSA]):
        result = subprocess.run(
            [sys.executable, str(SCRIPT), "--registry", REGISTRY, *extra],
            capture_output=True, text=True,
        )
        assert result.returncode != 0, extra


CONFORMANCE = ("--conformance-fhir-host", "fhir-test.example.com",
               "--conformance-auth-host", "auth-test.example.com",
               "--trusted-proxy-network", "10.52.0.0/14")


def test_conformance_hosts_are_off_by_default():
    docs = render()
    assert config_map(docs, "fhir-service-config")["Fhir__ServerBaseUrl"] == "https://api.cloudhealthoffice.com/fhir/r4"
    assert not any(k.startswith("SmartAuth__") for k in config_map(docs, "fhir-service-config"))
    assert config_map(docs, "smart-auth-service-config")["SmartAuth__Issuer"] == "https://auth.cloudhealthoffice.com"


def test_conformance_hosts_set_the_issuer_and_the_fhir_base_url():
    docs = render(*CONFORMANCE)
    auth = config_map(docs, "smart-auth-service-config")
    assert auth["SmartAuth__Issuer"] == "https://auth-test.example.com"
    assert auth["SmartAuth__FhirBaseUrl"] == "https://fhir-test.example.com/fhir/r4"
    assert {k: v for k, v in auth.items() if k.startswith("SmartAuth__TrustedProxyNetworks__")} == {
        "SmartAuth__TrustedProxyNetworks__0": "10.52.0.0/14"}
    fhir = config_map(docs, "fhir-service-config")
    assert fhir["Fhir__ServerBaseUrl"] == "https://fhir-test.example.com/fhir/r4"
    assert {k: v for k, v in fhir.items() if k.startswith("SmartAuth__")} == {
        "SmartAuth__Issuer": "https://auth-test.example.com",
        "SmartAuth__Mode": "ExternalIssuer",
        # As smart-auth-service writes it (Uri.AbsoluteUri); explicit entries match exactly.
        "SmartAuth__TrustedIssuers__0__Issuer": "https://auth-test.example.com/",
        "SmartAuth__TrustedIssuers__0__Audiences__0": "fhir-api",
        "SmartAuth__TrustedIssuers__0__Tenants__0": "demo",
        "SmartAuth__TrustedIssuers__0__IssuesChoClientRegistrations": "true",
    }


def test_conformance_hosts_hold_under_production_security(tmp_path):
    docs = render(*CONFORMANCE, *production_args(tmp_path))
    assert config_map(docs, "fhir-service-config")["SmartAuth__Mode"] == "ExternalIssuer"
    assert config_map(docs, "smart-auth-service-config")["SmartAuth__TrustedProxyNetworks__0"] == "10.52.0.0/14"


def render_error(*extra: str) -> str:
    result = subprocess.run(
        [sys.executable, str(SCRIPT), "--registry", REGISTRY, *extra], capture_output=True, text=True)
    assert result.returncode != 0
    return result.stderr


def test_conformance_hosts_are_checked():
    assert "go together" in render_error("--conformance-fhir-host", "fhir-test.example.com")
    assert "bare hostname" in render_error(
        "--conformance-fhir-host", "https://fhir-test.example.com", "--conformance-auth-host", "auth-test.example.com",
        "--trusted-proxy-network", "10.0.0.0/8")
    assert "must differ" in render_error(
        "--conformance-fhir-host", "x.example.com", "--conformance-auth-host", "x.example.com",
        "--trusted-proxy-network", "10.0.0.0/8")
    assert "needs --trusted-proxy-network" in render_error(
        "--conformance-fhir-host", "fhir-test.example.com", "--conformance-auth-host", "auth-test.example.com")
    assert "not a CIDR" in render_error(*CONFORMANCE[:4], "--trusted-proxy-network", "10.52.0.0")
    assert "only used with" in render_error("--trusted-proxy-network", "10.0.0.0/8")
