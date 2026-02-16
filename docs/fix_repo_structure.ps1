# fix_repo_structure.ps1
Param(
  [string]$RepoRoot = "."
)

$ErrorActionPreference = "Stop"
Push-Location $RepoRoot

Write-Host "==> Normalizing repo structure..." -ForegroundColor Cyan

# 1) Ensure .github/workflows exists
$targetWorkflows = ".github/workflows"
if (-not (Test-Path $targetWorkflows -PathType Container)) {
  New-Item -ItemType Directory -Force -Path $targetWorkflows | Out-Null
  Write-Host "Created $targetWorkflows"
}

# Find any stray '.github*' dirs that contain workflow files and move them in
Get-ChildItem -Directory -Recurse -Force | Where-Object {
  $_.FullName -match "\.github" -and $_.Name -match "workflows"
} | ForEach-Object {
  $src = $_.FullName
  if ($src -ne (Resolve-Path $targetWorkflows).Path) {
    Write-Host "Found stray workflows folder: $src"
    Get-ChildItem $src -Filter "deploy_logicapps*.yml" -File -ErrorAction SilentlyContinue |
      ForEach-Object {
        Copy-Item $_.FullName $targetWorkflows -Force
        Write-Host "  Moved $($_.Name) -> $targetWorkflows"
      }
  }
}

# 2) Ensure Logic Apps folders
$ingestDir = "logicapps/workflows/ingest275"
$rfaiDir   = "logicapps/workflows/rfai277"
New-Item -ItemType Directory -Force -Path $ingestDir | Out-Null
New-Item -ItemType Directory -Force -Path $rfaiDir   | Out-Null

# 3) Move ingest275 workflow.json if it exists in legacy spot
$legacyIngest = "logicapp_275_ingestion_template/workflow.json"
$destIngest   = Join-Path $ingestDir "workflow.json"
if (Test-Path $legacyIngest -PathType Leaf) {
  if (-not (Test-Path $destIngest)) {
    Move-Item $legacyIngest $destIngest -Force
    Write-Host "Moved $legacyIngest -> $destIngest"
  } else {
    Write-Host "Ingest already present at $destIngest (leaving $legacyIngest in place)"
  }
}

# 4) If my package was extracted, copy its workflows into the right spots (only if missing)
$pkgDir   = "hipaa_attachments_logicapps_package"
$pkgIngest = Join-Path $pkgDir "workflow.ingest275.json"
$pkgRfai   = Join-Path $pkgDir "workflow.rfai277.json"

# -- FIXED: wrap Test-Path calls in parentheses
if ((Test-Path $pkgIngest -PathType Leaf) -and -not (Test-Path $destIngest)) {
  Copy-Item $pkgIngest $destIngest -Force
  Write-Host "Copied $pkgIngest -> $destIngest"
}

$destRfai = Join-Path $rfaiDir "workflow.json"
if ((Test-Path $pkgRfai -PathType Leaf) -and -not (Test-Path $destRfai)) {
  Copy-Item $pkgRfai $destRfai -Force
  Write-Host "Copied $pkgRfai -> $destRfai"
}



# 5) Move any workflow ymls sitting in repo into .github/workflows
Get-ChildItem -Recurse -Include '*.yaml','*.yml' -File | ForEach-Object {
  $src = $_.FullName
  if ($src -notlike "*\.github\workflows\*") {
    Copy-Item $src $targetWorkflows -Force
    Write-Host "Moved $($_.Name) -> $targetWorkflows"
  }
}


Write-Host "`n==> Final structure (top level):" -ForegroundColor Cyan
(Get-ChildItem -Directory).Name | ForEach-Object { Write-Host " - $_" }

Write-Host "`nExpect to see:" -ForegroundColor Yellow
Write-Host "  .github/workflows/deploy_logicapps_workflows_matrix_with_lint.yml"
Write-Host "  infra/main.bicep"
Write-Host "  logicapps/workflows/ingest275/workflow.json"
Write-Host "  logicapps/workflows/rfai277/workflow.json"

Pop-Location

Write-Host "`nDone. Stage & push changes when ready:" -ForegroundColor Cyan
Write-Host "  git add ."
Write-Host "  git commit -m `"Normalize repo structure for Logic Apps deploy`""
Write-Host "  git push origin main"
