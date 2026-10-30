# Public Provider Directory (Da Vinci PDex Plan-Net 1.2.0)

Gap-matrix G-36, D-29, owner decision Q-19.

CMS-9115-F requires each plan's Provider Directory API to be public: no
authentication, no consumer-identifying information. CHO publishes one per
plan, in the shape of the [Da Vinci PDex Plan-Net 1.2.0](https://hl7.org/fhir/us/davinci-pdex-plan-net/)
implementation guide, at:

```
GET /fhir/r4/directory/{directory}/metadata
GET /fhir/r4/directory/{directory}/{type}?{search}
GET /fhir/r4/directory/{directory}/{type}/{id}
GET /fhir/r4/directory/{directory}/{type}/{id}/_history/{versionId}
```

`{type}` is any of Organization, Practitioner, PractitionerRole, Location,
HealthcareService, OrganizationAffiliation, InsurancePlan and Endpoint. Any
other type is 404. Any credential on the request is ignored.

## Configuration (fhir-service)

```
PublicProviderDirectory:Directories:<directory id>            tenant id (nothing is published by default)
PublicProviderDirectory:Payers:<directory id>:Name            the plan's own Organization
PublicProviderDirectory:Payers:<directory id>:Phone
PublicProviderDirectory:Payers:<directory id>:Website
PublicProviderDirectory:Payers:<directory id>:AddressLine
PublicProviderDirectory:Payers:<directory id>:City
PublicProviderDirectory:Payers:<directory id>:State
PublicProviderDirectory:Payers:<directory id>:PostalCode
PublicProviderDirectory:Payers:<directory id>:IdentifierSystem  optional, with IdentifierValue; without both,
PublicProviderDirectory:Payers:<directory id>:IdentifierValue   the directory id under a CHO public-directory system
PublicProviderDirectory:PermitPerMinute                       per client address, default 300
PublicProviderDirectory:RefreshMinutes                        snapshot age before a rebuild, default 60
PublicProviderDirectory:MaxResources                          snapshot size guard, default 500000
```

The payer entry matters for conformance. Plan-Net requires every network to
name the organization that manages it (`partOf`), every InsurancePlan to name
its owner and administrator, and every Organization to carry an address. CHO
keeps no record of the plan itself, so the directory publishes it from this
configuration as `Organization/payer` (type `payer`). A directory with no payer
entry still serves, under a placeholder named for the directory id, but its
payer Organization fails Plan-Net validation for want of an address.

fhir-service also needs `Services:ProviderServiceUrl` and
`Services:BenefitPlanServiceUrl`.

### On the GKE demo

`scripts/deploy-gke.sh` (and the GKE deploy workflow, from the repository
variable of the same name) takes the directories as one JSON value,
`PUBLIC_PROVIDER_DIRECTORY`, keyed by directory id:

```json
{"demo": {"Tenant": "demo", "Name": "Demo Health Plan (synthetic)", "Phone": "555-0100",
          "AddressLine": "1 Synthetic Way", "City": "Springfield", "State": "FL", "PostalCode": "32401"}}
```

`deploy/gcp/render_manifests.py --public-directory` turns each entry into the
settings above on fhir-service. It refuses a directory id that is not a URL
segment, an unknown setting, a missing `Tenant`, `Name` or address field
(Plan-Net fails without the address), and `IdentifierSystem` without
`IdentifierValue`. Unset, nothing is published.

## How it is built

```
provider-service                                fhir-service
  GET /fhir/plannet-export/networks?payer=payer  ─┐
  GET /fhir/plannet-export/providers?payer=…    ─┤
benefit-plan-service                             ├─> snapshot per directory ─> read, search, metadata
  GET /fhir/InsurancePlan, GET /fhir/Endpoint   ─┘    (in memory)
```

* **provider-service projects** (`PlanNetDirectoryProjector`). Active networks
  become `plannet-Network`. An individual provider becomes a Practitioner, a
  Location (`{npi}-loc`, from the provider's address) and one PractitionerRole
  per active network participation, carrying the network-reference and
  newpatients extensions. A practitioner with a hospital affiliation in force
  practises at that hospital: the role's `organization`, `healthcareService`
  and `location` name the hospital's Organization, `{npi}-svc` and `{npi}-loc`
  (the earliest-effective affiliation when there are several). The
  practitioner's own Location stays published, and is the role's location only
  when the hospital is not in the directory.
  An organization provider becomes an Organization, a
  Location, a HealthcareService (`{npi}-svc`) and one OrganizationAffiliation per
  network. Codes come only from Plan-Net's required value sets: the NUCC
  taxonomy is mapped to ProviderRoleCS, OrgTypeCS, HealthcareServiceCategoryCS
  and OrganizationAffiliationRoleCS, and a specialty is published only when the
  taxonomy code is in the specialty value set. A provider whose taxonomy is
  outside them is published without that code rather than with a wrong one.
* **fhir-service shapes and serves.** It adds the payer Organization, gives each
  InsurancePlan its owner, administrator and a Plan-Net product type, and keeps
  only active Endpoints, typed as `rest-non-fhir`. Then it makes the links only
  the whole directory can (`PlanNetLinking`): each OrganizationAffiliation's
  `organization` is the organization that manages its network (the network's
  `partOf`), the payer lists the Endpoints it manages, and a PractitionerRole
  link to a hospital the directory does not publish is dropped rather than left
  dangling. Everything passes through the
  same response filter as before, which strips CHO-internal extensions and tax
  ids.
* **The snapshot.** The first request for a directory builds it and waits.
  After `RefreshMinutes`, the next request starts a rebuild in the background
  and is answered from the old snapshot. A failed rebuild keeps the old snapshot
  and is not retried for 30 seconds; a failed first build answers 502 without
  any upstream detail. The build runs as fhir-service's own service identity for
  the directory's tenant, never as the caller. Each resource carries
  `meta.versionId`, a hash of its content, which is also the ETag.
* **Search.** Every SHALL search parameter of the Plan-Net CapabilityStatement,
  plus `_id`, `_lastUpdated` and `identifier`; string, token, reference and date
  matching with their FHIR modifiers; chains to depth 4; `_has` reverse chains;
  `_include`, `_revinclude` and `*`; `_count` (default 50, at most 200) and
  `_page`. An unknown parameter is ignored and reported in an OperationOutcome
  entry; a malformed one is 400.

## Limits

* `near` (geolocation) is not supported: CHO stores no coordinates.
* Data is as fresh as the snapshot, an hour by default. CMS asks for directory
  updates within 30 business days of a change.
* The whole directory is held in memory, per replica. `MaxResources` stops a
  build that would exceed it.
* The Inferno Plan-Net kit (`INFERNO-PLANNET-SERVER-001`, nightly) is generated
  from Plan-Net 1.1.0; see `docs/interop/davinci.md`.
