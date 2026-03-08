#!/usr/bin/env bash
# ============================================================================
# Cloud Health Office — Repository Restructuring Script
# ============================================================================
# Generated: March 8, 2026 (against current main branch)
#
# Transforms the CHO repo from 44 root items into a clean commercial-grade
# structure. Uses git mv exclusively to preserve full history.
#
# CURRENT STATE (44 root items):
#   - 13 root markdown files (should be 4: README, CONTRIBUTING, SECURITY, CHANGELOG)
#   - 2 XSD schemas at root
#   - 3 test fixture files at root
#   - 4 Node.js config files at root (package.json, tsconfig, jest.config)
#   - 18 directories (several infrastructure dirs should be consolidated)
#   - 1 stray log file in src/
#
# TARGET STATE:
#   - ~10 root files (standard OSS files + solution + Node config)
#   - ~14 root directories (well-organized)
#   - All docs under docs/
#   - All infra consolidated under infrastructure/
#   - All test fixtures under tests/fixtures/
#   - New src/engines/ directory for the Benefit Calculation Engine
#
# USAGE:
#   cd cloudhealthoffice
#   chmod +x restructure-repo.sh
#   ./restructure-repo.sh
#   git status                    # Review changes
#   git diff --stat               # See full summary
#   git add -A && git commit -m "refactor: restructure repo for commercial-grade organization"
#
# SAFETY:
#   - All moves use git mv (preserves history)
#   - Does NOT auto-commit — you review first
#   - Revert everything: git checkout . && git clean -fd
#
# ============================================================================

set -euo pipefail

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; BLUE='\033[0;34m'; NC='\033[0m'

echo -e "${BLUE}============================================================================${NC}"
echo -e "${BLUE}  Cloud Health Office — Repository Restructuring (March 2026)${NC}"
echo -e "${BLUE}============================================================================${NC}"
echo ""

# ── Safety check ──
if [ ! -f "cloudhealthoffice-main.sln" ] && [ ! -f "cloudhealthoffice.sln" ]; then
    echo -e "${RED}ERROR: Can't find the solution file. Run from the repo root.${NC}"
    exit 1
fi

BEFORE_ROOT=$(ls -1 | grep -v '^\.' | wc -l)
echo -e "${YELLOW}Before: ${BEFORE_ROOT} root items${NC}"
echo ""

# ────────────────────────────────────────────────────────────
# PHASE 1: Create target directories
# ────────────────────────────────────────────────────────────
echo -e "${GREEN}Phase 1: Creating target directories...${NC}"

mkdir -p docs/guides
mkdir -p docs/governance
mkdir -p docs/implementation
mkdir -p schemas/x12
mkdir -p tests/fixtures
mkdir -p infrastructure/azure
mkdir -p infrastructure/k8s
mkdir -p infrastructure/helm
mkdir -p infrastructure/kafka
mkdir -p infrastructure/monitoring
mkdir -p infrastructure/logicapps
mkdir -p infrastructure/argo-events
mkdir -p infrastructure/argo-events/sensors
mkdir -p infrastructure/argo-workflows
mkdir -p infrastructure/docker
mkdir -p src/engines

echo -e "  ${GREEN}✓ Directories created${NC}"

# ────────────────────────────────────────────────────────────
# PHASE 2: Move root markdown files to docs/
# ────────────────────────────────────────────────────────────
echo -e "${GREEN}Phase 2: Organizing root documentation...${NC}"

# Keep at root: README.md, CONTRIBUTING.md, SECURITY.md, CHANGELOG.md, LICENSE, NOTICE
# Move everything else to docs/

# Guides / operational docs → docs/guides/
for f in QUICKSTART.md QUICK-UPDATE-GUIDE.md DEPLOYMENT.md ARCHITECTURE.md FEATURES.md; do
    [ -f "$f" ] && git mv "$f" docs/guides/ && echo "  → docs/guides/$f" || true
done

# Implementation summaries → docs/implementation/
for f in PRIOR-AUTH-IMPLEMENTATION-SUMMARY.md SECURITY-FIXES-SUMMARY.md; do
    [ -f "$f" ] && git mv "$f" docs/implementation/ && echo "  → docs/implementation/$f" || true
done

# Governance → docs/governance/
for f in CODE_OF_CONDUCT.md GOVERNANCE.md; do
    [ -f "$f" ] && git mv "$f" docs/governance/ && echo "  → docs/governance/$f" || true
done

echo -e "  ${GREEN}✓ 9 markdown files organized (4 remain at root)${NC}"

# ────────────────────────────────────────────────────────────
# PHASE 3: Move XSD schemas
# ────────────────────────────────────────────────────────────
echo -e "${GREEN}Phase 3: Moving X12 schemas...${NC}"

for f in *.xsd; do
    [ -f "$f" ] && git mv "$f" schemas/x12/ && echo "  → schemas/x12/$f" || true
done

echo -e "  ${GREEN}✓ XSD schemas moved${NC}"

