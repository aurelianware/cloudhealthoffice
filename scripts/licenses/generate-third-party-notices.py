#!/usr/bin/env python3
"""Generates THIRD-PARTY-NOTICES.md from the packages CHO actually resolves.

  python3 scripts/licenses/generate-third-party-notices.py

Needs the .NET SDK and a restored NuGet cache (it restores what it lists), and
the npm lock files in the repository. Run it after adding or upgrading a
dependency; tests/CloudHealthOffice.Architecture.Tests fails when a package in
Directory.Packages.props is missing from the notices.

- .NET: every package resolved (including transitive ones) by the production
  projects, i.e. every tracked *.csproj that is not a test project. Licence,
  copyright and project URL come from each package's .nuspec.
- npm: production dependencies of the root package-lock.json and of the
  containers' package.json files.
- Test and build tooling (test projects, npm devDependencies) is listed
  separately: it is not shipped in CHO's images.
"""
from __future__ import annotations

import json
import os
import re
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
NUGET = Path(os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget" / "packages"))

# Licences that need a closer look before shipping: copyleft, source-available
# or commercial terms. Anything not recognised as permissive is flagged too.
PERMISSIVE = {
    "MIT", "MIT-0", "Apache-2.0", "BSD-2-Clause", "BSD-3-Clause", "ISC", "0BSD",
    "Unlicense", "MS-PL", "Zlib", "BlueOak-1.0.0", "CC0-1.0", "Python-2.0", "PostgreSQL",
    "CC-BY-4.0", "BSD-2-Clause OR MIT", "(MIT OR Apache-2.0)", "MIT OR Apache-2.0",
    "Apache-2.0 OR MIT", "(MIT OR CC0-1.0)", "MIT OR CC0-1.0", "MS-PL OR Apache-2.0",
}

# Well-known licence URLs used by older packages that predate SPDX expressions.
URL_LICENSES = [
    (r"go\.microsoft\.com/fwlink/\?LinkId=329770|dotnet/corefx/blob/master/LICENSE|dotnet\.microsoft\.com/.*license", "MIT"),
    (r"licenses\.nuget\.org/MIT|opensource\.org/licenses/MIT|/LICENSE\.?(md|txt)?$", None),
    (r"apache\.org/licenses/LICENSE-2\.0|aws\.amazon\.com/apache2\.0", "Apache-2.0"),
    (r"go\.microsoft\.com/fwlink/\?linkid=868514", "MIT"),  # SkiaSharp / HarfBuzzSharp
    (r"confluentinc/librdkafka/blob/master/LICENSES?\.txt", "BSD-2-Clause"),
    (r"opensource\.org/licenses/BSD-3-Clause", "BSD-3-Clause"),
]


def run(cmd: list[str], cwd: Path = ROOT) -> str:
    return subprocess.run(cmd, cwd=cwd, check=True, capture_output=True, text=True).stdout


def tracked(pattern: str) -> list[str]:
    return [p for p in run(["git", "ls-files", pattern]).split("\n") if p]


def is_test_project(path: str) -> bool:
    name = Path(path).stem
    return name.endswith(("Tests", ".Tests", "E2E", "E2ETests")) or "LoadTests" in name or "/tests/" in f"/{path}"


def dotnet_packages(projects: list[str]) -> dict[tuple[str, str], set[str]]:
    """(id, version) -> projects that resolve it, via a scratch solution."""
    found: dict[tuple[str, str], set[str]] = defaultdict(set)
    if not projects:
        return found
    with tempfile.TemporaryDirectory() as tmp:
        run(["dotnet", "new", "sln", "--name", "notices", "--output", tmp, "--force"])
        sln = str(Path(tmp) / "notices.sln")
        run(["dotnet", "sln", sln, "add", *projects])
        run(["dotnet", "restore", sln])
        report = json.loads(run(["dotnet", "list", sln, "package", "--include-transitive", "--format", "json"]))
    for project in report.get("projects", []):
        name = Path(project["path"]).stem
        for framework in project.get("frameworks", []):
            for pkg in framework.get("topLevelPackages", []) + framework.get("transitivePackages", []):
                found[(pkg["id"], pkg["resolvedVersion"])].add(name)
    return found


def nuspec_info(package_id: str, version: str) -> dict[str, str]:
    folder = NUGET / package_id.lower() / version.lower()
    spec = next(iter(folder.glob("*.nuspec")), None) if folder.exists() else None
    info = {"license": "UNKNOWN", "url": "", "copyright": ""}
    if spec is None:
        return info
    meta = ET.parse(spec).getroot().find("{*}metadata")
    if meta is None:
        return info

    def text(tag: str) -> str:
        node = meta.find("{*}" + tag)
        return (node.text or "").strip() if node is not None else ""

    lic = meta.find("{*}license")
    if lic is not None and lic.get("type") == "expression" and lic.text:
        info["license"] = lic.text.strip()
    elif lic is not None and lic.get("type") == "file" and lic.text:
        info["license"] = guess_license_file(folder / lic.text.strip())
    else:
        url = text("licenseUrl")
        info["license"] = guess_license_url(url) or (f"see {url}" if url else "UNKNOWN")
    info["url"] = text("projectUrl")
    info["copyright"] = text("copyright")
    return info


def guess_license_url(url: str) -> str | None:
    for pattern, lic in URL_LICENSES:
        if lic and re.search(pattern, url, re.I):
            return lic
    if re.search(r"licenses\.nuget\.org/([\w.\-]+)", url):
        return re.search(r"licenses\.nuget\.org/([\w.\-+ ]+)", url).group(1)
    return None


def guess_license_file(path: Path) -> str:
    try:
        head = path.read_text(errors="ignore")[:4000]
    except OSError:
        return "see package licence file"
    if re.search(r"Permission is hereby granted, free of charge", head):
        return "MIT"
    if re.search(r"Apache License,?\s+Version 2\.0", head):
        return "Apache-2.0"
    if re.search(r"Redistribution and use in source and binary forms", head):
        return "BSD-3-Clause"
    return "see package licence file"


def npm_packages() -> tuple[dict[tuple[str, str], str], dict[tuple[str, str], str]]:
    prod: dict[tuple[str, str], str] = {}
    dev: dict[tuple[str, str], str] = {}
    for lock_path in tracked("*package-lock.json"):
        if lock_path.startswith("src/site/"):
            # The marketing site's build tooling; nothing from it ships in CHO.
            target = dev
        else:
            target = None
        lock = json.loads((ROOT / lock_path).read_text())
        for path, meta in lock.get("packages", {}).items():
            if not path:
                continue
            name = path.split("node_modules/")[-1]
            key = (name, meta.get("version", "?"))
            lic = meta.get("license") or "UNKNOWN"
            if isinstance(lic, dict):
                lic = lic.get("type", "UNKNOWN")
            bucket = target if target is not None else (dev if meta.get("dev") else prod)
            bucket[key] = lic
    for manifest in tracked("containers/*/package.json"):
        data = json.loads((ROOT / manifest).read_text())
        for name, spec in (data.get("dependencies") or {}).items():
            try:
                info = json.loads(run(["npm", "view", f"{name}@{spec}", "version", "license", "--json"]))
                if isinstance(info, list):
                    info = info[-1]
                prod[(name, info.get("version", spec))] = info.get("license", "UNKNOWN")
            except (subprocess.CalledProcessError, FileNotFoundError, json.JSONDecodeError):
                prod[(name, spec)] = "UNKNOWN"
    return prod, dev


def needs_review(lic: str) -> bool:
    return lic.strip() not in PERMISSIVE


def table(rows: list[tuple[str, str, str, str]]) -> list[str]:
    out = ["| Package | Version | Licence | Project |", "|---|---|---|---|"]
    for name, version, lic, url in rows:
        link = f"[link]({url})" if url.startswith("http") else ""
        out.append(f"| {name} | {version} | {lic} | {link} |")
    return out


def main() -> int:
    projects = tracked("*.csproj")
    prod_projects = [p for p in projects if not is_test_project(p)]
    test_projects = [p for p in projects if is_test_project(p)]

    prod_nuget = dotnet_packages(prod_projects)
    test_nuget = {k: v for k, v in dotnet_packages(test_projects).items() if k not in prod_nuget}
    npm_prod, npm_dev = npm_packages()

    def nuget_rows(pkgs):
        rows = []
        for (pid, ver) in sorted(pkgs, key=lambda k: (k[0].lower(), k[1])):
            info = nuspec_info(pid, ver)
            rows.append((pid, ver, info["license"], info["url"]))
        return rows

    prod_rows = nuget_rows(prod_nuget)
    test_rows = nuget_rows(test_nuget)
    npm_prod_rows = [(n, v, l, f"https://www.npmjs.com/package/{n}") for (n, v), l in sorted(npm_prod.items())]
    npm_dev_rows = [(n, v, l, f"https://www.npmjs.com/package/{n}") for (n, v), l in sorted(npm_dev.items())]

    shipped = prod_rows + npm_prod_rows
    counts: dict[str, int] = defaultdict(int)
    for _, _, lic, _ in shipped:
        counts[lic] += 1
    review = [r for r in shipped if needs_review(r[2])]

    lines = [
        "# Third-party notices",
        "",
        "Cloud Health Office (© 2025-2026 Aurelianware, Inc., Business Source License 1.1; see",
        "[LICENSE](LICENSE) and [NOTICE](NOTICE)) is built on the open-source and third-party",
        "components below. Each remains under its own licence; this file lists them so a",
        "reviewer or a plan deploying CHO can see what it ships and on what terms.",
        "",
        "Generated by `scripts/licenses/generate-third-party-notices.py` from the packages the",
        "build actually resolves (direct and transitive). Do not edit by hand; regenerate after",
        "adding or upgrading a dependency.",
        "",
        "## Summary",
        "",
        f"- **Shipped .NET packages** (production projects, transitive included): {len(prod_rows)}",
        f"- **Shipped npm packages** (production dependencies): {len(npm_prod_rows)}",
        f"- **Test and build tooling** (not shipped): {len(test_rows)} NuGet, {len(npm_dev_rows)} npm",
        "",
        "| Licence | Shipped packages |",
        "|---|---|",
        *[f"| {lic} | {n} |" for lic, n in sorted(counts.items(), key=lambda kv: (-kv[1], kv[0]))],
        "",
        "## Needs attention",
        "",
        "Shipped components whose licence is not a standard permissive one (MIT, Apache-2.0,",
        "BSD, ISC, MS-PL and similar), or could not be determined automatically:",
        "",
        *(table(review) if review else ["None."]),
        "",
        "## Services CHO uses but does not distribute",
        "",
        "CHO connects to these at run time; they are deployed and licensed separately by",
        "whoever runs CHO and are not part of this repository or its images:",
        "",
        "| Service | Licence | Note |",
        "|---|---|---|",
        "| MongoDB Community Server | SSPL-1.0 | Run unmodified as a database server. Offering MongoDB itself as a service triggers SSPL; using it as CHO's database does not. MongoDB Atlas or Enterprise are commercial alternatives. |",
        "| PostgreSQL (reference-data-service) | PostgreSQL | Permissive. |",
        "| Apache Kafka / Azure Service Bus | Apache-2.0 / commercial service | Messaging transport, either one. |",
        "| Redis (benefit-plan-service accumulator store) | RSALv2 / SSPLv1 / AGPLv3 (7.4+); BSD-3-Clause (7.2 and earlier) | Run unmodified as a cache/store; check the version you deploy. |",
        "| MinIO / S3-compatible object storage | AGPL-3.0 (MinIO) | Development emulator and optional storage backend; production can use any S3-compatible or cloud blob store. |",
        "",
        "## Bundled assets",
        "",
        "Files shipped inside CHO's own assemblies rather than as packages:",
        "",
        "| Asset | Licence | Where | Note |",
        "|---|---|---|---|",
        "| Noto Sans Regular and SemiBold 2.015 (subset), © 2022 The Noto Project Authors | SIL OFL 1.1 | idcard-service (`src/services/idcard-service/Fonts/`) and correspondence-service via CloudHealthOffice.PdfLayout (`src/services/shared/CloudHealthOffice.PdfLayout/Fonts/`), licence in `OFL.txt` beside each | Embedded so ID card and letter PDFs render without system fonts; Latin subset built by `scripts/fonts/subset-idcard-fonts.sh`. |",
        "",
        "## .NET packages (shipped)",
        "",
        *table(prod_rows),
        "",
        "## npm packages (shipped)",
        "",
        *table(npm_prod_rows),
        "",
        "## Test and build tooling (not shipped)",
        "",
        "### NuGet (test projects only)",
        "",
        *table(test_rows),
        "",
        "### npm (devDependencies and the marketing site build)",
        "",
        *table(npm_dev_rows),
        "",
    ]
    (ROOT / "THIRD-PARTY-NOTICES.md").write_text("\n".join(lines))
    print(f"Wrote THIRD-PARTY-NOTICES.md: {len(prod_rows)} NuGet + {len(npm_prod_rows)} npm shipped, "
          f"{len(review)} needing attention.")
    for r in review:
        print("  review:", r[0], r[1], r[2])
    return 0


if __name__ == "__main__":
    sys.exit(main())
