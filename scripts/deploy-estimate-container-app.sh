#!/usr/bin/env bash
set -euo pipefail

RESOURCE_GROUP="${CHO_ESTIMATE_RESOURCE_GROUP:-cho}"
ACR_NAME="${CHO_ESTIMATE_ACR_NAME:-clouhealthoffice}"
KEY_VAULT_NAME="${CHO_ESTIMATE_KEY_VAULT_NAME:-cho-kv}"
TEMPLATE_FILE="${CHO_ESTIMATE_TEMPLATE_FILE:-infrastructure/azure/estimate-container-app.bicep}"
IMAGE_TAG="${CHO_ESTIMATE_IMAGE_TAG:-sha-$(git rev-parse HEAD)}"
BENEFIT_REPOSITORY="cloudhealthoffice-benefit-plan-service"
REDIS_REPOSITORY="third-party/redis"

if [[ "${1:-}" != "preview" && "${1:-}" != "deploy" ]]; then
  echo "Usage: $0 preview|deploy" >&2
  exit 2
fi

mode="$1"
benefit_image="${ACR_NAME}.azurecr.io/${BENEFIT_REPOSITORY}:${IMAGE_TAG}"
redis_image="${ACR_NAME}.azurecr.io/${REDIS_REPOSITORY}:7.4-alpine"

if [[ "$mode" == "preview" ]]; then
  az deployment group what-if \
    --resource-group "$RESOURCE_GROUP" \
    --name cho-estimate-preview \
    --template-file "$TEMPLATE_FILE" \
    --parameters \
      benefitPlanImage="${ACR_NAME}.azurecr.io/${BENEFIT_REPOSITORY}:latest" \
      redisImage="$redis_image" \
      estimateApiKey=preview-not-deployed \
      redisPassword=preview-not-deployed \
    --result-format ResourceIdOnly
  exit 0
fi

echo "Mirroring Redis into the private registry..."
az acr import \
  --name "$ACR_NAME" \
  --source docker.io/library/redis:7.4-alpine \
  --image "${REDIS_REPOSITORY}:7.4-alpine" \
  --force \
  --only-show-errors

echo "Building and pushing benefit-plan-service ${IMAGE_TAG}..."
az acr build \
  --registry "$ACR_NAME" \
  --file src/services/benefit-plan-service/Dockerfile \
  --build-arg "REGISTRY=${ACR_NAME}.azurecr.io" \
  --image "${BENEFIT_REPOSITORY}:${IMAGE_TAG}" \
  .

get_or_create_secret() {
  local secret_name="$1"
  local byte_count="$2"
  local secret_value
  if secret_value=$(az keyvault secret show --vault-name "$KEY_VAULT_NAME" --name "$secret_name" --query value -o tsv 2>/dev/null); then
    printf '%s' "$secret_value"
    return
  fi
  secret_value=$(openssl rand -base64 "$byte_count" | tr -d '\n')
  az keyvault secret set \
    --vault-name "$KEY_VAULT_NAME" \
    --name "$secret_name" \
    --value "$secret_value" \
    --output none
  printf '%s' "$secret_value"
}

estimate_api_key=$(get_or_create_secret estimate-api-key 48)
redis_password=$(get_or_create_secret estimate-redis-password 36)

echo "Deploying the estimate-only Container Apps slice..."
az deployment group create \
  --resource-group "$RESOURCE_GROUP" \
  --name "cho-estimate-$(date -u +%Y%m%d%H%M%S)" \
  --template-file "$TEMPLATE_FILE" \
  --parameters \
    benefitPlanImage="$benefit_image" \
    redisImage="$redis_image" \
    estimateApiKey="$estimate_api_key" \
    redisPassword="$redis_password" \
  --query properties.outputs \
  --output json

unset estimate_api_key redis_password
