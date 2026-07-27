## Episode metadata
- Series: Scaling a Healthcare Claims Platform in Azure
- Episode title: Part One — The Service Principal That Was Never There
- Source article: article.txt
- Published article: /insights/azure-scaling/part-1-the-service-principal-that-was-never-there
- Status: published

## Summary

Spinoff of the Million Claim Challenge / local-Kubernetes field notes, covering the first day of
bringing this platform up on a real Azure subscription (AKS, ACR, Key Vault, Storage, Cosmos DB,
Kafka via Strimzi) after the prior Azure environment was rebuilt from scratch.

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

Result: ~27 services running on real AKS by end of day. First genuine "deployed to Azure," not
just "written to be deployable to Azure."

## Planned series arc

- Part One: Azure — standing the environment back up, the identity bug, first real deployment.
- Part Two: Azure — closing the vCPU ceiling, full fleet confirmed running end to end.
- Future parts: same exercise on GCP, AWS.
