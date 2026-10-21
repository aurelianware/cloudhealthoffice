#!/usr/bin/env python3
"""Build known-broken service variants from HEAD in disposable source copies.

No production mutation flags, checkout edits, remote targets, or raw token capture.
Requires a clean committed checkout and already-built baseline runner/member service.
"""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tarfile
import tempfile

ROOT = Path(__file__).resolve().parents[2]
FHIR_MUTATIONS = {
    "scope-bypass": (
        "src/services/fhir-service/Middleware/SmartScopeEnforcementMiddleware.cs",
        "if (!HasRequiredScope(scopes, resourceType, interaction.Access, interaction.Contexts,",
        "if (scopes.Count < 0 && !HasRequiredScope(scopes, resourceType, interaction.Access, interaction.Contexts,",
        ["scope.unrelated", "scope.write-only", "scope.lowercase-route"],
    ),
    "audience-bypass": (
        "src/services/fhir-service/Services/Identity/SmartTokenValidation.cs",
        "&& audiences?.Any(a => issuer.Audiences.Contains(a, StringComparer.Ordinal)) == true;",
        "&& audiences != null;",
        ["identity.wrong-audience", "identity.issuer-audience-confusion"],
    ),
}


P2P_MUTATIONS = {
    "consent-member-bypass": (
        "src/services/fhir-service/Services/PayerToPayer/PdexConsent.cs",
        "if (PatientIdentifiers.Overlaps(ids!)) return true;",
        "if (ids.Count > 0) return true;",
        ["export.consent.wrong-patient", "match.consent.wrong-patient"],
    ),
    "consent-cache-key-bypass": (
        "src/services/fhir-service/Services/PayerToPayer/PayerToPayerConsentAttestations.cs",
        "_collection.Find(Key(tenantId, memberId, receivingPayerId))",
        "_collection.Find(new BsonDocument(\"memberId\", memberId))",
        ["cache.foreign-payer", "cache.foreign-tenant"],
    ),
}


PROVIDER_MUTATIONS = {
    "attribution-bypass": (
        "src/services/fhir-service/Services/ProviderAccess/ProviderAccessAuthorizationService.cs",
        "if (!attributed)", "if (!attributed && request.MemberId.Length < 0)",
        ["attribution.foreign-member", "attribution.lowercase", "attribution.query-spoof"],
    ),
    "provider-consent-bypass": (
        "src/services/fhir-service/Services/ProviderAccess/ProviderAccessAuthorizationService.cs",
        "if (!consent.Allowed)", "if (!consent.Allowed && request.MemberId.Length < 0)",
        ["consent.activated-denial", "consent.system-denial", "consent.lowercase-denial", "consent.search-denial", "consent.withdrawn-permit-denial", "consent.deny-overrides-permit", "registry.outage-denial"],
    ),
}


DOCUMENT_MUTATIONS = {
    "catalog-sensitivity-bypass": (
        "src/services/document-service/Services/SubjectAccess.cs",
        "return actor.HasPermission(ChoRolePermissions.DocumentsReadSensitive)",
        "return actor.IsAuthenticated",
        ["sensitive.reader-denial", "sensitive.wildcard-denial", "sensitive.list-withholding", "sensitive.hidden-dedup", "attachment.sensitive-denial"],
    ),
    "attachment-document-permission-bypass": (
        "src/services/attachment-service/Controllers/AttachmentsController.cs",
        "if (!_actor.HasPermission(ChoRolePermissions.DocumentsRead)",
        "if (false && !_actor.HasPermission(ChoRolePermissions.DocumentsRead)",
        ["attachment.document-permission"],
    ),
}


