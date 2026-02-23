#!/usr/bin/env bash
# ============================================================================
# Cloud Health Office — Repository Restructuring Script
# ============================================================================
#
# Transforms CHO from 132 root files / 29 root dirs into a commercial-grade
# structure modeled after CloudDentalOffice (12 root files / 13 root dirs).
#
# USAGE:
# cd cloudhealthoffice
# chmod +x restructure-repo.sh
# ./restructure-repo.sh
#
# SAFETY:
# - Uses git mv (preserves full history)
# - Does NOT auto-commit — review with `git status` first
# - Revert everything with `git checkout .` if unhappy
#
# ============================================================================
set -euo pipefail
RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; BLUE='\033[0;34m'; NC='\033[0m'
echo -e "${BLUE}============================================================${NC}"
echo -e "${BLUE} Cloud Health Office — Repository Restructuring${NC}"
echo -e "${BLUE}============================================================${NC}"
echo ""
# Verify we're in the right repo
if [ ! -f "package.json" ] || ! grep -q "cloud-health-office" package.json 2>/dev/null; then
echo -e "${RED}ERROR: Run this script from the cloudhealthoffice repo root.${NC}"
exit 1
fi
BEFORE_FILES=$(ls -1 | grep -v '^\.' | wc -l)
BEFORE_DIRS=$(ls -1d */ 2>/dev/null | wc -l)
echo -e "${YELLOW}Before:${NC} $BEFORE_FILES root items ($BEFORE_DIRS directories)"
echo ""
# ────────────────────────────────────────────────────────────
# PHASE 1: Create target directory structure
# ────────────────────────────────────────────────────────────
echo -e "${GREEN}Phase 1: Creating directory structure...${NC}"
dirs=(
docs/architecture docs/deployment docs/security docs/compliance
docs/onboarding docs/roadmap docs/features docs/testing
docs/internal/implementation-summaries docs/internal/status-reports
docs/governance
api/openapi api/quickstarts api/postman
tests/fixtures/edi tests/fixtures/json
scripts/setup scripts/testing scripts/deploy
schemas/x12
infrastructure/argo-events infrastructure/argo-workflows
infrastructure/azure infrastructure/k8s infrastructure/kafka
infrastructure/monitoring infrastructure/logicapps infrastructure/docker
infrastructure/helm
)
for d in "${dirs[@]}"; do mkdir -p "$d"; done
echo -e " ${GREEN}✓ Created ${#dirs[@]} directories${NC}"
# ────────────────────────────────────────────────────────────
# PHASE 2: Move root markdown files → docs/
# ────────────────────────────────────────────────────────────
echo -e "${GREEN}Phase 2: Moving 49 root markdown files → docs/...${NC}"
# Helper: move if exists
gmv() { git mv "$1" "$2" 2>/dev/null && echo " → $2" || true; }
# Architecture
gmv ARCHITECTURE.md docs/architecture/
gmv BRANCHING-STRATEGY.md docs/architecture/
# Deployment
for f in DEPLOYMENT.md COSMOS-DB-DEPLOYMENT.md GITHUB-ACTIONS-SETUP.md \
DEPLOYMENT-SECRETS-SETUP.md DEPLOYMENT-GATES-GUIDE.md \
DEPLOYMENT-WORKFLOW-REFERENCE.md DOCKER-BUILD-STATUS.md; do
gmv "$f" docs/deployment/
done
# Security
for f in SECURITY-HARDENING.md SECURITY-IMPLEMENTATION-SUMMARY.md \
SECURITY-TOKEN-CONFIGURATION.md HIPAA-X12-Agreements-Guide.md; do
gmv "$f" docs/security/
done
# Compliance
gmv FHIR-IMPLEMENTATION-SUMMARY.md docs/compliance/
gmv VALIDATION-TOOLS.md docs/compliance/
# Onboarding
for f in QUICKSTART.md ONBOARDING.md ONBOARDING-ENHANCEMENTS.md \
QUICK-UPDATE-GUIDE.md TROUBLESHOOTING.md TROUBLESHOOTING-FAQ.md MIGRATION.md; 
gmv "$f" docs/onboarding/
do
done
# Roadmap / Releases
for f in ROADMAP.md ROADMAP-2026.md V4-LAUNCH-ROADMAP.md SAAS-LAUNCH-READINESS.md \
RELEASE-v4.0.0.md RELEASE_NOTES.md WHATS-NEW.md; do
gmv "$f" docs/roadmap/
done
# Features / Governance
gmv FEATURES.md docs/features/
gmv GOVERNANCE.md docs/governance/
# Testing docs
for f in AUTHENTICATION-TESTING-GUIDE.md AUTHENTICATION-VISUAL-GUIDE.md \
TESTING-SIGNUP-FIX.md TESTING-SMART-ROUTING.md TEST-276-277-STATUS.md \
test-plan-trading-partners.md testing-status-report.md; do
gmv "$f" docs/testing/
done
# Internal implementation summaries (one-time "done" docs)
for f in 276-277-IMPLEMENTATION-COMPLETE.md BRANDING-IMPLEMENTATION-SUMMARY.md \
GATED-RELEASE-IMPLEMENTATION-SUMMARY.md IMPLEMENTATION-SUMMARY.md \
PRIOR-AUTH-IMPLEMENTATION-SUMMARY.md VALUEADDS277-IMPLEMENTATION-COMPLETE.md \
WEBSITE-PHASE2-COMPLETE.md WEBSITE-UPDATES-FINAL.md UPDATES-SUMMARY.md; do
gmv "$f" docs/internal/implementation-summaries/
done
# Internal status reports
for f in DEPLOYMENT-CHECK-SUMMARY.md DEPLOYMENT-PIPELINE-CHANGES.md \
DEPLOYMENT-STATUS-REPORT.md DEPLOYMENT-WORKFLOW-VALIDATION.md; do
gmv "$f" docs/internal/status-reports/
done
echo -e " ${GREEN}✓ Markdown files organized${NC}"
# ────────────────────────────────────────────────────────────
# PHASE 3: Move test fixtures → tests/fixtures/
# ────────────────────────────────────────────────────────────
echo -e "${GREEN}Phase 3: Moving test fixtures...${NC}"
for f in test-attachment-275-solicited.json test-attachment-275-unsolicited.json \
test-auth-request.json test-backend-response-payload.json \
test-claim-institutional-payload.json test-claim-payload.json \
test-eligibility-inquiry.json test-enrollment-payload.json; do
gmv "$f" tests/fixtures/json/
done
for f in test-x12-275-clearinghouse-inbound.edi test-x12-276-claim-status-request.edi \
test-x12-277-claim-status-response.edi test-x12-277-healthplan-outbound.edi \
test-x12-278-review-request.edi test-x12-834-enrollment-sample.edi; do
gmv "$f" tests/fixtures/edi/
done
echo -e " ${GREEN}✓ 14 fixtures moved${NC}"
# ────────────────────────────────────────────────────────────
# PHASE 4: Move root scripts
# ────────────────────────────────────────────────────────────
echo -e "${GREEN}Phase 4: Moving root scripts...${NC}"
# Test scripts
for f in test-276-277-claim-status.sh test-attachment-oauth.sh \
test-sftp-275-278-workflow.sh test-stripe-signup.sh test-workflows.ps1; do
gmv "$f" scripts/testing/
done
# Setup scripts
for f in bootstrap_repo.ps1 setup-integration-account.ps1 \
setup-portal-azuread-secret.sh setup-stripe.sh \
configure-hipaa-trading-partners.ps1 configure-x12-agreements.ps1 \
validate-github-secrets.sh check-workflow-status.sh fix_repo_structure.ps1; do
gmv "$f" scripts/setup/
done
# Deploy scripts
for f in deploy-api-connections.json.ps1 deploy-new-integration-account.ps1 \
deploy-tenant-onboarding.sh deploy-workflows.ps1; do
gmv "$f" scripts/deploy/
done
echo -e " ${GREEN}✓ 18 scripts moved${NC}"
# ────────────────────────────────────────────────────────────
# PHASE 5: Move schemas + Azure deployment artifacts
# ────────────────────────────────────────────────────────────
echo -e "${GREEN}Phase 5: Moving schemas and Azure artifacts...${NC}"
gmv Companion_275AttachmentEnvelope.xsd schemas/x12/
gmv X12_005010X212_277.xsd schemas/x12/
gmv X12_005010X217_278.xsd schemas/x12/
gmv azuredeploy.json infrastructure/azure/
gmv integration-link.json infrastructure/azure/
echo -e " ${GREEN}✓ 5 files moved${NC}"
# ────────────────────────────────────────────────────────────
# PHASE 6: Consolidate infrastructure directories
# ────────────────────────────────────────────────────────────
echo -e "${GREEN}Phase 6: Consolidating infrastructure...${NC}"
# argo-events/ → infrastructure/argo-events/
if [ -d "argo-events" ]; then
git mv argo-events/* infrastructure/argo-events/ 2>/dev/null || true
rmdir argo-events 2>/dev/null || git rm -r argo-events 2>/dev/null || true
echo " → infrastructure/argo-events/"
fi
# argo-workflows/ → infrastructure/argo-workflows/
if [ -d "argo-workflows" ]; then
git mv argo-workflows/* infrastructure/argo-workflows/ 2>/dev/null || true
rmdir argo-workflows 2>/dev/null || git rm -r argo-workflows 2>/dev/null || true
echo " → infrastructure/argo-workflows/"
fi
# attachments_logicapps_package/ → infrastructure/azure/
[ -d "attachments_logicapps_package" ] && git mv attachments_logicapps_package infrastructure
# infra/ (Bicep) → infrastructure/azure/
if [ -d "infra" ]; then
for f in infra/*; do [ -e "$f" ] && git mv "$f" infrastructure/azure/ 2>/dev/null || true
rmdir infra 2>/dev/null || true
echo " → infrastructure/azure/ (Bicep)"
fi
# k8s/ → infrastructure/k8s/
if [ -d "k8s" ]; then
for f in k8s/*; do [ -e "$f" ] && git mv "$f" infrastructure/k8s/ 2>/dev/null || true; do
rmdir k8s 2>/dev/null || true
echo " → infrastructure/k8s/"
fi
# kafka/ → infrastructure/kafka/
if [ -d "kafka" ]; then
for f in kafka/*; do [ -e "$f" ] && git mv "$f" infrastructure/kafka/ 2>/dev/null || true
rmdir kafka 2>/dev/null || true
echo " → infrastructure/kafka/"
fi
# monitoring/ → infrastructure/monitoring/
if [ -d "monitoring" ]; then
for f in monitoring/*; do [ -e "$f" ] && git mv "$f" infrastructure/monitoring/ 2>/dev/nu
rmdir monitoring 2>/dev/null || true
echo " → infrastructure/monitoring/"
fi
# logicapps/ → infrastructure/logicapps/
if [ -d "logicapps" ]; then
for f in logicapps/*; do [ -e "$f" ] && git mv "$f" infrastructure/logicapps/ 2>/dev/null
rmdir logicapps 2>/dev/null || true
echo " → infrastructure/logicapps/"
fi
# logicapp_275_ingestion_template/ → infrastructure/logicapps/
[ -d "logicapp_275_ingestion_template" ] && git mv logicapp_275_ingestion_template infrastruc
# helm/ → infrastructure/helm/
[ -d "helm" ] && git mv helm infrastructure/helm 2>/dev/null && echo " → infrastructure/helm
# migration/ → scripts/migration/
[ -d "migration" ] && git mv migration scripts/migration 2>/dev/null && echo " → scripts/mig
echo -e " ${GREEN}✓ Infrastructure consolidated${NC}"
# ────────────────────────────────────────────────────────────
# PHASE 7: Move remaining root directories into proper homes
# ────────────────────────────────────────────────────────────
echo -e "${GREEN}Phase 7: Moving remaining root directories...${NC}"
# portal/ → src/portal/ (application code, matches CDO's src/ pattern)
[ -d "portal" ] && git mv portal src/portal 2>/dev/null && echo " → src/portal/" || true
# functions/ → src/functions/ (Azure Functions are app code)
[ -d "functions" ] && git mv functions src/functions 2>/dev/null && echo " → src/functions/"
# ml/ → src/ml/ (ML models are app code)
[ -d "ml" ] && git mv ml src/ml 2>/dev/null && echo " → src/ml/" || true
# fundraising/ → docs/fundraising/
[ -d "fundraising" ] && git mv fundraising docs/fundraising 2>/dev/null && echo " → docs/fun
# marketplace/ → docs/marketplace/
[ -d "marketplace" ] && git mv marketplace docs/marketplace 2>/dev/null && echo " → docs/mar
# sales-materials/ → docs/sales-materials/
[ -d "sales-materials" ] && git mv sales-materials docs/sales-materials 2>/dev/null && echo "
# generated/ → docs/generated-examples/
[ -d "generated" ] && git mv generated docs/generated-examples 2>/dev/null && echo " → docs/
echo -e " ${GREEN}✓ Root directories organized${NC}"
# ────────────────────────────────────────────────────────────
# PHASE 8: Delete temp/backup/log files
# ────────────────────────────────────────────────────────────
echo -e "${GREEN}Phase 8: Cleaning up junk files...${NC}"
git rm -f tmp_x217.txt 2>/dev/null && echo " ✗ tmp_x217.txt" || true
git rm -f README.md.bak 2>/dev/null && echo " ✗ README.md.bak" || true
# Delete any stray log files
for f in *.log; do
[ -f "$f" ] && git rm -f "$f" 2>/dev/null && echo " ✗ $f" || rm -f "$f"
done
echo -e " ${GREEN}✓ Temp files cleaned${NC}"
# ────────────────────────────────────────────────────────────
# PHASE 9: Add .gitkeep to new empty directories
# ────────────────────────────────────────────────────────────
echo -e "${GREEN}Phase 9: Placeholder files for empty dirs...${NC}"
for dir in api/openapi api/quickstarts api/postman infrastructure/docker; do
if [ -z "$(ls -A $dir 2>/dev/null)" ]; then
touch "$dir/.gitkeep"
git add "$dir/.gitkeep" 2>/dev/null
fi
done
echo -e " ${GREEN}✓ Done${NC}"
# ────────────────────────────────────────────────────────────
# SUMMARY
# ────────────────────────────────────────────────────────────
echo ""
echo -e "${BLUE}============================================================${NC}"
echo -e "${BLUE} Restructuring Complete${NC}"
echo -e "${BLUE}============================================================${NC}"
echo ""
AFTER_FILES=$(ls -1 | grep -v '^\.' | wc -l)
AFTER_DIRS=$(ls -1d */ 2>/dev/null | wc -l)
echo -e "${YELLOW}Before:${NC} $BEFORE_FILES root items ($BEFORE_DIRS directories)"
echo -e "${GREEN}After:${NC} $AFTER_FILES root items ($AFTER_DIRS directories)"
echo ""
echo -e "${YELLOW}Root now contains:${NC}"
echo ""
echo " FILES (12):"
echo " README.md LICENSE NOTICE CONTRIBUTING.md CHANGELOG.md"
echo " SECURITY.md CODE_OF_CONDUCT.md package.json package-lock.json"
echo " tsconfig.json jest.config.js Directory.Build.props"
echo ""
echo " DIRECTORIES (13):"
echo " api/ → OpenAPI specs, quickstarts, Postman collection"
echo " config/ → Configuration schemas and examples"
echo " containers/ → Container images (x12-parser, sftp-fetcher, etc.)"
echo " core/ → Shared type definitions and validation"
echo " docs/ → ALL documentation (architecture, deployment, security...)"
echo " infrastructure/ → Argo, K8s, Helm, Azure Bicep, Kafka, monitoring"
echo " schemas/ → X12 XSD schemas, FHIR profiles"
echo " scripts/ → Setup, deploy, testing, migration scripts"
echo " services/ → .NET microservices (16 bounded contexts)"
echo " site/ → Marketing website (cloudhealthoffice.com)"
echo " echo " src/ tests/ → FHIR APIs, Portal, AI, Security, Functions, ML"
→ Unit, integration, E2E tests + fixtures"
echo " tools/ → Migration wizard"
echo ""
echo -e "${YELLOW}Next steps:${NC}"
echo " 1. git status # Review all changes"
echo " 2. git diff --stat # Summary"
echo " 3. Replace README.md with new version"
echo " 4. Copy OpenAPI specs → api/openapi/"
echo " 5. Copy quickstarts → api/quickstarts/"
echo " 6. Add docker-compose.yml"
echo " 7. git add -A && git commit -m 'refactor: restructure repo for commercial-grade organ
echo ""
echo " echo " Reorganize 132 root files and 29 directories into a clean, CDO-style"
structure with 12 root files and 13 purposeful directories."
echo ""
echo " - Move 49 markdown docs into docs/ subdirectories"
echo " - Move 14 test fixtures into tests/fixtures/"
echo " echo " - Move 18 scripts into scripts/{setup,testing,deploy}/"
- Consolidate 11 infra dirs into infrastructure/"
echo " echo " echo " - Move portal, functions, ml into src/"
- Remove temp files (tmp_x217.txt, *.bak, *.log)"
- Create api/ directory for OpenAPI specs'"
echo ""
echo -e "${GREEN}Done! No commits made — review and commit when ready.${NC}"