# ────────────────────────────────────────────────────────────
# PHASE 4: Move test fixture files
# ────────────────────────────────────────────────────────────
echo -e "${GREEN}Phase 4: Moving test fixtures...${NC}"

for f in test-*.json test-*.edi; do
    [ -f "$f" ] && git mv "$f" tests/fixtures/ && echo "  → tests/fixtures/$f" || true
done

echo -e "  ${GREEN}✓ Test fixtures moved${NC}"

# ────────────────────────────────────────────────────────────
# PHASE 5: Consolidate infrastructure directories
# ────────────────────────────────────────────────────────────
echo -e "${GREEN}Phase 5: Consolidating infrastructure...${NC}"

# argo-events/ → infrastructure/argo-events/
if [ -d "argo-events" ]; then
    # Move files
    for f in argo-events/*.yaml argo-events/*.yml; do
        [ -f "$f" ] && git mv "$f" infrastructure/argo-events/ || true
    done
    # Move sensors subdirectory
    if [ -d "argo-events/sensors" ]; then
        for f in argo-events/sensors/*; do
            [ -e "$f" ] && git mv "$f" infrastructure/argo-events/sensors/ || true
        done
        rmdir argo-events/sensors 2>/dev/null || true
    fi
    rmdir argo-events 2>/dev/null || true
    echo "  → infrastructure/argo-events/"
fi

# argo-workflows/ → infrastructure/argo-workflows/
if [ -d "argo-workflows" ]; then
    for f in argo-workflows/*; do
        [ -e "$f" ] && git mv "$f" infrastructure/argo-workflows/ || true
    done
    rmdir argo-workflows 2>/dev/null || true
    echo "  → infrastructure/argo-workflows/"
fi

# infra/ → infrastructure/azure/ (Bicep files + nested dirs)
if [ -d "infra" ]; then
    # Move top-level files
    for f in infra/*.bicep infra/*.json; do
        [ -f "$f" ] && git mv "$f" infrastructure/azure/ || true
    done
    # Move subdirectories
    for d in infra/modules infra/azure infra/marketplace; do
        if [ -d "$d" ]; then
            target="infrastructure/azure/$(basename $d)"
            mkdir -p "$target"
            for f in "$d"/*; do
                [ -e "$f" ] && git mv "$f" "$target/" || true
            done
            rmdir "$d" 2>/dev/null || true
        fi
    done
    # Move nested infra/k8s, infra/kafka, infra/helm, infra/logicapps
    for subdir in k8s kafka helm logicapps; do
        if [ -d "infra/$subdir" ]; then
            for f in "infra/$subdir"/*; do
                [ -e "$f" ] && git mv "$f" "infrastructure/$subdir/" || true
            done
            rmdir "infra/$subdir" 2>/dev/null || true
        fi
    done
    # Clean up infra/
    find infra -type d -empty -delete 2>/dev/null || true
    rmdir infra 2>/dev/null || true
    echo "  → infrastructure/azure/ (Bicep + modules)"
fi

# helm/ (root) → infrastructure/helm/
if [ -d "helm" ]; then
    # helm/cloudhealthoffice/ is a chart directory
    if [ -d "helm/cloudhealthoffice" ]; then
        mkdir -p infrastructure/helm/cloudhealthoffice
        for f in helm/cloudhealthoffice/*; do
            [ -e "$f" ] && git mv "$f" infrastructure/helm/cloudhealthoffice/ || true
        done
        rmdir helm/cloudhealthoffice 2>/dev/null || true
    fi
    for f in helm/*; do
        [ -e "$f" ] && git mv "$f" infrastructure/helm/ || true
    done
    rmdir helm 2>/dev/null || true
    echo "  → infrastructure/helm/"
fi

# monitoring/ → infrastructure/monitoring/
if [ -d "monitoring" ]; then
    for f in monitoring/*; do
        if [ -d "$f" ]; then
            target="infrastructure/monitoring/$(basename $f)"
            mkdir -p "$target"
            for ff in "$f"/*; do
                [ -e "$ff" ] && git mv "$ff" "$target/" || true
            done
            rmdir "$f" 2>/dev/null || true
        else
            [ -e "$f" ] && git mv "$f" infrastructure/monitoring/ || true
        fi
    done
    rmdir monitoring 2>/dev/null || true
    echo "  → infrastructure/monitoring/"
fi

# logicapps/ (root) → infrastructure/logicapps/
if [ -d "logicapps" ]; then
    for f in logicapps/*; do
        if [ -d "$f" ]; then
            target="infrastructure/logicapps/$(basename $f)"
            mkdir -p "$target"
            for ff in "$f"/*; do
                [ -e "$ff" ] && git mv "$ff" "$target/" || true
            done
            rmdir "$f" 2>/dev/null || true
        else
            [ -e "$f" ] && git mv "$f" infrastructure/logicapps/ || true
        fi
    done
    rmdir logicapps 2>/dev/null || true
    echo "  → infrastructure/logicapps/"
fi

# attachments_logicapps_package/ → infrastructure/logicapps/attachments-package/
if [ -d "attachments_logicapps_package" ]; then
    mkdir -p infrastructure/logicapps/attachments-package
    for f in attachments_logicapps_package/*; do
        [ -e "$f" ] && git mv "$f" infrastructure/logicapps/attachments-package/ || true
    done
    rmdir attachments_logicapps_package 2>/dev/null || true
    echo "  → infrastructure/logicapps/attachments-package/"
fi

echo -e "  ${GREEN}✓ Infrastructure consolidated${NC}"

# ────────────────────────────────────────────────────────────
# PHASE 6: Move remaining root directories
# ────────────────────────────────────────────────────────────
echo -e "${GREEN}Phase 6: Organizing remaining root directories...${NC}"

# fundraising/ → docs/fundraising/ (not app code)
[ -d "fundraising" ] && git mv fundraising docs/fundraising && echo "  → docs/fundraising/" || true

# generated/ → docs/generated/ (generated examples, not app code)
[ -d "generated" ] && git mv generated docs/generated && echo "  → docs/generated/" || true

# ml/ → src/ml/ (ML models are app artifacts)
[ -d "ml" ] && git mv ml src/ml && echo "  → src/ml/" || true

echo -e "  ${GREEN}✓ Directories organized${NC}"

# ────────────────────────────────────────────────────────────
# PHASE 7: Clean up stray/junk files
# ────────────────────────────────────────────────────────────
echo -e "${GREEN}Phase 7: Cleaning up stray files...${NC}"

# Stray log file in src/
if [ -f "src/portal.cloudhealthoffice.com-1770425021345.log" ]; then
    git rm -f "src/portal.cloudhealthoffice.com-1770425021345.log" 2>/dev/null || rm -f "src/portal.cloudhealthoffice.com-1770425021345.log"
    echo "  ✗ src/portal.cloudhealthoffice.com-*.log (deleted)"
fi

# Any other stray log/bak/tmp files at root
for f in *.log *.bak tmp_*; do
    [ -f "$f" ] && { git rm -f "$f" 2>/dev/null || rm -f "$f"; echo "  ✗ $f (deleted)"; } || true
done

# Broken symlink: logicapps → infra/logicapps (infra/ was moved to infrastructure/)
if [ -L "logicapps" ]; then
    rm -f logicapps
    echo "  ✗ logicapps (broken symlink, was pointing to old infra/logicapps)"
fi

echo -e "  ${GREEN}✓ Stray files cleaned${NC}"

# ────────────────────────────────────────────────────────────
# PHASE 8: Create src/engines/ for Benefit Engine
# ────────────────────────────────────────────────────────────
echo -e "${GREEN}Phase 8: Creating src/engines/ for Benefit Calculation Engine...${NC}"

mkdir -p src/engines/.gitkeep_dir
touch src/engines/.gitkeep
rmdir src/engines/.gitkeep_dir 2>/dev/null || true

echo -e "  ${GREEN}✓ src/engines/ ready for CloudHealthOffice.BenefitEngine${NC}"

# ────────────────────────────────────────────────────────────
# PHASE 9: Update .gitignore
# ────────────────────────────────────────────────────────────
echo -e "${GREEN}Phase 9: Updating .gitignore...${NC}"

# Append root-clutter prevention rules if not already present
if ! grep -q "# Root clutter prevention" .gitignore 2>/dev/null; then
    cat >> .gitignore << 'GITIGNORE'

# Root clutter prevention (added by restructure script)
/*.log
/*.bak
/tmp_*
*.DS_Store
GITIGNORE
    echo "  → Added root clutter prevention rules"
fi

echo -e "  ${GREEN}✓ .gitignore updated${NC}"

# ────────────────────────────────────────────────────────────
# SUMMARY
# ────────────────────────────────────────────────────────────
echo ""
echo -e "${BLUE}============================================================================${NC}"
echo -e "${BLUE}  Restructuring Complete!${NC}"
echo -e "${BLUE}============================================================================${NC}"
echo ""

AFTER_ROOT=$(ls -1 | grep -v '^\.' | wc -l)
echo -e "${YELLOW}Before: ${BEFORE_ROOT} root items${NC}"
echo -e "${GREEN}After:  ${AFTER_ROOT} root items${NC}"
echo ""

echo -e "Root items remaining:"
ls -1p | grep -v '^\.'
echo ""

echo -e "${BLUE}Next steps:${NC}"
echo "  1. Review:  git status && git diff --stat"
echo "  2. Commit:  git add -A && git commit -m 'refactor: restructure repo for commercial-grade organization'"
echo "  3. Add benefit engine files to src/engines/CloudHealthOffice.BenefitEngine/"
echo "  4. Add engine to cloudhealthoffice-main.sln"
echo ""
echo -e "${YELLOW}To undo everything: git checkout . && git clean -fd${NC}"