def run(member, output, coverage=None, claims=None, consent=None, documents=False):
    profile = "p2p-consent" if coverage is not None else "fhir-smart"
    service_args = [str(member)] if coverage is None else [str(member), str(coverage), str(claims)]
    count = 55 if coverage is not None else 42
    mutations = P2P_MUTATIONS if coverage is not None else FHIR_MUTATIONS
    controls = (["export.inline-control", "export.cache-control", "match.control", "match.export-control", "tenant.b.inline-control"]
                if coverage is not None else ["identity.valid-control", "identity.second-issuer-control", "cho.reader-control", "bulk.create-control"])
    if consent is not None:
        profile, count, mutations = "provider-access", 30, PROVIDER_MUTATIONS
        service_args = [str(member), str(consent)]
        controls = ["provider.control", "provider.system-control", "tenant.b.control", "consent.patient-self-control", "consent.other-member-control", "consent.other-tenant-control", "consent.revoked-optout-control", "consent.repermit-control", "registry.pre-outage-control", "registry.patient-self-control"]
    primary_service = "fhir-service"
    if documents:
        profile, count, mutations = "documents", 39, DOCUMENT_MUTATIONS
        primary_service, service_args = "document-service", [str(member)]
        controls = ["catalog.download-control", "catalog.tenant-b-control", "catalog.clean-restoration", "sensitive.exact-control", "sensitive.part2-denial", "attachment.download-control", "attachment.audit-identity", "audit.recovery-control"]
    if subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT).strip():
        raise RuntimeError("Sensitivity requires a clean committed checkout")
    source = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    os.umask(0o077)
    output.mkdir(mode=0o700, parents=False, exist_ok=False)
    records = []
    runner = ROOT / "tools/SecurityChallenge/bin/Release/net8.0/SecurityChallenge.dll"
    baseline = output / "baseline"
    subprocess.run(["dotnet", str(runner), profile,
                    str(ROOT / f"src/services/{primary_service}/bin/Release/net8.0/{primary_service}.dll"),
                    *service_args, str(baseline)], cwd=ROOT, check=True)
    baseline_results = [json.loads(line) for line in (baseline / "scenario-results.jsonl").read_text().splitlines()]
    baseline_manifest = json.loads((baseline / "manifest.json").read_text())
    catalog = {r["id"] for r in baseline_results}
    if (len(baseline_results) != count or len(catalog) != count or not baseline_manifest["completed"]
            or baseline_manifest["sourceDirty"] or baseline_manifest["sourceCommit"] != source
            or any(r["outcome"] != "PASS" for r in baseline_results)):
        raise RuntimeError("Healthy clean-source baseline required before sensitivity")
    for name, (file, before, after, expected) in mutations.items():
        packet = output / name
        with tempfile.TemporaryDirectory(prefix="cho-fhir-mutant-") as temporary:
            temporary = Path(temporary)
            archive = temporary / "source.tar"
            with archive.open("wb") as stream:
                subprocess.run(["git", "archive", source], cwd=ROOT, stdout=stream, check=True)
            checkout = temporary / "source"
            checkout.mkdir()
            with tarfile.open(archive) as tar:
                tar.extractall(checkout, filter="data")
            target = checkout / file
            content = target.read_text()
            if content.count(before) != 1:
                raise RuntimeError("Mutation anchor must match exactly once: " + name)
            mutated = content.replace(before, after)
            target.write_text(mutated)
            mutated_service = ("attachment-service" if file.startswith("src/services/attachment-service/") else primary_service)
            # Build failures are setup errors, never evidence that a mutant was detected.
            with (output / (name + "-build.log")).open("w") as log:
                subprocess.run(["dotnet", "build", "src/services/" + mutated_service, "--configuration", "Release",
                                "--disable-build-servers", "-m:1", "-p:UseSharedCompilation=false", "--verbosity", "quiet"],
                               cwd=checkout, stdout=log, stderr=subprocess.STDOUT, check=True)
            mutant = checkout / f"src/services/{mutated_service}/bin/Release/net8.0/{mutated_service}.dll"
            mutant_args = [str(mutant), *service_args]
            if documents and mutated_service == "attachment-service":
                mutant_args = [str(ROOT / "src/services/document-service/bin/Release/net8.0/document-service.dll"), str(mutant)]
            process = subprocess.run(["dotnet", str(runner), profile, *mutant_args, str(packet)], cwd=ROOT)
            manifest = json.loads((packet / "manifest.json").read_text())
            results = [json.loads(line) for line in (packet / "scenario-results.jsonl").read_text().splitlines()]
            by_id = {r["id"]: r["outcome"] for r in results}
            detected = (process.returncode == 1 and manifest["completed"] and not manifest["sourceDirty"]
                        and manifest["sourceCommit"] == source and len(results) == len(catalog) and set(by_id) == catalog
                        and all(by_id.get(i) == "FAIL" for i in expected)
                        and all(by_id.get(i) == "PASS" for i in controls)
                        and all(r["outcome"] in ("PASS", "FAIL") for r in results))
            record = {"mutation": name, "sourceCommit": source, "file": file,
                      "originalSourceSha256": hashlib.sha256(content.encode()).hexdigest(),
                      "mutatedSourceSha256": hashlib.sha256(mutated.encode()).hexdigest(),
                      "expectedFailures": expected, "controls": controls, "detected": detected,
                      "packetManifestSha256": hashlib.sha256((packet / "manifest.json").read_bytes()).hexdigest()}
            records.append(record)
            # The nested manifest identifies the runner checkout and binary hashes;
            # this record explicitly identifies the different service source.
            (output / "sensitivity-results.json").write_text(json.dumps({"sourceCommit": source, "profile": profile,
                "serviceSource": "disposable-mutated-git-archive", "publication": "REQUIRES_REVIEW", "mutations": records}, indent=2) + "\n")
            if not detected:
                raise RuntimeError("Mutant was not detected with healthy controls: " + name)
            print("Detected controlled mutation: " + name, flush=True)
    files = sorted(p for p in output.rglob("*") if p.is_file())
    (output / "artifact-checksums.txt").write_text("".join(hashlib.sha256(p.read_bytes()).hexdigest() + "  " + str(p.relative_to(output)) + "\n" for p in files))
    for path in output.rglob("*"):
        os.chmod(path, 0o700 if path.is_dir() else 0o600)


