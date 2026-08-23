#!/usr/bin/env bash
# Record the ps-agent demo tapes with VHS.
#
# VHS needs a real PTY. On Windows that means running it from WSL and letting the tape launch
# `pwsh.exe` through interop — the console that process gets is a genuine one, so the interactive
# viewer runs rather than falling back to the headless pipeline.
#
# Prerequisites inside the distro: vhs, ttyd, ffmpeg.
#   wsl -d Ubuntu-24.04
#   sudo apt install ttyd ffmpeg
#   curl -fsSL https://github.com/charmbracelet/vhs/releases/latest/download/vhs_<ver>_Linux_x86_64.tar.gz | tar xz
#
# Usage (from the repo root, inside WSL):
#   docs/demo/record.sh [tape-name ...]      # default: every tape in docs/demo
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
VHS="${VHS:-$HOME/.local/bin/vhs}"
command -v "$VHS" >/dev/null 2>&1 || VHS=vhs

tapes=("$@")
if [ ${#tapes[@]} -eq 0 ]; then
  mapfile -t tapes < <(cd "$HERE" && ls -1 ./*.tape | sed 's|^\./||; s|\.tape$||')
fi

for name in "${tapes[@]}"; do
  src="$HERE/$name.tape"
  [ -f "$src" ] || { echo "no such tape: $src" >&2; exit 1; }

  # Copy to /tmp and normalise line endings: the repo is on an NTFS mount, and VHS's parser does
  # not tolerate CRLF.
  work="/tmp/$name.tape"
  cp "$src" "$work"
  sed -i 's/\r$//' "$work"

  echo "== recording $name"
  ( cd /tmp && "$VHS" "$work" )

  # Collect what the tape actually declared, rather than guessing the filename from the tape's
  # name — they need not match, and guessing wrong copies nothing while still reporting success.
  mapfile -t outputs < <(grep -iE '^[[:space:]]*Output[[:space:]]' "$work" \
    | sed -E 's/^[[:space:]]*Output[[:space:]]+//; s/^"//; s/"$//')

  if [ ${#outputs[@]} -eq 0 ]; then
    echo "   !! $name declares no Output line" >&2
    exit 1
  fi

  for out in "${outputs[@]}"; do
    if [ ! -f "$out" ]; then
      echo "   !! expected $out, which VHS did not produce" >&2
      exit 1
    fi

    cp "$out" "$HERE/$(basename "$out")"
    size=$(du -h "$out" | cut -f1)
    dur=$(ffprobe -v error -show_entries format=duration -of csv=p=0 "$out" 2>/dev/null || echo '?')
    printf '   %-28s %6s  %ss\n' "$(basename "$out")" "$size" "$dur"
  done
done
