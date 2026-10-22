# care-management-service

Care management: what happens to a member between services.

Utilization management asks whether a service should be covered.
Care management asks what happens to the member between services — who is
following up, against what plan, and whether anything is improving. The two
meet at the edges (a discharge recorded by concurrent review, or a denial,
can refer a case) but a case outlives any one authorization.

## The record

A **case** is one member, one programme, one care manager, for as long as the
member is enrolled. It carries:

- **Assessments** — the answers to a named instrument, with the template id
  and version. The instrument itself is not stored: several are licensed, and
  a score only means something alongside the version that produced it.
- **A care plan** — problems, goals, interventions and barriers, kept as
  separate lists joined by id. An intervention commonly serves several goals
  and a goal commonly spans several problems; nesting them would force a
  choice the clinical reality does not have.
- **Activities** — due-dated tasks, pushed into `work-management-service`
  rather than given a second queue.
- **A care team** — including people outside the plan, such as a family
  caregiver.

## Two rules worth knowing before changing anything

**Closed and Declined are terminal.** A member who comes back gets a new
case. The dates and the closure outcome are the record of one episode, and
reopening would overwrite them.

**Care management is voluntary.** A case cannot be made active, and a care
plan cannot be activated, until the member's consent is recorded with a date.
A plan the member never agreed to is not a care plan.

## FHIR

`GET /fhir/CarePlan?patient=`, `/fhir/Goal?patient=`, `/fhir/CareTeam?patient=`
and the by-id reads project the case, read-only and on demand. Nothing is
stored twice, so a projection cannot drift from the case it describes.
care-management-service owns the record and so projects its own resources, as
authorization-service does for prior-authorization `ExplanationOfBenefit`;
fhir-service proxies rather than duplicating the mapping.

`CareGoalStatus` is spelled to match the FHIR goal-achievement codes, so the
projection has no mapping to invent.

## The work queue is not load-bearing

Activities are recorded on the case whether or not `work-management-service`
accepts them; the gateway answers null rather than throwing. The care record
is the system of record and does not depend on a queue being up. Without
`Services:WorkManagementServiceUrl` configured, activities simply appear in
nobody's queue.

## Configuration

| Key | Meaning |
|---|---|
| `MongoDb:ConnectionString` | Required. The host refuses to start without it. |
| `MongoDb:CareCasesCollection` | Defaults to `CareCases`. |
| `Services:WorkManagementServiceUrl` | Optional; unset disables queueing. |

Permissions: `care:read` and `care:write`. The tenant and the acting care
manager come from the validated CHO token, never from a request body.