if __name__ == "__main__":
    if len(sys.argv) == 4 and sys.argv[1] == "documents":
        if not Path(sys.argv[2]).is_file() or Path(sys.argv[3]).exists():
            sys.exit("Provide built attachment assembly and a new evidence directory")
        run(Path(sys.argv[2]).resolve(), Path(sys.argv[3]).resolve(), documents=True)
    elif len(sys.argv) == 5 and sys.argv[1] == "provider-access":
        if not all(Path(p).is_file() for p in sys.argv[2:4]) or Path(sys.argv[4]).exists():
            sys.exit("Provide built member and consent assemblies and a new evidence directory")
        run(Path(sys.argv[2]).resolve(), Path(sys.argv[4]).resolve(), consent=Path(sys.argv[3]).resolve())
    elif len(sys.argv) == 6 and sys.argv[1] == "p2p-consent":
        if not all(Path(p).is_file() for p in sys.argv[2:5]) or Path(sys.argv[5]).exists():
            sys.exit("Provide built member, coverage and claims assemblies and a new evidence directory")
        run(Path(sys.argv[2]).resolve(), Path(sys.argv[5]).resolve(), Path(sys.argv[3]).resolve(), Path(sys.argv[4]).resolve())
    elif len(sys.argv) == 3 and Path(sys.argv[1]).is_file() and not Path(sys.argv[2]).exists():
        run(Path(sys.argv[1]).resolve(), Path(sys.argv[2]).resolve())
    else:
        sys.exit("Usage: run-fhir-sensitivity.py documents <attachment-service.dll> <new-evidence-directory> OR [p2p-consent] <member-service.dll> [<coverage-service.dll> <claims-service.dll>] <new-evidence-directory>")
