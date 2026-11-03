#!/bin/bash
# slotdis.sh <dll> <slot>...: the CvDllPreGame slot's thunk and the CvPreGame body it calls
B=out/binaryverify/BinaryVerify.exe; D="$1"; shift
for k in "$@"; do
  echo "### slot $k"
  V=$("$B" "$D" vfn ".?AVCvDllPreGame@@" $k)
  echo "$V" | head -8
  T=$(echo "$V" | grep -o 'call 10[0-9A-F]*h' | head -1 | sed 's/call 10//; s/h//')
  [ -n "$T" ] && { echo "  body $T:"; "$B" "$D" dis "$T" 18; }
done
