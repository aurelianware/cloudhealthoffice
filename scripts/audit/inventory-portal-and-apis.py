#!/usr/bin/env python3
"""Inventory CloudHealthOffice portal routes and HTTP API endpoints.

This is a static source scan used by the portal UX/security audit. It does not
call live services. Re-run after adding pages or controllers:

    python3 scripts/audit/inventory-portal-and-apis.py
    python3 scripts/audit/inventory-portal-and-apis.py --write-docs

The optional --write-docs flag regenerates:

    docs/audits/generated/portal-route-inventory.md
    docs/audits/generated/api-endpoint-inventory.md
"""

from __future__ import annotations

import argparse
import re
import sys
from collections import defaultdict
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
PORTAL_PAGES = REPO_ROOT / "src" / "portal" / "CloudHealthOffice.Portal" / "Pages"
SERVICES_ROOT = REPO_ROOT / "src" / "services"
PORTAL_PROGRAM = REPO_ROOT / "src" / "portal" / "CloudHealthOffice.Portal" / "Program.cs"
GENERATED_DIR = REPO_ROOT / "docs" / "audits" / "generated"

HTTP_ATTR = re.compile(
    r"\[Http(Get|Post|Put|Patch|Delete|Head)\((?:\"([^\"]*)\")?\)\]",
    re.IGNORECASE,
)
ROUTE_ATTR = re.compile(r"\[Route\(\"([^\"]+)\"\)\]")
AUTHORIZE_ATTR = re.compile(r"\[Authorize(?:\(([^\]]*)\))?\]")
ALLOW_ANON_ATTR = re.compile(r"\[AllowAnonymous\]")
PAGE_DIRECTIVE = re.compile(r"@page\s+\"([^\"]+)\"")
PERMISSION_GATE = re.compile(
    r"<PermissionGate(?:\s+Permission=\"([^\"]*)\")?(?:\s+RoleName=\"([^\"]*)\")?"
)
MAP_MINIMAL = re.compile(
    r"app\.Map(Get|Post|Put|Patch|Delete)\(\s*\"([^\"]+)\"",
    re.IGNORECASE,
)


@dataclass
class PortalRoute:
    file: str
    routes: list[str]
    authorize: bool
    allow_anonymous: bool
    permission: str
    role_name: str


@dataclass
class ApiEndpoint:
    service: str
    file: str
    class_name: str
    method: str
    route: str
    class_authorize: str
    method_authorize: str
    allow_anonymous: bool
    mutation: bool


def rel(path: Path) -> str:
    return str(path.relative_to(REPO_ROOT))


def scan_portal_pages() -> list[PortalRoute]:
    results: list[PortalRoute] = []
    if not PORTAL_PAGES.exists():
        return results
    for path in sorted(PORTAL_PAGES.rglob("*.razor")):
        text = path.read_text(encoding="utf-8", errors="replace")
        routes = PAGE_DIRECTIVE.findall(text)
        if not routes:
            continue
        gate = PERMISSION_GATE.search(text)
        results.append(
            PortalRoute(
                file=rel(path),
                routes=routes,
                authorize="[Authorize]" in text or "[Microsoft.AspNetCore.Authorization.Authorize]" in text,
                allow_anonymous="[AllowAnonymous]" in text,
                permission=(gate.group(1) or "") if gate else "",
                role_name=(gate.group(2) or "") if gate else "",
            )
        )
    return results


def extract_class_name(text: str) -> str:
    match = re.search(r"class\s+(\w+)\s*:", text)
    return match.group(1) if match else "(unknown)"


def class_route_prefix(text: str) -> str:
    # First [Route] before the first HTTP method is treated as the controller prefix.
    first_http = HTTP_ATTR.search(text)
    search_region = text[: first_http.start()] if first_http else text
    match = ROUTE_ATTR.search(search_region)
    return match.group(1) if match else ""


def combine_route(prefix: str, template: str, method_route: str) -> str:
    parts = []
    for part in (prefix, template, method_route):
        if not part:
            continue
        parts.append(part.strip("/"))
    combined = "/" + "/".join(p for p in parts if p)
    combined = combined.replace("[controller]", "").replace("//", "/")
    return combined if combined != "/" else "/"


