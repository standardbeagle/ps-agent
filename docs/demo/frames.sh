#!/usr/bin/env bash
# Extract still frames from a recording, for checking a demo without watching it.
#   frames.sh <file.mp4|file.gif> <seconds> [seconds ...]
# Frames land beside the recording as <name>_t<seconds>.png.
set -euo pipefail

src="$1"; shift
[ -f "$src" ] || { echo "no such recording: $src" >&2; exit 1; }

dir="$(cd "$(dirname "$src")" && pwd)"
base="$(basename "${src%.*}")"

echo "duration: $(ffprobe -v error -show_entries format=duration -of csv=p=0 "$src")s"

for t in "$@"; do
  out="$dir/${base}_t${t}.png"
  ffmpeg -y -loglevel error -ss "$t" -i "$src" -update 1 -frames:v 1 "$out"
  echo "  ${base}_t${t}.png"
done
