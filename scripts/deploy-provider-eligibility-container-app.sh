#!/usr/bin/env bash
# Deploys the provider eligibility API into the Container Apps environment
# shared with CloudDentalOffice. See src/services/provider-eligibility-api/README.md.
#
# Before the first deploy, store the Stedi key in Key Vault yourself (it is
# never generated or printed by this script):
#   az keyvault secret set --vault-name cho-kv \
#     --name provider-eligibility-stedi-api-key --value "<stedi key>"
#
# Required: CHO_PROVIDER_ELIGIBILITY_TENANT_IDS, comma-separated CDO tenant ids.
# Optional: CHO_PROVIDER_ELIGIBILITY_STEDI_ENV=test|production (default test).
set -euo pipefail

RESOURCE_GROUP="${CHO_PROVIDER_ELIGIBILITY_RESOURCE_GROUP:-cho}"
ACR_NAME="${CHO_PROVIDER_ELIGIBILITY_ACR_NAME:-clouhealthoffice}"
KEY_VAULT_NAME="${CHO_PROVIDER_ELIGIBILITY_KEY_VAULT_NAME:-cho-kv}"
TEMPLATE_FILE="${CHO_PROVIDER_ELIGIBILITY_TEMPLATE_FILE:-infrastructure/azure/provider-eligibility-container-app.bicep}"
IMAGE_TAG="${CHO_PROVIDER_ELIGIBILITY_IMAGE_TAG:-sha-$(git rev-parse HEAD)}"
STEDI_ENVIRONMENT="${CHO_PROVIDER_ELIGIBILITY_STEDI_ENV:-test}"
TENANT_IDS="${CHO_PROVIDER_ELIGIBILITY_TENANT_IDS:-}"
REPOSITORY="cloudhealthoffice-provider-eligibility-api"

if [[ "${1:-}" != "preview" && "${1:-}" != "deploy" ]]; then
  echo "Usage: $0 preview|deploy" >&2
  exit 2
fi

if [[ "$STEDI_ENVIRONMENT" != "test" && "$STEDI_ENVIRONMENT" != "production" ]]; then
  echo "CHO_PROVIDER_ELIGIBILITY_STEDI_ENV must be test or production." >&2
  exit 2
fi

tenant_json="["
IFS=',' read -r -a tenants <<<"$TENANT_IDS"
for tenant in "${tenants[@]}"; do
  tenant="${tenant// /}"
  [[ -z "$tenant" ]] && continue
  if [[ ! "$tenant" =~ ^[A-Za-z0-9._-]+$ ]]; then
    echo "Invalid tenant id in CHO_PROVIDER_ELIGIBILITY_TENANT_IDS." >&2
    exit 2
  fi
  [[ "$tenant_json" != "[" ]] && tenant_json+=","
  tenant_json+="\"$tenant\""
done
tenant_json+="]"
if [[ "$tenant_json" == "[]" ]]; then
  echo "Set CHO_PROVIDER_ELIGIBILITY_TENANT_IDS to the CloudDentalOffice tenant id(s)." >&2
  exit 2
fi

mode="$1"
image="${ACR_NAME}.azurecr.io/${REPOSITORY}:${IMAGE_TAG}"

if [[ "$mode" == "preview" ]]; then
  az deployment group what-if \
    --resource-group "$RESOURCE_GROUP" \
    --name cho-provider-eligibility-preview \
    --template-file "$TEMPLATE_FILE" \
    --parameters \
      providerEligibilityImage="${ACR_NAME}.azurecr.io/${REPOSITORY}:latest" \
      stediEnvironment="$STEDI_ENVIRONMENT" \
      stediApiKey=preview-not-deployed \
      cdoClientApiKey=preview-not-deployed \
      cdoTenantIds="$tenant_json" \
    --result-format ResourceIdOnly
  exit 0
fi

if ! stedi_api_key=$(az keyvault secret show --vault-name "$KEY_VAULT_NAME" \
    --name provider-eligibility-stedi-api-key --query value -o tsv 2>/dev/null) || [[ -z "$stedi_api_key" ]]; then
  echo "Key Vault secret provider-eligibility-stedi-api-key is missing; set it first (see header)." >&2
  exit 1
fi

echo "Building and pushing provider-eligibility-api ${IMAGE_TAG}..."
az acr build \
  --registry "$ACR_NAME" \
  --file src/services/provider-eligibility-api/Dockerfile \
  --build-arg "REGISTRY=${ACR_NAME}.azurecr.io" \
  --image "${REPOSITORY}:${IMAGE_TAG}" \
  .

cdo_client_api_key=$(az keyvault secret show --vault-name "$KEY_VAULT_NAME" \
  --name provider-eligibility-cdo-api-key --query value -o tsv 2>/dev/null || true)
if [[ -z "$cdo_client_api_key" ]]; then
  cdo_client_api_key=$(openssl rand -base64 48 | tr -d '\n')
  az keyvault secret set \
    --vault-name "$KEY_VAULT_NAME" \
    --name provider-eligibility-cdo-api-key \
    --value "$cdo_client_api_key" \
    --output none
fi

umask 077
parameters_file="$(mktemp /tmp/cho-provider-eligibility-parameters.XXXXXX.json)"
trap 'rm -f "$parameters_file"' EXIT

cat >"$parameters_file" <<EOF
{
  "providerEligibilityImage": { "value": "$image" },
  "stediEnvironment": { "value": "$STEDI_ENVIRONMENT" },
  "stediApiKey": { "value": "$stedi_api_key" },
  "cdoClientApiKey": { "value": "$cdo_client_api_key" },
  "cdoTenantIds": { "value": $tenant_json }
}
EOF

echo "Deploying provider-eligibility (Stedi ${STEDI_ENVIRONMENT} mode)..."
az deployment group create \
  --resource-group "$RESOURCE_GROUP" \
  --name "cho-provider-eligibility-$(date -u +%Y%m%d%H%M%S)" \
  --template-file "$TEMPLATE_FILE" \
  --parameters "@$parameters_file" \
  --query properties.outputs \
  --output json

unset stedi_api_key cdo_client_api_key
echo "CloudDentalOffice reads the client credential from Key Vault secret provider-eligibility-cdo-api-key."