def scan_controller(path: Path, service: str) -> list[ApiEndpoint]:
    text = path.read_text(encoding="utf-8", errors="replace")
    class_name = extract_class_name(text)
    prefix = class_route_prefix(text)
    class_auth = ""
    class_auth_match = AUTHORIZE_ATTR.search(text[: HTTP_ATTR.search(text).start()] if HTTP_ATTR.search(text) else text[:800])
    if class_auth_match:
        class_auth = class_auth_match.group(0)

    endpoints: list[ApiEndpoint] = []
    # Walk HTTP attributes and inspect a window after each for method-level auth.
    for match in HTTP_ATTR.finditer(text):
        http_method = match.group(1).upper()
        method_route = match.group(2) or ""
        # Include attributes that appear immediately above [HttpX].
        lookbehind_start = max(0, match.start() - 400)
        window = text[lookbehind_start : match.start() + 1200]
        sig = re.search(r"public\s+(?:async\s+)?[\w<>,\s]+\s+\w+\s*\(", window)
        header = window[: sig.start()] if sig else window[:800]
        allow_anon = bool(ALLOW_ANON_ATTR.search(header))
        method_auth = AUTHORIZE_ATTR.search(header)
        route = combine_route(prefix, "", method_route)
        endpoints.append(
            ApiEndpoint(
                service=service,
                file=rel(path),
                class_name=class_name,
                method=http_method,
                route=route,
                class_authorize=class_auth,
                method_authorize=method_auth.group(0) if method_auth else "",
                allow_anonymous=allow_anon,
                mutation=http_method in {"POST", "PUT", "PATCH", "DELETE"},
            )
        )
    return endpoints


def scan_program_maps(path: Path, service: str) -> list[ApiEndpoint]:
    if not path.exists():
        return []
    text = path.read_text(encoding="utf-8", errors="replace")
    endpoints: list[ApiEndpoint] = []
    for match in MAP_MINIMAL.finditer(text):
        http_method = match.group(1).upper()
        route = match.group(2)
        window = text[match.start() : match.start() + 2500]
        allow_anon = "AllowAnonymous" in window
        endpoints.append(
            ApiEndpoint(
                service=service,
                file=rel(path),
                class_name="Program",
                method=http_method,
                route=route,
                class_authorize="",
                method_authorize="",
                allow_anonymous=allow_anon,
                mutation=http_method in {"POST", "PUT", "PATCH", "DELETE"},
            )
        )
    return endpoints


def scan_apis() -> list[ApiEndpoint]:
    endpoints: list[ApiEndpoint] = []
    if SERVICES_ROOT.exists():
        for service_dir in sorted(p for p in SERVICES_ROOT.iterdir() if p.is_dir()):
            service = service_dir.name
            for controller in service_dir.rglob("*Controller*.cs"):
                if "/obj/" in str(controller) or "/bin/" in str(controller) or ".Tests" in str(controller):
                    continue
                endpoints.extend(scan_controller(controller, service))
            endpoints.extend(scan_program_maps(service_dir / "Program.cs", service))
    endpoints.extend(scan_program_maps(PORTAL_PROGRAM, "portal"))
    return endpoints


def auth_label(route: PortalRoute) -> str:
    if route.allow_anonymous:
        return "AllowAnonymous"
    if route.authorize:
        return "Authorize"
    return "NONE"


def endpoint_protection(ep: ApiEndpoint) -> str:
    if ep.allow_anonymous:
        return "AllowAnonymous"
    if ep.method_authorize:
        return ep.method_authorize
    if ep.class_authorize:
        return ep.class_authorize
    return "NONE"


def is_authorized(ep: ApiEndpoint) -> bool:
    return endpoint_protection(ep) not in {"NONE", "AllowAnonymous"}


def markdown_portal(routes: list[PortalRoute]) -> str:
    lines = [
        "# Generated portal route inventory",
        "",
        f"Generated by `scripts/audit/inventory-portal-and-apis.py` on {datetime.now(timezone.utc).strftime('%Y-%m-%d %H:%M UTC')}.",
        "Do not edit by hand. Re-run the script after adding or renaming Razor pages.",
        "",
        "| Route | File | Auth attribute | PermissionGate | Role label |",
        "| --- | --- | --- | --- | --- |",
    ]
    for item in routes:
        for route in item.routes:
            lines.append(
                f"| `{route}` | `{item.file}` | {auth_label(item)} | `{item.permission or '—'}` | {item.role_name or '—'} |"
            )
    missing = [r for r in routes if not r.authorize and not r.allow_anonymous]
    lines.extend(["", f"**Pages with neither `[Authorize]` nor `[AllowAnonymous]`:** {len(missing)}"])
    for item in missing:
        lines.append(f"- `{', '.join(item.routes)}` — `{item.file}`")
    return "\n".join(lines) + "\n"


