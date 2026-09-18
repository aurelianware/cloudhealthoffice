# Audits

Structured reviews of CloudHealthOffice as it exists in this repository.

| Document | Purpose |
| --- | --- |
| [Portal UX, workflow, and security audit](portal-ux-security-audit.md) | Inventory of the Blazor operations portal, payer personas and workflows, authentication, authorization, API exposure, tenant isolation, PHI handling, auditability, and a recommended implementation roadmap. |
| [Generated portal route inventory](generated/portal-route-inventory.md) | Machine-generated `@page` table. Do not edit by hand. |
| [Generated HTTP endpoint inventory](generated/api-endpoint-inventory.md) | Machine-generated controller and minimal-API table. Do not edit by hand. |

Regenerate the machine inventories:

```bash
python3 scripts/audit/inventory-portal-and-apis.py --write-docs
```

The portal test project also guards route-auth declarations:

```bash
dotnet test src/portal/CloudHealthOffice.Portal.Tests/CloudHealthOffice.Portal.Tests.csproj --filter FullyQualifiedName~PortalRouteAuthInventoryTests
```
