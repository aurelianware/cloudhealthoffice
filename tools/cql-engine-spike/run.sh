#!/usr/bin/env bash
# run.sh — reproduce the ADR 035 CQL engine spike.
#
# Runs the public CMS125 Breast Cancer Screening eCQM (QI-Core 2025, 66 test
# cases with expected MeasureReports) through two CQL engines:
#   java    HL7/CQF clinical-reasoning 4.14.0 (R4MultiMeasureService)
#   dotnet  Firely CQL SDK 2.18.0 (ELM compiled to .NET, definitions invoked)
#
# Needs: git, java 21 + maven, .NET 8 SDK, python3, network to GitHub,
# Maven Central and nuget.org. No PHI and no CHO services are involved.
#
# Usage:
#   ./run.sh tests                 # both engines against the 66 test cases
#   ./run.sh scale <copies> [threads]
#                                  # clone each test patient <copies> times
#                                  # (66 x copies members) and time both engines

set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORK="$HERE/work"
DQM_COMMIT="5f840c8792ab3d0c026fc17d07847d782d34e482"     # cqframework/dqm-content-qicore-2025
FIRELY_COMMIT="38b711569a90e2d903979a150cea848fdca2fa64"  # FirelyTeam/firely-cql-sdk (2.18.0)
MODE="${1:-tests}"

fetch() { # repo commit dir
  [[ -d "$WORK/$3/.git" ]] && return
  git init -q "$WORK/$3"
  git -C "$WORK/$3" fetch -q --depth 1 "https://github.com/$1" "$2"
  git -C "$WORK/$3" checkout -q FETCH_HEAD
}
mkdir -p "$WORK"
fetch cqframework/dqm-content-qicore-2025 "$DQM_COMMIT" dqm
fetch FirelyTeam/firely-cql-sdk "$FIRELY_COMMIT" firely

BUNDLE="$WORK/dqm/bundles/measure/CMS125FHIRBreastCancerScreen/CMS125FHIRBreastCancerScreen-bundle.json"

# Firely cannot compile the published ELM as is: it rejects namespaced includes
# and needs explicit DateTime casts in QICoreCommon. The Firely repository
# carries the same libraries with those casts, compiled by the Java translator.
ELM="$WORK/elm-firely"
mkdir -p "$ELM"
for lib in CMS125FHIRBreastCancerScreen Hospice QICoreCommon AdvancedIllnessandFrailty PalliativeCare \
           FHIRHelpers Status SupplementalDataElements AdultOutpatientEncounters CumulativeMedicationDuration; do
  cp "$WORK/firely/LibrarySets/dqm-content-qicore-2025/Elm/$lib.json" "$ELM/"
done

( cd "$HERE/java" && mvn -q -B dependency:copy-dependencies -DoutputDirectory=lib \
    && mkdir -p out && javac -d out -cp "lib/*" src/main/java/spike/Harness.java )
( cd "$HERE/dotnet" && dotnet build -c Release -v q -nologo >/dev/null )

# The published bundle carries one partial VSAC expansion (AdvancedIllness,
# 1000 of 1997 codes). Java uses it silently; Firely refuses it. ALLOW_PARTIAL=1
# makes Firely use it the same way, so both engines see identical data.
export ALLOW_PARTIAL=1
if [[ "$MODE" == "tests" ]]; then
  java -cp "$HERE/java/out:$HERE/java/lib/*" spike.Harness "$BUNDLE" tests 2>&1 | grep -E '^(java|FAIL|ERROR)'
  dotnet "$HERE/dotnet/bin/Release/net8.0/CqlEngineSpike.dll" "$BUNDLE" "$ELM" tests 2>&1 | grep -E '^(firely|FAIL|ERROR|partial)'
else
  COPIES="${2:?copies}"; THREADS="${3:-4}"
  java -Xmx8g -cp "$HERE/java/out:$HERE/java/lib/*" spike.Harness "$BUNDLE" scale "$COPIES" "$THREADS" 2>&1 | grep -E '^java'
  dotnet "$HERE/dotnet/bin/Release/net8.0/CqlEngineSpike.dll" "$BUNDLE" "$ELM" scale "$COPIES" "$THREADS" 2>&1 | grep -E '^firely'
fi
