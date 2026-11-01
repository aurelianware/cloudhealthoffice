"""
scripts/deploy-gke.sh with HOSTED=true, against stub kubectl, helm and gcloud.

Run: python3 -m pytest deploy/gcp/hosted/test_deploy_gke_hosted.py
Needs bash, openssl and PyYAML. No cluster: the stubs record each call, and the
rendered manifests are the real render_manifests.py output.
"""

import base64
import json
import os
import subprocess
import textwrap
from pathlib import Path

import pytest
import yaml

REPO_ROOT = Path(__file__).resolve().parents[3]
SCRIPT = REPO_ROOT / "scripts" / "deploy-gke.sh"
DISK_KEY = "projects/cho-hosted/locations/us-central1/keyRings/cho-cmek/cryptoKeys/disks"
IAP_AUD = "/projects/123456789012/global/backendServices/987654321"

KUBECTL = r'''#!/usr/bin/env python3
import base64, json, os, sys
from pathlib import Path
log = Path(os.environ["STUB_LOG"])
args = sys.argv[1:]
stdin = "" if sys.stdin.isatty() else sys.stdin.read()
if args[:1] == ["apply"] and "-f" in args and args[args.index("-f") + 1].startswith("/"):
    stdin = Path(args[args.index("-f") + 1]).read_text()  # the rendered manifests
with log.open("a") as f:
    f.write(json.dumps({"tool": "kubectl", "args": args, "stdin": stdin}) + "\n")
joined = " ".join(args)
if args[:2] == ["config", "current-context"]:
    print(os.environ.get("STUB_CONTEXT", "gke_cho-hosted_us-central1_cho-hosted"))
elif args[:2] == ["get", "secret"] and "cho-services" in args and "jsonpath={.data.password}" in joined:
    print(base64.b64encode(b"kafka-scram-password").decode(), end="")
elif args[:2] == ["get", "secret"] and "cloudhealthoffice-kafka-cluster-ca-cert" in args:
    cert = Path(os.environ["STUB_KAFKA_CA"]).read_bytes()
    print(json.dumps({"data": {"ca.crt": base64.b64encode(cert).decode(), "ca.password": "eA=="}}))
elif args[:2] == ["get", "secret"]:
    sys.exit(1)
elif args[:2] == ["get", "deployments"]:
    print("")
elif args[:2] == ["get", "pods"]:
    print("")
elif args[:1] == ["create"] and "--dry-run=client" in args:
    # kubectl create ... --dry-run=client -o yaml, as JSON (valid YAML).
    kind, rest = args[1], args[2:]
    if kind == "secret":
        rest = rest[1:]
    name = rest[0]
    data = {}
    for a in rest:
        if a.startswith("--from-literal="):
            k, v = a[len("--from-literal="):].split("=", 1)
            data[k] = v
        elif a.startswith("--from-file="):
            k, path = a[len("--from-file="):].split("=", 1)
            data[k] = Path(path).read_text()
    if kind == "secret":
        doc = {"apiVersion": "v1", "kind": "Secret", "metadata": {"name": name},
               "data": {k: base64.b64encode(v.encode()).decode() for k, v in data.items()}}
    elif kind == "configmap":
        doc = {"apiVersion": "v1", "kind": "ConfigMap", "metadata": {"name": name}, "data": data}
    else:
        doc = {"apiVersion": "v1", "kind": "Namespace", "metadata": {"name": name}}
    print(json.dumps(doc))
'''

HELM = "#!/usr/bin/env bash\necho \"{\\\"tool\\\": \\\"helm\\\"}\" >> \"$STUB_LOG\"\n"

GCLOUD = r'''#!/usr/bin/env python3
import json, os, sys
from pathlib import Path
log = Path(os.environ["STUB_LOG"])
args = sys.argv[1:]
stdin = "" if sys.stdin.isatty() else sys.stdin.read()
with log.open("a") as f:
    f.write(json.dumps({"tool": "gcloud", "args": args, "stdin": stdin}) + "\n")
if args[:4] == ["kms", "keys", "versions", "get-public-key"]:
    out = args[args.index("--output-file") + 1]
    Path(out).write_text(Path(os.environ["STUB_PUBLIC_KEY"]).read_text())
elif args[:3] == ["secrets", "versions", "access"]:
    secret = args[args.index("--secret") + 1]
    stored = os.environ.get("STUB_STORED_" + secret.replace("-", "_").upper())
    if stored:
        print(stored, end="")
    else:
        print("NOT_FOUND", file=sys.stderr); sys.exit(1)
elif args[:3] == ["secrets", "versions", "list"]:
    if os.environ.get("STUB_SECRET_DENIED"):
        print("PERMISSION_DENIED", file=sys.stderr); sys.exit(1)
    print("")
elif args[:3] == ["secrets", "versions", "add"]:
    print("added")
'''


