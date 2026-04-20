# Shared JSON Options

**Owner:** Platform · **Status:** Rolled out 2026-04

Every Cloud Health Office service with an HTTP controller surface is expected
to register the same core JSON conventions. This used to be a copy-paste
block in each `Program.cs`; it now lives in the shared
`CloudHealthOffice.Infrastructure` package as a single extension method.

## What it does

`builder.Services.AddControllers().AddCloudHealthOfficeJsonOptions();`
registers exactly one thing today:

- **`JsonStringEnumConverter`** — enums serialize/deserialize as member
  names (e.g. `"LitigationHold"`) instead of their underlying integer
  values. Portal clients and sibling services consistently send and
  receive string enum payloads.

That's it. No naming policy. No null handling. No custom converter
collection.

## Why it is deliberately minimal

Services in this repo have historically differed on wire format:

- Some publish **PascalCase** property names (the framework default).
- Some publish **camelCase** (via `JsonNamingPolicy.CamelCase`).
- Some omit nulls (`DefaultIgnoreCondition.WhenWritingNull`).
- Some don't.

Making the shared extension prescriptive about any of those dimensions
would change a service's wire format the moment it adopts the helper,
silently breaking any client that decoded the old shape. The first
pass therefore sticks to the one convention that is unambiguously
right everywhere (string enums) and leaves the rest under per-service
control.

## What it does NOT do

| Convention                  | Default | Why not shared today |
| --------------------------- | ------- | -------------------- |
| `PropertyNamingPolicy`      | framework default (PascalCase in) / camelCase out per ASP.NET defaults) | services differ on the wire |
| `DefaultIgnoreCondition`    | framework default (emit null) | services differ on the wire |
| `allowIntegerValues: false` | `true` (framework default) | keep external callers posting ints working until they migrate |

Flipping `allowIntegerValues` to `false` is tracked as a follow-up. The
goal is that no external caller continues to bypass the string-enum
contract long-term.

## How to adopt in a new service

```csharp
using CloudHealthOffice.Infrastructure.Json;

builder.Services.AddControllers()
    .AddCloudHealthOfficeJsonOptions();
```

If you also need service-specific overrides, chain a second
`.AddJsonOptions(...)` call after — configuration actions compose, so the
shared converter remains registered.

```csharp
builder.Services.AddControllers()
    .AddCloudHealthOfficeJsonOptions()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    });
```

## Services intentionally NOT migrated

Three services deliberately skip `AddCloudHealthOfficeJsonOptions()`. Each
has an in-line comment in its `Program.cs` explaining why, so the next
person scaffolding doesn't "fix" the unexplained absence.

| Service | Reason |
| ------- | ------ |
| `fhir-service` | FHIR R4 wire format requires numeric enum coding and uses custom `FhirInputFormatter` / `FhirOutputFormatter`. Applying `JsonStringEnumConverter` would break FHIR conformance. |
| `CHO.TerminologyService` | FHIR ConceptMap/$translate service; its payloads follow FHIR conventions (camelCase + `WhenWritingNull`, numeric enum coding) rather than the platform default. |
| `CloudHealthOffice.PricingApi` | Already publishes camelCase properties AND camelCase-cased enum names (e.g. `"medicareFeeSchedule"`). The shared helper registers an unnamed converter that would emit PascalCase (`"MedicareFeeSchedule"`), breaking existing consumers. Harmonizing casing is tracked as a follow-up. |

## Testing

Every touched service has a `SharedJsonOptionsSmokeTests` class that
asserts `JsonStringEnumConverter` is present in the MVC JSON options.
Each test introspects `IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>`
from the service's DI container and verifies the converter is registered.

## Follow-ups

- Flip `allowIntegerValues` to `false` once no external caller is still
  POSTing numeric enum values.
- Harmonize CamelCase enum naming so `PricingApi` can adopt the shared
  helper.
- Consider extending the helper once naming policy is unified repo-wide.
