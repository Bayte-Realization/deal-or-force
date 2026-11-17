#!/bin/sh
# Photographs every state of the mockup at 1:1 with headless Chrome and prints the census.
# Usage (from the mockup folder, with "python -m http.server 8765" running there):
#   sh tools/capture.sh <out-dir>
# then  python tools/finish_shots.py <out-dir> shots [old-state-1.jpg]  makes the JPGs, the flag sheet
# and the before/after comparison.
OUT=${1:-shots}; mkdir -p "$OUT"; OUT=$(cd "$OUT" && pwd)
CHROME="/c/Program Files/Google/Chrome/Application/chrome.exe"
shot() { # name state outliner [flag]
  if [ $2 = 7 ]; then W=1280; H=720; else W=1920; H=1080; fi
  "$CHROME" --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 \
    --window-size=$W,$H --virtual-time-budget=2500 --screenshot="$(cygpath -w "$OUT/$1.png")" \
    "http://127.0.0.1:8765/index.html?shot&state=$2&outliner=$3${4:+&flag=$4}" >/dev/null 2>&1
}
for s in 1 2 3 4 5 6 7 8; do shot state-$s $s 0; done
shot state-1-open 1 1
shot state-7-open 7 1
shot state-8-open 8 1
# revision 3: the flag frame with other proportions (1:1, Nepal's outline, 11:28), at rest and in the panel
for f in che npl qat vat; do shot flag-$f 1 0 $f; shot flagpanel-$f 2 0 $f; done
shot flag-720-qat 7 0 qat
# the census of every rest position, read from the page itself (buttons <= 40, panels <= 6, repeats 0)
census() { # state outliner
  if [ $1 = 7 ]; then W=1280; H=720; else W=1920; H=1080; fi
  "$CHROME" --headless=new --disable-gpu --window-size=$W,$H --virtual-time-budget=2500 --dump-dom     "http://127.0.0.1:8765/index.html?shot&state=$1&outliner=$2" 2>/dev/null | grep -o '<div id="census".*' | sed 's/<[^>]*>//g' | head -1
}
for s in 1 6 7 8; do census $s 0; census $s 1; done
ls "$OUT"