@pytest.fixture()
def stubs(tmp_path):
    bin_dir = tmp_path / "bin"
    bin_dir.mkdir()
    for name, body in (("kubectl", KUBECTL), ("helm", HELM), ("gcloud", GCLOUD)):
        path = bin_dir / name
        path.write_text(body)
        path.chmod(0o755)
    key = tmp_path / "token.key"
    subprocess.run(["openssl", "genpkey", "-algorithm", "EC", "-pkeyopt", "ec_paramgen_curve:P-256",
                    "-out", str(key)], check=True, capture_output=True)
    public = tmp_path / "token.pub"
    subprocess.run(["openssl", "pkey", "-in", str(key), "-pubout", "-out", str(public)], check=True, capture_output=True)
    kafka_ca = tmp_path / "kafka-ca.crt"
    subprocess.run(["openssl", "req", "-x509", "-new", "-newkey", "ec", "-pkeyopt", "ec_paramgen_curve:P-256",
                    "-nodes", "-days", "30", "-subj", "/CN=cluster-ca", "-keyout", str(tmp_path / "kafka-ca.key"),
                    "-out", str(kafka_ca)], check=True, capture_output=True)
    base_bundle = tmp_path / "base.crt"
    base_bundle.write_text(_public_ca(tmp_path))
    return {"bin": bin_dir, "log": tmp_path / "calls.jsonl", "public_key": public, "kafka_ca": kafka_ca,
            "base_bundle": base_bundle}


def _public_ca(tmp_path):
    out = tmp_path / "public-ca.crt"
    subprocess.run(["openssl", "req", "-x509", "-new", "-newkey", "ec", "-pkeyopt", "ec_paramgen_curve:P-256",
                    "-nodes", "-days", "30", "-subj", "/CN=Some Public Root", "-keyout", str(tmp_path / "public-ca.key"),
                    "-out", str(out)], check=True, capture_output=True)
    return out.read_text()


def hosted_env(stubs, **overrides):
    env = {
        "PATH": f"{stubs['bin']}:{os.environ['PATH']}",
        "HOME": os.environ.get("HOME", "/tmp"),
        "STUB_LOG": str(stubs["log"]),
        "STUB_PUBLIC_KEY": str(stubs["public_key"]),
        "STUB_KAFKA_CA": str(stubs["kafka_ca"]),
        "CA_BASE_BUNDLE": str(stubs["base_bundle"]),
        "HOSTED": "true",
        "GAR_REPOSITORY": "us-central1-docker.pkg.dev/cho-hosted/cho",
        "IMAGE_TAG": "abc123",
        "TOKEN_SIGNING_KEY_VERSION": "projects/cho-hosted/locations/us-central1/keyRings/cho-token-signing/cryptoKeys/token-signing/cryptoKeyVersions/1",
        "TOKEN_SERVICE_GOOGLE_ACCOUNT": "cho-token-service@cho-hosted.iam.gserviceaccount.com",
        "KUBERNETES_SA_ISSUER": "https://container.googleapis.com/v1/projects/cho-hosted/locations/us-central1/clusters/cho-hosted",
        "IAP_AUDIENCE": IAP_AUD,
        "HOSTED_TENANT": "plan-a",
        "HOSTED_TENANT_NAME": "Plan A Health",
        "HOSTED_ADMIN_EMAILS": "ops@plan-a.example",
        "FHIR_HOST": "fhir.plan-a.example",
        "AUTH_HOST": "auth.plan-a.example",
        "HOSTED_DISK_KMS_KEY": DISK_KEY,
        "HOSTED_SECRETS_PROJECT": "cho-hosted",
        "DOCUMENTS_BUCKET": "cho-hosted-documents",
        "DOCUMENTS_SERVICE_ACCOUNT": "cho-documents@cho-hosted.iam.gserviceaccount.com",
    }
    env.update(overrides)
    return {k: v for k, v in env.items() if v is not None}


