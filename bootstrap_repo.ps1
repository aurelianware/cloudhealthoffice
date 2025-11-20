Param(
    [Parameter(Mandatory=$true)][string]$RepoName,
    [Parameter(Mandatory=$true)][string]$SourceDir,
    [ValidateSet('private','public')][string]$Visibility = 'private'
)

$ErrorActionPreference = 'Stop'

function Require-Cmd($name) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
        throw "Required command '$name' is not installed or not on PATH."
    }
}

Require-Cmd git
Require-Cmd gh

# Validate source files
function Require-File($path) {
    if (-not (Test-Path $path -PathType Leaf)) {
        throw "Missing required file: $path"
    }
}

$infra = Join-Path $SourceDir 'infra\main.bicep'
$ingest = Join-Path $SourceDir 'logicapps\workflows\ingest275\workflow.json'
$rfai   = Join-Path $SourceDir 'logicapps\workflows\rfai277\workflow.json'

Require-File $infra
Require-File $ingest
Require-File $rfai

# Create GitHub repo
Write-Host "Creating GitHub repo '$RepoName' ($Visibility)..." -ForegroundColor Cyan
$null = gh repo create $RepoName --$Visibility --confirm

# Working directory for new repo
$workDir = Join-Path (Get-Location) "${RepoName}_repo"
if (Test-Path $workDir) {
    throw "Directory $workDir already exists. Remove it or choose another repo name."
}
New-Item -ItemType Directory -Path $workDir | Out-Null

# Init local repo
Set-Location $workDir
git init | Out-Null

# Scaffold structure
New-Item -ItemType Directory -Path "infra" -Force | Out-Null
New-Item -ItemType Directory -Path "logicapps\workflows\ingest275" -Force | Out-Null
New-Item -ItemType Directory -Path "logicapps\workflows\rfai277" -Force | Out-Null
New-Item -ItemType Directory -Path ".github\workflows" -Force | Out-Null

# Copy files
Copy-Item $infra "infra\main.bicep" -Force
Copy-Item $ingest "logicapps\workflows\ingest275\workflow.json" -Force
Copy-Item $rfai   "logicapps\workflows\rfai277\workflow.json" -Force

# Add workflow YAML (from source if present, or create default)
$srcWorkflow = Join-Path $SourceDir ".github\workflows\deploy_logicapps_workflows_matrix_with_lint.yml"
$destWorkflow = ".github\workflows\deploy_logicapps_workflows_matrix_with_lint.yml"

if (Test-Path $srcWorkflow -PathType Leaf) {
    Copy-Item $srcWorkflow $destWorkflow -Force
} else {
@"
name: Deploy HIPAA Attachments (Bicep + Logic Apps)

on:
  workflow_dispatch:
    inputs:
      subscriptionId:
        description: 'Azure Subscription ID'
        required: true
      resourceGroup:
        description: 'Resource Group name (will be created if missing)'
        required: true
        default: 'payer-attachments-rg'
      location:
        description: 'Azure region'
        required: true
        default: 'eastus'
      baseName:
        description: 'Base name prefix for resources'
        required: true
        default: 'payer-attachments'
      logicAppName:
        description: 'Logic App Standard app name (optional; defaults to <baseName>-la)'
        required: false
        default: ''
      bicepPath:
        description: 'Path to Bicep file'
        required: true
        default: 'infra/main.bicep'
      workflowsPath:
        description: 'Path to Logic App workflows folder (must contain subfolders per workflow with workflow.json inside)'
        required: true
        default: 'logicapps/workflows'

jobs:
  deploy:
    runs-on: ubuntu-latest
    permissions:
      id-token: write
      contents: read

    steps:
      - name: Checkout
        uses: actions/checkout@v4

      - name: Azure Login (OIDC)
        uses: azure/login@v2
        with:
          client-id: \${{ secrets.AZURE_CLIENT_ID }}
          tenant-id: \${{ secrets.AZURE_TENANT_ID }}
          subscription-id: \${{ github.event.inputs.subscriptionId }}

      - name: Ensure Resource Group
        uses: azure/cli@v2
        with:
          inlineScript: |
            az group create -n "\${{ github.event.inputs.resourceGroup }}" -l "\${{ github.event.inputs.location }}"

      - name: Deploy Bicep (infra)
        uses: azure/arm-deploy@v2
        with:
          scope: resourcegroup
          resourceGroupName: \${{ github.event.inputs.resourceGroup }}
          template: \${{ github.event.inputs.bicepPath }}
          parameters: baseName=\${{ github.event.inputs.baseName }}
          deploymentName: payer-attachments-infra

      - name: Resolve Logic App name
        id: resolvela
        uses: azure/cli@v2
        with:
          inlineScript: |
            LA="\${{ github.event.inputs.logicAppName }}"
            if [ -z "$LA" ]; then
              LA="\${{ github.event.inputs.baseName }}-la"
            fi
            echo "name=$LA" >> $GITHUB_OUTPUT

      - name: Zip Logic App workflows
        run: |
          set -euo pipefail
          cd "\${{ github.event.inputs.workflowsPath }}"
          zip -r ../../workflows.zip .
          cd -
          ls -l workflows.zip || true

      - name: Deploy workflows (ZIP deploy to Logic App Standard)
        uses: azure/cli@v2
        with:
          inlineScript: |
            APP="\${{ steps.resolvela.outputs.name }}"
            RG="\${{ github.event.inputs.resourceGroup }}"
            ZIP_PATH="workflows.zip"
            if [ ! -f "$ZIP_PATH" ]; then
              echo "::error::ZIP $ZIP_PATH not found. Ensure previous step created it."; exit 1
            fi
            az webapp deploy \
              --resource-group "$RG" \
              --name "$APP" \
              --src-path "$ZIP_PATH" \
              --type zip

      - name: Restart Logic App to pick up new workflows (optional)
        uses: azure/cli@v2
        with:
          inlineScript: |
            az webapp restart -g "\${{ github.event.inputs.resourceGroup }}" -n "\${{ steps.resolvela.outputs.name }}"
"@ | Set-Content -Path $destWorkflow -Encoding UTF8
}

# README
@"
# HIPAAd Attachments – Logic Apps & Infra

This repo contains:
- infra/main.bicep — Storage, Service Bus topics, App Insights, Logic App Standard plan/app (starter)
- logicapps/workflows/ingest275/workflow.json — 275 Ingestion workflow (SFTP → Blob → X12 decode → SB → QNXT)
- logicapps/workflows/rfai277/workflow.json — 277 RFAI outbound workflow (SB → X12 encode → SFTP)
- .github/workflows/deploy_logicapps_workflows.yml — GitHub Actions (Bicep + workflows ZIP deploy)

## Deploy
Run the **Deploy HIPAA Attachments (Bicep + Logic Apps)** workflow from the Actions tab (workflow_dispatch).
"@ | Set-Content -Path "README.md" -Encoding UTF8

# Git push
git add .
git commit -m "Initial commit – infra + Logic App workflows + deploy workflow" | Out-Null
$login = gh api user --jq .login
git branch -M main
git remote add origin "https://github.com/$login/$RepoName.git"
git push -u origin main

Write-Host "✅ Repo bootstrapped: https://github.com/$login/$RepoName" -ForegroundColor Green
