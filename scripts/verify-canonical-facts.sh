#!/usr/bin/env bash
# Recomputes the Canonical Facts table in docs/POSITIONING.md from the
# working tree. Run from the repository root. Build outputs (bin/, obj/),
# node_modules and generated migrations are excluded everywhere.
set -euo pipefail

cd "$(git rev-parse --show-toplevel)"

prune=(-path '*/bin' -prune -o -path '*/obj' -prune -o -path '*/node_modules' -prune -o -path '*/Migrations' -prune -o)

service_dirs=$(find src/services -mindepth 1 -maxdepth 1 -type d ! -name shared | wc -l)
shared_projects=$(find src/services/shared -name '*.csproj' ! -name '*Tests.csproj' | wc -l)
engine_projects=$(find src/engines -name '*.csproj' ! -name '*Tests.csproj' | wc -l)
test_projects=$(find src tests "${prune[@]}" -name '*.Tests.csproj' -print | wc -l)
test_methods=$(find src tests "${prune[@]}" -name '*.cs' -print0 \
  | xargs -0 grep -hoE '^\s*\[(Fact|Theory)(\(|\])' | wc -l)
prod_lines=$(find src "${prune[@]}" -name '*.cs' -print \
  | grep -viE '(^|/)[^/]*tests?(/|\.)|/Tests/' | xargs cat | wc -l)
doc_lines=$(find docs "${prune[@]}" \( -name '*.md' -o -name '*.txt' \) -print0 | xargs -0 cat | wc -l)

cat <<EOF
| Metric | Value |
| --- | --- |
| Service directories (src/services, excluding shared) | ${service_dirs} |
| Shared service libraries (src/services/shared, non-test) | ${shared_projects} |
| Engine projects (src/engines, non-test) | ${engine_projects} |
| Test projects (*.Tests.csproj) | ${test_projects} |
| Test methods ([Fact] + [Theory]) | ${test_methods} |
| Production C# lines | ${prod_lines} |
| Documentation lines (docs/, .md and .txt) | ${doc_lines} |
EOF
