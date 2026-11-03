#!/bin/bash
# Public packaging checks live in PowerShell; Git Bash remains the documented entry point.
# --release: verified signing + mandatory Defender scan; --github: the unsigned public build GitHub makes for its
# Releases page; default: unsigned test build.
set -euo pipefail
cd "$(dirname "$0")/../.."
args=()
for arg in "$@"; do
  case "$arg" in
    --release) args+=(-Release) ;;
    --scan) args+=(-Scan) ;;
    --github) args+=(-GitHub) ;;
    *) echo "usage: $0 [--release] [--scan] [--github]" >&2; exit 2 ;;
  esac
done
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$(cygpath -w OutsideRecorder/installer/Build-Installer.ps1)" "${args[@]}"
