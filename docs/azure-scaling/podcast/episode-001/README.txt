## Episode metadata
- Series: Scaling a Healthcare Claims Platform in Azure
- Episode title: Part One — The Service Principal That Was Never There
- Source article: article.txt
- Published article: /insights/azure-scaling/part-1-the-service-principal-that-was-never-there
- Status: published

## Summary

Spinoff of the Million Claim Challenge / local-Kubernetes field notes. The platform previously ran
in Azure; the roadmap deliberately pulled back to local-only Kubernetes so evaluators could run it
without needing a cloud subscription first, and the Million Claim Challenge + 834/837 on-ramp work
proved it there. This episode covers the first day of bringing it back to a real Azure subscription
(AKS, ACR, Key Vault, Storage, Cosmos DB, Kafka via Strimzi) after the prior Azure environment was
rebuilt from scratch.

Central story: an hours-long chase where `az role assignment create --assignee-object-id` kept
succeeding without validating the target identity existed, so every RBAC grant that morning
landed on a service principal the CI pipeline was never actually authenticating as. Confirmed via
a sha256 hash comparison of the subscription id (never printing the raw secret) that the CI was
authenticating into an entirely different, older Azure tenant. Fixed by creating the correct app
registration + OIDC federated credential in the right tenant.

Four smaller, sequential bugs followed once identity was fixed: a missing `--resource-group` flag
on `az acr import`, a comments-only alias Kubernetes manifest that broke the deploy loop, an
exhausted namespace ResourceQuota sized for a much smaller fleet, and a disclosed-not-fixed
Azure free-trial regional vCPU cap blocking further node autoscaling.

Result: ~27 services running on real AKS by end of day -- the first deployment to the rebuilt
environment, and the first time in a while this platform has run anywhere but a laptop.

The point of the environment isn't the environment: close to thirty services are running, including
enrollment-import-service (the 834 on-ramp) and claims-service/eligibility-service (837 processing
and the Million Claim Challenge's scale story). Once the fleet is fully up, the plan is to run the
same evaluator on-ramp and the same claims-scale methodology already proven locally against this
real Azure infrastructure instead.

## Planned series arc

- Part One: Azure — standing the environment back up, the identity bug, first real deployment.
- Part Two: Azure — closing the vCPU ceiling, full fleet confirmed running end to end, then the
  834/837 on-ramp and a claims-volume run against the real environment.
- Future parts: same exercise on GCP, AWS.
