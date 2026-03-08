#!/usr/bin/env bash
# Quick fixes for the post-restructure repo
# Run from repo root: ./fix-issues.sh

set -euo pipefail

echo "=== Fix 1: Rename engine csproj (missing .csproj extension) ==="
git mv "src/engines/CloudHealthOffice.BenefitEngine/CloudHealthOffice.BenefitEngine" \
       "src/engines/CloudHealthOffice.BenefitEngine/CloudHealthOffice.BenefitEngine.csproj"
echo "  ✓ Renamed to CloudHealthOffice.BenefitEngine.csproj"

echo ""
echo "=== Fix 2: Fix test directory name and csproj filename ==="
# Rename directory from .Test to .Tests
git mv "tests/CloudHealthOffice.BenefitEngine.Test" \
       "tests/CloudHealthOffice.BenefitEngine.Tests"
# Fix the leading space in the csproj filename
git mv "tests/CloudHealthOffice.BenefitEngine.Tests/ CloudHealthOffice.BenefitEngine.Tests.csproj" \
       "tests/CloudHealthOffice.BenefitEngine.Tests/CloudHealthOffice.BenefitEngine.Tests.csproj"
echo "  ✓ Directory renamed to .Tests, csproj filename fixed"

echo ""
echo "=== Fix 3: Organize engine source into subdirectories ==="
cd src/engines/CloudHealthOffice.BenefitEngine

mkdir -p Domain Models Services Configuration

# Domain
[ -f BenefitDomain.cs ] && git mv BenefitDomain.cs Domain/

# Models
[ -f BenefitModels.cs ] && git mv BenefitModels.cs Models/

# Services (keep existing Services/ contents, add more)
[ -f BenefitCalculationEngine.cs ] && git mv BenefitCalculationEngine.cs Services/
[ -f AccumulatorWorkingSet.cs ] && git mv AccumulatorWorkingSet.cs Services/
[ -f ServiceCategoryResolver.cs ] && git mv ServiceCategoryResolver.cs Services/
[ -f Providers.cs ] && git mv Providers.cs Services/

# Configuration
[ -f BenefitEngineRegistration.cs ] && git mv BenefitEngineRegistration.cs Configuration/

cd ../../..

echo "  ✓ Source files organized into Domain/, Models/, Services/, Configuration/"

echo ""
echo "=== Fix 4: Replace NOTICE file ==="
cat > NOTICE << 'EOF'
Cloud Health Office
Copyright 2024-2026 Aurelianware, Inc.

This product is licensed under the Business Source License 1.1.
See the LICENSE file for the full license text.

The Licensed Work converts to Apache License, Version 2.0 on the Change Date
specified in the LICENSE file (2030-03-08).

For commercial licensing inquiries: licensing@cloudhealthoffice.com
EOF
git add NOTICE
echo "  ✓ NOTICE file replaced (was a duplicate of LICENSE)"

echo ""
echo "=== Done ==="
echo ""
echo "Remaining manual steps:"
echo "  1. dotnet sln add src/engines/CloudHealthOffice.BenefitEngine/CloudHealthOffice.BenefitEngine.csproj"
echo "  2. dotnet sln add tests/CloudHealthOffice.BenefitEngine.Tests/CloudHealthOffice.BenefitEngine.Tests.csproj"
echo "  3. git add -A && git commit -m 'fix: correct csproj names, organize engine, fix NOTICE'"
echo "  4. rm fix-issues.sh && git add -A && git commit -m 'chore: remove fix script'"