def markdown_apis(endpoints: list[ApiEndpoint]) -> str:
    lines = [
        "# Generated HTTP endpoint inventory",
        "",
        f"Generated by `scripts/audit/inventory-portal-and-apis.py` on {datetime.now(timezone.utc).strftime('%Y-%m-%d %H:%M UTC')}.",
        "Static source scan of controllers and `MapGet`/`MapPost` endpoints. Not a live traffic capture.",
        "",
        "## Summary by service",
        "",
        "| Service | Endpoints | Mutations | Class/method `[Authorize]` | `[AllowAnonymous]` | No auth attribute |",
        "| --- | ---: | ---: | ---: | ---: | ---: |",
    ]
    by_service: dict[str, list[ApiEndpoint]] = defaultdict(list)
    for ep in endpoints:
        by_service[ep.service].append(ep)
    for service in sorted(by_service):
        items = by_service[service]
        mutations = sum(1 for e in items if e.mutation)
        authorized = sum(1 for e in items if is_authorized(e))
        anon = sum(1 for e in items if e.allow_anonymous)
        none = sum(1 for e in items if endpoint_protection(e) == "NONE")
        lines.append(
            f"| `{service}` | {len(items)} | {mutations} | {authorized} | {anon} | {none} |"
        )

    none_mutations = [
        e for e in endpoints if e.mutation and endpoint_protection(e) == "NONE"
    ]
    lines.extend(
        [
            "",
            f"**Mutation endpoints with no `[Authorize]` / `[AllowAnonymous]` attribute:** {len(none_mutations)}",
            "",
            "These are not automatically anonymous — ASP.NET Core requires a fallback policy or `[Authorize]` — but CloudHealthOffice services generally do **not** set a fallback authorization policy, so these routes are reachable without an authenticated user unless another control exists (network policy, API key middleware, environment gate).",
            "",
            "| Service | Method | Route | File |",
            "| --- | --- | --- | --- |",
        ]
    )
    for ep in none_mutations:
        lines.append(f"| `{ep.service}` | {ep.method} | `{ep.route}` | `{ep.file}` |")

    lines.extend(["", "## Full endpoint list", "", "| Service | Method | Route | Protection | Mutation | File |", "| --- | --- | --- | --- | --- | --- |"])
    for ep in endpoints:
        lines.append(
            f"| `{ep.service}` | {ep.method} | `{ep.route}` | {endpoint_protection(ep)} | {'yes' if ep.mutation else 'no'} | `{ep.file}` |"
        )
    return "\n".join(lines) + "\n"


def print_summary(routes: list[PortalRoute], endpoints: list[ApiEndpoint]) -> None:
    print(f"Portal pages with @page: {len(routes)}")
    print(f"  Authorize: {sum(1 for r in routes if r.authorize)}")
    print(f"  AllowAnonymous: {sum(1 for r in routes if r.allow_anonymous)}")
    print(f"  Neither: {sum(1 for r in routes if not r.authorize and not r.allow_anonymous)}")
    print(f"  PermissionGate present: {sum(1 for r in routes if r.permission or '<PermissionGate>' in Path(REPO_ROOT / r.file).read_text(encoding='utf-8', errors='replace'))}")
    print()
    print(f"HTTP endpoints scanned: {len(endpoints)}")
    print(f"  Mutations: {sum(1 for e in endpoints if e.mutation)}")
    print(f"  AllowAnonymous: {sum(1 for e in endpoints if e.allow_anonymous)}")
    print(f"  Authorize (class or method): {sum(1 for e in endpoints if is_authorized(e))}")
    print(f"  No auth attribute: {sum(1 for e in endpoints if endpoint_protection(e) == 'NONE')}")
    print(f"  Mutations with no auth attribute: {sum(1 for e in endpoints if e.mutation and endpoint_protection(e) == 'NONE')}")
    print()
    print("Pages with neither Authorize nor AllowAnonymous:")
    for item in routes:
        if not item.authorize and not item.allow_anonymous:
            print(f"  {', '.join(item.routes)}  ({item.file})")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--write-docs",
        action="store_true",
        help="Write generated markdown inventories under docs/audits/generated/",
    )
    args = parser.parse_args()

    routes = scan_portal_pages()
    endpoints = scan_apis()
    print_summary(routes, endpoints)

    if args.write_docs:
        GENERATED_DIR.mkdir(parents=True, exist_ok=True)
        portal_path = GENERATED_DIR / "portal-route-inventory.md"
        api_path = GENERATED_DIR / "api-endpoint-inventory.md"
        portal_path.write_text(markdown_portal(routes), encoding="utf-8")
        api_path.write_text(markdown_apis(endpoints), encoding="utf-8")
        print()
        print(f"Wrote {rel(portal_path)}")
        print(f"Wrote {rel(api_path)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
