#!/usr/bin/env python3
"""Download the CMS code-set releases and load them into terminology-service.

Reads scripts/terminology/cms-code-sets.json (one entry per CMS release: the
zip URL, the file inside it, the version and its first covered date), downloads
each zip from cms.gov, takes the ICD-10 order file or the HCPCS spreadsheet out
of it, and posts it to terminology-service's POST /admin/code-sets/load. Then it
runs the manifest's spot checks through CodeSystem/$validate-code.

Releases already loaded (same system and version) are skipped unless --force,
so the script is safe to run again. Loading needs a platform:admin token:

  CHO_TOKEN=...  load_cms_code_sets.py --base-url https://terminology.example
  load_cms_code_sets.py --base-url http://localhost:5080 --dev-token   # docker compose / demo (development key)
  load_cms_code_sets.py --dry-run                                      # download and parse only

The HCPCS spreadsheet is converted to CSV with openpyxl (pip install openpyxl).
"""
from __future__ import annotations

import argparse
import csv
import datetime as dt
import fnmatch
import io
import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import zipfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
DEFAULT_MANIFEST = HERE / "cms-code-sets.json"
USER_AGENT = "cho-code-set-loader/1.0"


def log(msg: str) -> None:
    print(msg, flush=True)


# ── Download and extract ───────────────────────────────────────────────────

def download(url: str, cache: Path) -> Path:
    cache.mkdir(parents=True, exist_ok=True)
    target = cache / Path(urllib.parse.urlparse(url).path).name
    if target.exists() and target.stat().st_size > 0:
        return target
    last_error: Exception | None = None
    for attempt in range(4):
        try:
            req = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
            with urllib.request.urlopen(req, timeout=120) as resp:
                data = resp.read()
            if not data.startswith(b"PK"):
                raise RuntimeError(f"{url} did not return a zip file")
            target.write_bytes(data)
            return target
        except urllib.error.HTTPError as e:
            if e.code == 404:
                raise RuntimeError(f"{url} returned 404: CMS moved or renamed the file; update cms-code-sets.json") from e
            last_error = e
        except (urllib.error.URLError, TimeoutError, ConnectionError) as e:
            last_error = e
        time.sleep(2 ** attempt)
    raise RuntimeError(f"could not download {url}: {last_error}")


def find_member(archive: zipfile.ZipFile, pattern: str) -> tuple[str, bytes] | None:
    """The largest file matching the glob (case-insensitive), looking one level into nested zips."""
    skip = ("addend", "transaction", "report", "__macosx")
    matches = [
        i for i in archive.infolist()
        if not i.is_dir()
        and fnmatch.fnmatch(Path(i.filename).name.lower(), pattern.lower())
        and not any(s in i.filename.lower() for s in skip)
    ]
    if matches:
        best = max(matches, key=lambda i: i.file_size)
        return best.filename, archive.read(best)
    for inner in (i for i in archive.infolist() if i.filename.lower().endswith(".zip")):
        with zipfile.ZipFile(io.BytesIO(archive.read(inner))) as nested:
            found = find_member(nested, pattern)
            if found:
                return f"{inner.filename}/{found[0]}", found[1]
    return None


def xlsx_to_csv(content: bytes) -> bytes:
    """The HCPCS spreadsheet as CSV, from its header row (the row with an HCPC column) down."""
    try:
        import openpyxl  # type: ignore
    except ImportError as e:  # pragma: no cover
        raise RuntimeError("converting the HCPCS spreadsheet needs openpyxl: pip install openpyxl") from e

    book = openpyxl.load_workbook(io.BytesIO(content), read_only=True, data_only=True)
    sheet = book.worksheets[0]
    out = io.StringIO()
    writer = csv.writer(out, lineterminator="\n")
    header_seen = False
    for row in sheet.iter_rows(values_only=True):
        cells = [cell_text(c) for c in row]
        if not header_seen:
            if any(c.strip().upper() in ("HCPC", "HCPCS") for c in cells):
                header_seen = True
                writer.writerow([c.strip() for c in cells])
            continue
        if any(cells):
            writer.writerow(cells)
    if not header_seen:
        raise RuntimeError("the HCPCS spreadsheet has no row with an HCPC column")
    return out.getvalue().encode()


def cell_text(value) -> str:
    if value is None:
        return ""
    if isinstance(value, (dt.datetime, dt.date)):
        return value.strftime("%Y%m%d")
    if isinstance(value, float) and value.is_integer():
        return str(int(value))
    return str(value).strip()


def release_file(release: dict, cache: Path) -> tuple[str, bytes]:
    archive_path = download(release["url"], cache)
    with zipfile.ZipFile(archive_path) as archive:
        found = find_member(archive, release["member"])
        if not found:
            names = ", ".join(i.filename for i in archive.infolist()[:20])
            raise RuntimeError(f"{release['label']}: no '{release['member']}' in {archive_path.name} ({names})")
    name, content = found
    if name.lower().endswith(".xlsx"):
        content = xlsx_to_csv(content)
    return name, content


# ── terminology-service ────────────────────────────────────────────────────

def call(method: str, url: str, token: str, body: bytes | None = None, content_type: str | None = None):
    headers = {"Authorization": f"Bearer {token}", "Accept": "application/json", "User-Agent": USER_AGENT}
    if content_type:
        headers["Content-Type"] = content_type
    req = urllib.request.Request(url, data=body, method=method, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=600) as resp:
            raw = resp.read()
            return resp.status, json.loads(raw) if raw else None
    except urllib.error.HTTPError as e:
        raw = e.read()
        try:
            return e.code, json.loads(raw)
        except ValueError:
            return e.code, raw.decode(errors="replace")