def run(env):
    return subprocess.run(["bash", str(SCRIPT)], cwd=REPO_ROOT, env=env, capture_output=True, text=True, timeout=600)


def calls(stubs):
    return [json.loads(line) for line in stubs["log"].read_text().splitlines()] if stubs["log"].exists() else []


def applied_docs(stubs):
    docs = []
    for call in calls(stubs):
        if call["tool"] == "kubectl" and call["args"][:1] == ["apply"] and call["stdin"]:
            docs += [d for d in yaml.safe_load_all(call["stdin"]) if d]
    return docs


def applied_files(stubs):
    return [c["args"][c["args"].index("-f") + 1] for c in calls(stubs)
            if c["tool"] == "kubectl" and c["args"][:1] == ["apply"] and "-f" in c["args"]]


@pytest.mark.parametrize("override, message", [
    ({"CLOUDFLARE_TUNNEL_TOKEN": "x"}, "BAA"),
    ({"CONFORMANCE_FHIR_HOST": "fhir.example.com"}, "FHIR_HOST and AUTH_HOST"),
    ({"HOSTED_TENANT": "demo"}, "demo"),
    ({"HOSTED_TENANT": None}, "HOSTED_TENANT"),
    ({"HOSTED_DISK_KMS_KEY": None}, "HOSTED_DISK_KMS_KEY"),
    ({"IAP_AUDIENCE": None}, "missing: IAP_AUDIENCE"),
    ({"RESOURCE_PROFILE": "small"}, "RESOURCE_PROFILE=default"),
    ({"ENABLE_KAFKA": "false"}, "runs Kafka"),
    ({"ANTHROPIC_API_KEY": "sk-test"}, "BAA with Anthropic"),
    ({"STUB_CONTEXT": "gke_cho-demo_us-west1_cho-demo"}, "demo cluster"),
])
def test_hosted_refuses_unsafe_settings(stubs, override, message):
    env = hosted_env(stubs, **override)
    if "STUB_CONTEXT" in override:
        env["STUB_CONTEXT"] = override["STUB_CONTEXT"]
    result = run(env)
    assert result.returncode != 0
    assert message in result.stderr, result.stderr


def test_hosted_deploy_installs_the_hosted_stack(stubs):
    result = run(hosted_env(stubs))
    assert result.returncode == 0, result.stdout[-3000:] + result.stderr[-3000:]
    docs = applied_docs(stubs)

    storage = next(d for d in docs if d.get("kind") == "StorageClass")
    assert storage["metadata"]["name"] == "cho-pd-cmek"
    assert storage["parameters"] == {"type": "pd-balanced", "disk-encryption-kms-key": DISK_KEY}
    assert storage["reclaimPolicy"] == "Retain"

    files = applied_files(stubs)
    assert "deploy/gcp/hosted/kafka-cluster.yaml" in files
    assert "deploy/gcp/hosted/redis.yaml" in files
    assert "deploy/gcp/kafka/kafka-cluster.yaml" not in files
    assert "infrastructure/k8s/redis-dataprotection.yaml" not in files
    assert "deploy/gcp/cloudflare/cloudflared.yaml" not in files

    secrets = {d["metadata"]["name"]: d for d in docs if d.get("kind") == "Secret"}
    def value(name, key):
        data = secrets[name].get("data") or {}
        return base64.b64decode(data[key]).decode()
    assert value("kafka-secret", "saslUsername") == "cho-services"
    assert value("kafka-secret", "saslPassword") == "kafka-scram-password"
    assert value("kafka-secret", "bootstrapServers").endswith(":9093")
    redis_password = value("redis-auth", "password")
    assert f"password={redis_password}" in value("redis-secret", "connectionString")
    assert "ssl=true" in value("redis-secret", "connectionString")
    assert value("database-secret", "connectionString").endswith("&tls=true")
    for key in ("tls.crt", "tls.key", "ca.crt", "server.pem"):
        assert value("cho-internal-tls", key)
    assert "BEGIN" in value("cho-internal-ca", "ca.key")

    # Each generated password went to Secret Manager, over stdin.
    added = {c["args"][3]: c["stdin"] for c in calls(stubs)
             if c["tool"] == "gcloud" and c["args"][:3] == ["secrets", "versions", "add"]}
    assert set(added) == {"cho-mongodb-password", "cho-postgres-password", "cho-pricing-admin-secret", "cho-redis-password"}
    assert added["cho-redis-password"] == redis_password
    assert all(v not in " ".join(c["args"]) for c in calls(stubs) if c["tool"] == "gcloud" for v in added.values())

    bundle = next(d for d in docs if d.get("kind") == "ConfigMap" and d["metadata"]["name"] == "cho-ca-bundle")
    pem = bundle["data"]["ca-certificates.crt"]
    assert pem.count("BEGIN CERTIFICATE") == 3
    assert stubs["kafka_ca"].read_text().strip() in pem

    mongo = next(d for d in docs if d.get("kind") == "StatefulSet" and d["metadata"]["name"] == "mongodb")
    args = mongo["spec"]["template"]["spec"]["containers"][0]["args"]
    assert args[args.index("--tlsMode") + 1] == "requireTLS"
    assert mongo["spec"]["volumeClaimTemplates"][0]["spec"]["storageClassName"] == "cho-pd-cmek"

    deployments = {d["metadata"]["name"]: d for d in docs if d.get("kind") == "Deployment"}
    portal_env = {e["name"]: e.get("value") for e in deployments["portal"]["spec"]["template"]["spec"]["containers"][0]["env"]}
    assert portal_env["ASPNETCORE_ENVIRONMENT"] == "Production"
    assert portal_env["Authentication__CloudflareAccess__Provider"] == "GoogleIap"

    seeds = [c for c in calls(stubs) if c["tool"] == "kubectl" and c["args"][:1] == ["exec"] and
             any("--eval" == a for a in c["args"])]
    scripts = " ".join(c["args"][-1] for c in seeds)
    assert "MBR001" not in scripts
    assert any("SEED_TENANT=plan-a" in c["args"] for c in seeds)
    assert all("--tls" in c["args"] for c in seeds)


