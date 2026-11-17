#!/bin/sh
# Photographs every state of the mockup at 1:1 with headless Chrome and prints the census.
# Usage (from the mockup folder, with "python -m http.server 8765" running there):
#   sh tools/capture.sh <out-dir>
OUT=${1:-shots}; mkdir -p "$OUT"; OUT=$(cd "$OUT" && pwd)
CHROME="/c/Program Files/Google/Chrome/Application/chrome.exe"
shot() { # name state outliner
  if [ $2 = 7 ]; then W=1280; H=720; else W=1920; H=1080; fi
  "$CHROME" --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 \
    --window-size=$W,$H --virtual-time-budget=2500 --screenshot="$(cygpath -w "$OUT/$1.png")" \
    "http://127.0.0.1:8765/index.html?shot&state=$2&outliner=$3" >/dev/null 2>&1
}
for s in 1 2 3 4 5 6 7 8; do shot state-$s $s 0; done
shot state-1-open 1 1
shot state-7-open 7 1
shot state-8-open 8 1
ls "$OUT"