def loaded_versions(base: str, token: str, wait: float = 0) -> set[tuple[str, str]]:
    deadline = time.monotonic() + wait
    while True:
        try:
            status, body = call("GET", f"{base}/admin/code-sets", token)
            break
        except (urllib.error.URLError, ConnectionError, TimeoutError) as e:
            if time.monotonic() >= deadline:
                raise RuntimeError(f"could not reach terminology-service at {base}: {e}") from e
            time.sleep(3)
    if status != 200:
        raise RuntimeError(f"GET /admin/code-sets returned {status}: {body}")
    return {(r["system"], r["version"].upper()) for r in body or []}


def load(base: str, token: str, release: dict, content: bytes) -> dict:
    query = {"system": release["system"], "format": release["format"], "version": release["version"]}
    if release.get("effectiveFrom"):
        query["effectiveFrom"] = release["effectiveFrom"]
    if release.get("effectiveTo"):
        query["effectiveTo"] = release["effectiveTo"]
    status, body = call("POST", f"{base}/admin/code-sets/load?{urllib.parse.urlencode(query)}", token,
                        content, "text/plain")
    if status != 200 or not isinstance(body, dict) or not body.get("success"):
        raise RuntimeError(f"{release['label']}: load returned {status}: {body}")
    return body


def run_checks(base: str, token: str, checks: list[dict]) -> int:
    failures = 0
    for c in checks:
        query = urllib.parse.urlencode({"url": c["system"], "code": c["code"], "date": c["date"]})
        status, body = call("GET", f"{base}/fhir/CodeSystem/$validate-code?{query}", token)
        ok = status == 200 and isinstance(body, dict) and body.get("status") == c["status"] \
            and (c.get("reason") is None or body.get("reason") == c["reason"])
        got = f"{body.get('status')} {body.get('reason') or ''}".strip() if isinstance(body, dict) else f"HTTP {status}"
        log(f"  {'ok  ' if ok else 'FAIL'} {c['code']:<8} on {c['date']}: {got} (expected {c['status']} {c.get('reason') or ''})".rstrip())
        failures += 0 if ok else 1
    return failures


# ── Main ───────────────────────────────────────────────────────────────────

def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--base-url", default=os.environ.get("TERMINOLOGY_URL", "http://localhost:5080"))
    parser.add_argument("--manifest", type=Path, default=DEFAULT_MANIFEST)
    parser.add_argument("--cache", type=Path, default=Path(os.environ.get("CMS_CODE_SET_CACHE", "/tmp/cms-code-sets")))
    parser.add_argument("--only", help="load only releases whose label contains this text (ICD-10-CM, HCPCS, ...)")
    parser.add_argument("--force", action="store_true", help="reload releases already loaded")
    parser.add_argument("--dry-run", action="store_true", help="download and parse only; no service calls")
    parser.add_argument("--dev-token", action="store_true",
                        help="mint a platform:admin token with the public development key (local and demo only)")
    parser.add_argument("--skip-checks", action="store_true")
    parser.add_argument("--wait", type=float, default=0, metavar="SECONDS",
                        help="keep trying to reach terminology-service this long before giving up (a service starting up)")
    args = parser.parse_args()

    manifest = json.loads(args.manifest.read_text())
    releases = [r for r in manifest["releases"] if not args.only or args.only.lower() in r["label"].lower()]
    base = args.base_url.rstrip("/")

    token = os.environ.get("CHO_TOKEN", "")
    if not args.dry_run and not token:
        if not args.dev_token:
            log("error: set CHO_TOKEN to a platform:admin token, or pass --dev-token for a local or demo stack")
            return 2
        sys.path.insert(0, str(HERE.parent / "demo" / "gcp"))
        import cho_demo  # type: ignore  # noqa: E402
        token = cho_demo.mint_token(platform_admin=True, hours=2)

    try:
        already = set() if args.dry_run else loaded_versions(base, token, args.wait)
    except RuntimeError as e:
        log(f"error: {e}")
        return 1
    if args.force:
        already = set()
    failures = 0
    for release in releases:
        key = (release["system"], release["version"].upper())
        if key in already:
            log(f"= {release['label']}: already loaded")
            continue
        try:
            name, content = release_file(release, args.cache)
            rows = content.count(b"\n")
            if args.dry_run:
                log(f"✓ {release['label']}: {name}, {rows} rows (dry run, not loaded)")
                continue
            result = load(base, token, release, content)
            adjusted = "; ".join(result.get("adjustedReleases") or [])
            log(f"✓ {release['label']}: {result['codesLoaded']} codes, {result.get('effectiveFrom')} to "
                f"{result.get('effectiveTo') or 'open'}" + (f" ({adjusted})" if adjusted else "")
                + (f", {result['rowsSkipped']} rows skipped" if result.get("rowsSkipped") else ""))
        except RuntimeError as e:
            log(f"✗ {e}")
            failures += 1

    if not args.dry_run and not args.skip_checks and manifest.get("checks"):
        log("Spot checks:")
        failures += run_checks(base, token, manifest["checks"])

    if failures:
        log(f"{failures} problem(s)")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
