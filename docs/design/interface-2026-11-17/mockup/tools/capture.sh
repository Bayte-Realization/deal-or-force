#!/bin/sh
# Photographs every state of the mockup at 1:1 with headless Chrome.
# Usage (from the mockup folder, with "python -m http.server 8765" running there):
#   sh tools/capture.sh <out-dir>
OUT=${1:-shots}; mkdir -p "$OUT"
CHROME="/c/Program Files/Google/Chrome/Application/chrome.exe"
for s in 1 2 3 4 5 6 7; do
  if [ $s = 7 ]; then W=1280; H=720; else W=1920; H=1080; fi
  "$CHROME" --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 \
    --window-size=$W,$H --virtual-time-budget=1500 --screenshot="$(cygpath -w "$OUT")\state-$s.png" \
    "http://127.0.0.1:8765/index.html?shot&state=$s" >/dev/null 2>&1
done
ls "$OUT"
