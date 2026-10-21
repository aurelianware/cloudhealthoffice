#!/usr/bin/env bash
# Rebuilds idcard-service's embedded Noto Sans subsets.
#
# SkiaSharp 2.88's PDF backend embeds the whole font file in every PDF (it does
# not subset), so the service ships fonts already cut down to the characters a
# US payer ID card needs: Latin (Basic, Latin-1, Extended-A/B, Extended
# Additional, which covers Spanish, Vietnamese, Tagalog, French, Haitian Creole
# and similar), combining marks, punctuation, currency, letterlike symbols and
# arrows. Hinting and OpenType layout tables are dropped: PDFs do not use
# hinting, and Skia draws this text without shaping. Each subset is ~89 KB
# instead of ~630 KB.
#
# Source: Noto Sans 2.015 (SIL OFL 1.1, no Reserved Font Name, so a subset may
# keep the name), taken from the @expo-google-fonts/noto-sans npm package.
# Requires npm and fonttools (pip install fonttools).
set -euo pipefail

VERSION="0.4.2"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
OUT="$ROOT/src/services/idcard-service/Fonts"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

(cd "$WORK" && npm pack --silent "@expo-google-fonts/noto-sans@$VERSION" >/dev/null && tar -xzf ./*.tgz)
PKG="$WORK/package"

UNICODES="U+0000-024F,U+0300-036F,U+1E00-1EFF,U+2000-206F,U+20A0-20CF,U+2100-214F,U+2190-21FF,U+2212,U+25CF,U+FFFD"
subset() {
  pyftsubset "$PKG/$1/NotoSans_$1.ttf" --unicodes="$UNICODES" --no-hinting \
    --layout-features='' --drop-tables+=GSUB,GPOS,GDEF,DSIG --name-IDs='*' \
    --notdef-outline --output-file="$OUT/$2"
}
subset 400Regular NotoSans-Regular.ttf
subset 600SemiBold NotoSans-SemiBold.ttf
cp "$PKG/LICENSE_FONT" "$OUT/OFL.txt"
ls -l "$OUT"
