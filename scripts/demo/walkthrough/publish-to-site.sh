#!/usr/bin/env bash
# Publishes a recorded walkthrough to the marketing site (cloudhealthoffice.com/demo).
#
# Takes the "Record demo walkthrough" workflow's output (the unzipped
# demo-walkthrough-<name> artifact, its .zip, or ./out after a local run) and
# writes two web-sized files to src/site/media/demo/:
#
#   <name>.mp4          H.264, 1280 wide, faststart, no audio. Plays in every
#                       current browser, Safari and iOS included. (A VP9 WebM
#                       came out larger than this for screen recordings, so
#                       the site serves MP4 only.)
#   <name>-poster.jpg   the title card (01title.png), shown before play
#
# Usage:
#   scripts/demo/walkthrough/publish-to-site.sh ~/Downloads/demo-walkthrough-texas.zip
#   scripts/demo/walkthrough/publish-to-site.sh scripts/demo/walkthrough/out texas
#
# The name defaults to the .webm file found in the input (texas.webm -> texas).
# The page markup does not change when a file is replaced; only check the
# duration and step list on src/site/demo/index.html if the story changed.
# Requires ffmpeg.
set -euo pipefail

if [ $# -lt 1 ]; then
  sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'
  exit 64
fi
command -v ffmpeg >/dev/null || { echo "ffmpeg is required" >&2; exit 69; }

repo_root="$(cd "$(dirname "$0")/../../.." && pwd)"
dest="$repo_root/src/site/media/demo"
input="$1"
work=""
cleanup() { if [ -n "$work" ]; then rm -rf "$work"; fi; }
trap cleanup EXIT

if [ -f "$input" ] && [[ "$input" == *.zip ]]; then
  work="$(mktemp -d)"
  unzip -q "$input" -d "$work"
  input="$work"
fi
[ -d "$input" ] || { echo "Not a directory or .zip: $1" >&2; exit 66; }

if [ $# -ge 2 ]; then
  name="$2"
  src="$input/$name.webm"
else
  src="$(find "$input" -maxdepth 2 -name '*.webm' | head -n 1)"
  name="$(basename "${src%.webm}")"
fi
[ -f "$src" ] || { echo "No walkthrough video found in $input" >&2; exit 66; }
case "$name" in *[!a-z0-9-]*|"") echo "Unexpected video name: $name" >&2; exit 65;; esac

poster_src="$(find "$(dirname "$src")" -maxdepth 2 -name '01*.png' | sort | head -n 1)"
mkdir -p "$dest"
scale="scale='min(1280,iw)':-2"

echo "Encoding $name.mp4"
ffmpeg -y -loglevel error -i "$src" -an -vf "$scale" \
  -c:v libx264 -preset slow -crf 26 -pix_fmt yuv420p -movflags +faststart \
  "$dest/$name.mp4"

echo "Writing $name-poster.jpg"
if [ -n "$poster_src" ]; then
  ffmpeg -y -loglevel error -i "$poster_src" -vf "$scale" -q:v 4 "$dest/$name-poster.jpg"
else
  ffmpeg -y -loglevel error -i "$src" -frames:v 1 -vf "$scale" -q:v 4 "$dest/$name-poster.jpg"
fi

duration="$(ffprobe -v error -show_entries format=duration -of csv=p=0 "$dest/$name.mp4" 2>/dev/null || true)"
ls -lh "$dest/$name.mp4" "$dest/$name-poster.jpg" | awk '{print $5, $9}'
if [ -n "$duration" ]; then printf 'Duration: %.0f seconds. Update "duration" on src/site/demo/index.html if it changed.\n' "$duration"; fi
