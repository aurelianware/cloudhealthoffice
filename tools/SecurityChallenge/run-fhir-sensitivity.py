#!/usr/bin/env python3
"""Build known-broken FHIR variants from HEAD in disposable source copies.

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
MUTATIONS = {
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


def run(member, output):
    if subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT).strip():
        raise RuntimeError("Sensitivity requires a clean committed checkout")
    source = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    os.umask(0o077)
    output.mkdir(mode=0o700, parents=False, exist_ok=False)
    records = []
    runner = ROOT / "tools/SecurityChallenge/bin/Release/net8.0/SecurityChallenge.dll"
    baseline = output / "baseline"
    subprocess.run(["dotnet", str(runner), "fhir-smart",
                    str(ROOT / "src/services/fhir-service/bin/Release/net8.0/fhir-service.dll"),
                    str(member), str(baseline)], cwd=ROOT, check=True)
    baseline_results = [json.loads(line) for line in (baseline / "scenario-results.jsonl").read_text().splitlines()]
    baseline_manifest = json.loads((baseline / "manifest.json").read_text())
    catalog = {r["id"] for r in baseline_results}
    if (len(baseline_results) != 42 or len(catalog) != 42 or not baseline_manifest["completed"]
            or baseline_manifest["sourceDirty"] or baseline_manifest["sourceCommit"] != source
            or any(r["outcome"] != "PASS" for r in baseline_results)):
        raise RuntimeError("Healthy clean-source baseline required before sensitivity")
    for name, (file, before, after, expected) in MUTATIONS.items():
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
            # Build failures are setup errors, never evidence that a mutant was detected.
            with (output / (name + "-build.log")).open("w") as log:
                subprocess.run(["dotnet", "build", "src/services/fhir-service", "--configuration", "Release",
                                "--disable-build-servers", "-m:1", "-p:UseSharedCompilation=false", "--verbosity", "quiet"],
                               cwd=checkout, stdout=log, stderr=subprocess.STDOUT, check=True)
            mutant = checkout / "src/services/fhir-service/bin/Release/net8.0/fhir-service.dll"
            process = subprocess.run(["dotnet", str(runner), "fhir-smart", str(mutant), str(member), str(packet)], cwd=ROOT)
            manifest = json.loads((packet / "manifest.json").read_text())
            results = [json.loads(line) for line in (packet / "scenario-results.jsonl").read_text().splitlines()]
            by_id = {r["id"]: r["outcome"] for r in results}
            controls = ["identity.valid-control", "identity.second-issuer-control", "cho.reader-control", "bulk.create-control"]
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
            (output / "sensitivity-results.json").write_text(json.dumps({"sourceCommit": source,
                "serviceSource": "disposable-mutated-git-archive", "publication": "REQUIRES_REVIEW", "mutations": records}, indent=2) + "\n")
            if not detected:
                raise RuntimeError("Mutant was not detected with healthy controls: " + name)
            print("Detected controlled mutation: " + name, flush=True)
    files = sorted(p for p in output.rglob("*") if p.is_file())
    (output / "artifact-checksums.txt").write_text("".join(hashlib.sha256(p.read_bytes()).hexdigest() + "  " + str(p.relative_to(output)) + "\n" for p in files))
    for path in output.rglob("*"):
        os.chmod(path, 0o700 if path.is_dir() else 0o600)


if __name__ == "__main__":
    if len(sys.argv) != 3 or not Path(sys.argv[1]).is_file() or Path(sys.argv[2]).exists():
        sys.exit("Usage: run-fhir-sensitivity.py <member-service.dll> <new-evidence-directory>")
    run(Path(sys.argv[1]).resolve(), Path(sys.argv[2]).resolve())
