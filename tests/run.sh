#!/usr/bin/env bash
# Runs the checks. Always: the mod's decisions (src/*.Pure.cs), headless, with no game or Unity assembly.
# With the game installed: also the game's own mod scanner on the built mod (run ./build.sh first).
#   GAME=/d/SteamLibrary/steamapps/common/"Probably Stolen" CSC=/path/to/csc.exe tests/run.sh
set -euo pipefail
cd "$(dirname "$0")/.."
GAME="${GAME:-/d/SteamLibrary/steamapps/common/Probably Stolen}"
CSC="${CSC:-/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe}"
M="$GAME/Probably Stolen_Data/Managed"
# The .NET Framework csc reads "/" as an option prefix and fails on long paths: work in a short folder.
WORK="${TEMP:-/tmp}/slc_tests"
rm -rf "${WORK:?}" && mkdir -p "$WORK"
cp tests/PureTest.cs tests/ScanTest.cs src/*.Pure.cs "$WORK/"

# The engine files are compiled together with PureTest.cs, so any game or Unity type in a *.Pure.cs file fails here.
pure=()
for f in src/*.Pure.cs; do pure+=("$(basename "$f")"); done
( cd "$WORK" && "$CSC" -nologo -out:PureTest.exe PureTest.cs "${pure[@]}" >/dev/null && ./PureTest.exe | grep -v '^ok' )

if [ -f "$M/Assembly-CSharp.dll" ] && [ -f out/save_language_converter/SaveLanguageConverter.dll ]; then
  refs=()
  for r in mscorlib netstandard System System.Core Assembly-CSharp; do refs+=("-r:$M/$r.dll"); done
  ( cd "$WORK" && "$CSC" -nologo -nostdlib -noconfig "${refs[@]}" -out:ScanTest.exe ScanTest.cs >/dev/null )
  echo "-- the game's mod scanner"
  # Outside the game the scanner reports a load error for netstandard types; only the capabilities count.
  ( cd "$WORK" && ./ScanTest.exe "$(cygpath -w "$M")" "$(cygpath -w "$OLDPWD/out/save_language_converter")" | sed -n 2p || true ) | tee "$WORK/scan.txt"
  tr -d '\r' < "$WORK/scan.txt" | grep -qx 'capabilities: none'
else
  echo "-- game or build not found: scanner skipped"
fi
