# Developer Guide

This guide orients contributors to the repository. CloudHealthOffice is a
multi-service .NET, Blazor, Kubernetes, FHIR, and X12 platform, so start small
and follow the local patterns in the service you are touching.

## Repository Layout

```text
src/
  services/        Microservices and APIs
  engines/         Domain engines used by services
  portal/          Blazor Server operations portal
  tools/           Benchmark and developer tools
  site/            Marketing and documentation website
docs/              Architecture, domain, benchmark, compliance, deployment docs
tests/             Unit, integration, and service-level tests
```

## Running Locally

Start with the current quickstart:

```bash
docker compose --profile core up -d
curl http://localhost:5001/health/live
```

Then use the service-specific docs and run only the services needed for your
change. The portal is commonly used for claims and mass-adjudication workflows.

## Building

Use targeted builds where possible:

```bash
dotnet build cloudhealthoffice-main.sln
dotnet build src/services/claims-service/claims-service.csproj
dotnet build src/portal/CloudHealthOffice.Portal/CloudHealthOffice.Portal.csproj
```

Some workflows use Docker or Kubernetes dependencies. Prefer documenting any
local dependency you discover instead of hiding it in a one-off script.

## Testing

Run the smallest meaningful test first:

```bash
dotnet test tests/CloudHealthOffice.MccPlatformValidator.Tests/CloudHealthOffice.MccPlatformValidator.Tests.csproj
dotnet test src/portal/CloudHealthOffice.Portal.Tests/CloudHealthOffice.Portal.Tests.csproj
```

Then broaden to affected services or solution-level tests when the change touches
shared contracts or behavior.

## Common Workflows

- Claims behavior: start with `src/services/claims-service`, related engines, and
  the MCC validator.
- Portal behavior: start with `src/portal/CloudHealthOffice.Portal` and portal
  tests.
- Benchmark evidence: start with `src/tools/mcc-platform-validator` and
  `docs/million-claim-challenge`.
- FHIR behavior: start with `src/services/fhir-service`, `src/fhir`, and
  architecture docs.
- Terminology behavior: start with `src/services/CHO.TerminologyService`.

## Additional Guides

- [Coding standards](coding-standards.md)
- [Debugging](debugging.md)
- [CI/CD](ci-cd.md)
- [Testing guide](../../tests/README.md)
- [Security policy](../../SECURITY.md)
