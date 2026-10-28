# Quickstarts

| Guide | What it covers |
|---|---|
| [Local claims adjudication](local-claims-quickstart.md) | Docker Compose stack, a local token, a benefit plan, a claim through adjudication, a payment run and the 835 ERA |
| [Quick Start on cloudhealthoffice.com](https://cloudhealthoffice.com/docs/quickstart) | The full platform on local Kubernetes, the Million Claim Challenge workload, and CRD discovery |

Local API calls need a development bearer token; see
[`scripts/dev-token.sh`](../../scripts/dev-token.sh). The token is accepted only
by services running with `ASPNETCORE_ENVIRONMENT=Development`.

## CMS-0057-F APIs

There is no separate CMS-0057-F quickstart. To see the four APIs behave:

- Run the executable acceptance scenarios against synthetic data:
  `dotnet test tests/Cms0057Acceptance.Tests/Cms0057Acceptance.Tests.csproj`.
  A green run shows the scenarios behave as specified. It is not a CMS
  certification (CMS does not certify payer APIs), a HIPAA attestation or a
  production-readiness review.
- Read the OpenAPI specifications in [`api/openapi`](../openapi), or browse
  them in the [API explorer](https://cloudhealthoffice.com/api-explorer).
- Check current status and limits in the
  [capability ledger](../../docs/CAPABILITY_RECONCILIATION.md).
- Sandboxes with synthetic data are provisioned on request:
  [request a sandbox](https://cloudhealthoffice.com/contact?interest=sandbox-access).
