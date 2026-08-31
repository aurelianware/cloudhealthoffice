#!/usr/bin/env python3
"""
CloudHealthOffice HTTP endpoint inventory.

Scans every ASP.NET controller under src/services (and the portal) for
attribute-routed HTTP endpoints and reports, per endpoint:

  * HTTP method
  * Route template (class [Route] combined with the action route)
  * Owning service / project
  * Whether the endpoint (or its controller/assembly) is protected by
    [Authorize] and/or marked [AllowAnonymous]

The output is a Markdown table plus a per-service summary. It is intentionally
a lightweight static scan (regex, not Roslyn) so it can run in CI without a
build. Re-run it and diff the output to keep docs/audits/api-security-inventory.md
honest as controllers change.

Usage:
    python3 scripts/audits/inventory-endpoints.py            # markdown to stdout
    python3 scripts/audits/inventory-endpoints.py --json     # machine-readable
    python3 scripts/audits/inventory-endpoints.py --summary  # per-service counts only
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from dataclasses import dataclass, asdict
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
SCAN_ROOTS = [REPO_ROOT / "src" / "services", REPO_ROOT / "src" / "portal"]

HTTP_ATTR = re.compile(
    r'\[Http(Get|Post|Put|Delete|Patch|Head|Options)(?:\(\s*"?([^")]*)"?\s*\))?\]'
)
ROUTE_ATTR = re.compile(r'\[Route\(\s*"([^"]*)"\s*\)\]')
AUTHORIZE_ATTR = re.compile(r'\[Authorize')
ALLOW_ANON_ATTR = re.compile(r'\[AllowAnonymous')
CONTROLLER_DECL = re.compile(r'class\s+(\w+)Controller\b')


@dataclass
class Endpoint:
    service: str
    controller: str
    method: str
    route: str
    controller_authorize: bool
    action_authorize: bool
    allow_anonymous: bool
    file: str
    line: int

    @property
    def protected(self) -> bool:
        if self.allow_anonymous:
            return False
        return self.controller_authorize or self.action_authorize


def service_name(path: Path) -> str:
    rel = path.relative_to(REPO_ROOT)
    parts = rel.parts
    # src/services/<name>/...  or  src/portal/<name>/...
    if len(parts) >= 3:
        return parts[2]
    return parts[-1]


def combine_route(class_route: str, action_route: str, controller: str) -> str:
    def subst(tok: str) -> str:
        return (tok or "").replace("[controller]", controller).replace(
            "[action]", ""
        )

    class_route = subst(class_route)
    action_route = subst(action_route)
    if action_route.startswith("/"):
        combined = action_route
    elif action_route:
        combined = f"{class_route.rstrip('/')}/{action_route}"
    else:
        combined = class_route
    combined = "/" + combined.strip("/")
    return combined


def scan_file(path: Path) -> list[Endpoint]:
    text = path.read_text(encoding="utf-8", errors="replace")
    lines = text.splitlines()

    controller_match = CONTROLLER_DECL.search(text)
    controller = controller_match.group(1) if controller_match else path.stem

    # Class-level route/authorize appear before the class declaration.
    class_decl_idx = controller_match.start() if controller_match else len(text)
    header = text[:class_decl_idx]
    class_route_m = ROUTE_ATTR.search(header)
    class_route = class_route_m.group(1) if class_route_m else "[controller]"
    controller_authorize = bool(AUTHORIZE_ATTR.search(header))

    endpoints: list[Endpoint] = []
    svc = service_name(path)

    for i, line in enumerate(lines):
        m = HTTP_ATTR.search(line)
        if not m:
            continue
        verb = m.group(1).upper()
        action_route = m.group(2) or ""
        # Look back a few lines for action-level [Authorize]/[AllowAnonymous]
        window = "\n".join(lines[max(0, i - 6): i + 1])
        action_authorize = bool(AUTHORIZE_ATTR.search(window))
        allow_anon = bool(ALLOW_ANON_ATTR.search(window))
        route = combine_route(class_route, action_route, controller)
        endpoints.append(
            Endpoint(
                service=svc,
                controller=f"{controller}Controller",
                method=verb,
                route=route,
                controller_authorize=controller_authorize,
                action_authorize=action_authorize,
                allow_anonymous=allow_anon,
                file=str(path.relative_to(REPO_ROOT)),
                line=i + 1,
            )
        )
    return endpoints


def collect() -> list[Endpoint]:
    endpoints: list[Endpoint] = []
    for root in SCAN_ROOTS:
        if not root.exists():
            continue
        for path in root.rglob("*Controller.cs"):
            if any(part in ("bin", "obj") for part in path.parts):
                continue
            if "Tests" in path.parts or path.name.endswith("Tests.cs"):
                continue
            endpoints.extend(scan_file(path))
    endpoints.sort(key=lambda e: (e.service, e.route, e.method))
    return endpoints


def render_markdown(endpoints: list[Endpoint]) -> str:
    out: list[str] = []
    total = len(endpoints)
    protected = sum(1 for e in endpoints if e.protected)
    anon = sum(1 for e in endpoints if e.allow_anonymous)
    mutations = sum(1 for e in endpoints if e.method in ("POST", "PUT", "DELETE", "PATCH"))
    out.append(f"# API Endpoint Inventory (generated)\n")
    out.append(
        f"- Total endpoints: **{total}**\n"
        f"- Protected by `[Authorize]` (controller or action, not overridden by AllowAnonymous): **{protected}** "
        f"({protected * 100 // max(total,1)}%)\n"
        f"- Explicit `[AllowAnonymous]`: **{anon}**\n"
        f"- Mutating (POST/PUT/DELETE/PATCH): **{mutations}**\n"
    )

    # Per-service summary
    out.append("\n## Per-service summary\n")
    out.append("| Service | Endpoints | Protected | Unprotected | Mutations |")
    out.append("|---|---:|---:|---:|---:|")
    svcs: dict[str, list[Endpoint]] = {}
    for e in endpoints:
        svcs.setdefault(e.service, []).append(e)
    for svc in sorted(svcs):
        eps = svcs[svc]
        prot = sum(1 for e in eps if e.protected)
        muts = sum(1 for e in eps if e.method in ("POST", "PUT", "DELETE", "PATCH"))
        out.append(
            f"| {svc} | {len(eps)} | {prot} | {len(eps) - prot} | {muts} |"
        )

    # Full table
    out.append("\n## Endpoints\n")
    out.append("| Service | Method | Route | Auth | Controller | Location |")
    out.append("|---|---|---|---|---|---|")
    for e in endpoints:
        if e.allow_anonymous:
            auth = "AllowAnonymous"
        elif e.protected:
            auth = "Authorize"
        else:
            auth = "**none**"
        out.append(
            f"| {e.service} | {e.method} | `{e.route}` | {auth} | {e.controller} | {e.file}:{e.line} |"
        )
    out.append("")
    return "\n".join(out)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--json", action="store_true", help="emit JSON")
    ap.add_argument("--summary", action="store_true", help="print per-service counts only")
    args = ap.parse_args()

    endpoints = collect()

    if args.json:
        print(json.dumps([asdict(e) for e in endpoints], indent=2))
        return 0

    if args.summary:
        total = len(endpoints)
        protected = sum(1 for e in endpoints if e.protected)
        print(f"endpoints={total} protected={protected} unprotected={total - protected}")
        svcs: dict[str, int] = {}
        for e in endpoints:
            svcs[e.service] = svcs.get(e.service, 0) + 1
        for svc in sorted(svcs):
            print(f"  {svc}: {svcs[svc]}")
        return 0

    print(render_markdown(endpoints))
    return 0


if __name__ == "__main__":
    sys.exit(main())