def test_secret_manager_errors_stop_the_deploy(stubs):
    result = run(hosted_env(stubs, STUB_SECRET_DENIED="1"))
    assert result.returncode != 0
    assert "Cannot read Secret Manager secret cho-mongodb-password" in result.stderr


def test_stored_passwords_are_reused(stubs):
    result = run(hosted_env(stubs, STUB_STORED_CHO_MONGODB_PASSWORD="kept-mongo",
                            STUB_STORED_CHO_REDIS_PASSWORD="kept-redis",
                            STUB_STORED_CHO_POSTGRES_PASSWORD="kept-pg",
                            STUB_STORED_CHO_PRICING_ADMIN_SECRET="kept-pricing"))
    assert result.returncode == 0, result.stderr[-3000:]
    assert not [c for c in calls(stubs) if c["tool"] == "gcloud" and c["args"][:3] == ["secrets", "versions", "add"]]
    secret = next(d for d in applied_docs(stubs) if d.get("kind") == "Secret" and d["metadata"]["name"] == "mongodb-auth")
    assert base64.b64decode(secret["data"]["password"]).decode() == "kept-mongo"


def test_demo_deploy_is_unchanged(stubs):
    env = {k: v for k, v in hosted_env(stubs).items()
           if k in ("PATH", "HOME", "STUB_LOG", "GAR_REPOSITORY", "IMAGE_TAG")}
    result = run(env)
    assert result.returncode == 0, result.stdout[-3000:] + result.stderr[-3000:]
    docs = applied_docs(stubs)
    storage = next(d for d in docs if d.get("kind") == "StorageClass")
    assert storage["parameters"] == {"type": "pd-standard"}
    files = applied_files(stubs)
    assert "deploy/gcp/kafka/kafka-cluster.yaml" in files
    assert "infrastructure/k8s/redis-dataprotection.yaml" in files
    assert not any(c["tool"] == "gcloud" for c in calls(stubs))
    secrets = {d["metadata"]["name"]: d for d in docs if d.get("kind") == "Secret"}
    assert base64.b64decode(secrets["kafka-secret"]["data"]["saslUsername"]).decode() == ""
    assert "tls=true" not in base64.b64decode(secrets["database-secret"]["data"]["connectionString"]).decode()
    seeds = " ".join(c["args"][-1] for c in calls(stubs) if c["tool"] == "kubectl" and c["args"][:1] == ["exec"])
    assert "MBR001" in seeds
    assert not any(d.get("kind") == "ConfigMap" and d["metadata"]["name"] == "cho-ca-bundle" for d in docs)
