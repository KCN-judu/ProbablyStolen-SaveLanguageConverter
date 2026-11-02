#!/usr/bin/env bash
# Builds the mod with the C# 5 compiler that ships with Windows, against the game's own Mono assemblies.
#   GAME=/d/SteamLibrary/steamapps/common/"Probably Stolen" CSC=/path/to/csc.exe ./build.sh [--install]
set -euo pipefail
cd "$(dirname "$0")"
GAME="${GAME:-/d/SteamLibrary/steamapps/common/Probably Stolen}"
M="$GAME/Probably Stolen_Data/Managed"
CSC="${CSC:-/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe}"
mkdir -p out/save_language_converter

refs=()
for r in mscorlib netstandard System System.Core Assembly-CSharp Assembly-CSharp-firstpass 0Harmony \
         UnityEngine UnityEngine.CoreModule UnityEngine.UI UnityEngine.UIModule Unity.TextMeshPro \
         Unity.Localization Unity.ResourceManager; do
  [ -f "$M/$r.dll" ] && refs+=("-r:$M/$r.dll")
done

# The .NET Framework csc cannot create its temp files under long paths, so compile in a short folder.
# It also reads "/" as an option prefix, so sources and output use backslashes.
WORK="${TEMP:-/tmp}/slc_build"
rm -rf "$WORK" && mkdir -p "$WORK/src"
cp src/*.cs "$WORK/src/"
srcs=()
for f in src/*.cs; do srcs+=("src\\$(basename "$f")"); done
( cd "$WORK" && "$CSC" -nologo -nostdlib -noconfig -target:library -optimize+ -warn:4 -nowarn:1701,1702 \
  "${refs[@]}" '-out:SaveLanguageConverter.dll' "${srcs[@]}" )
cp "$WORK/SaveLanguageConverter.dll" out/save_language_converter/

cp manifest.xml out/save_language_converter/
cp preview.png out/save_language_converter/
mkdir -p out/save_language_converter/Localization
cp Localization/*.csv out/save_language_converter/Localization/
echo "built out/save_language_converter"

if [ "${1:-}" = "--install" ]; then
  rm -rf "$GAME/Mods/save_language_converter"
  cp -r out/save_language_converter "$GAME/Mods/"
  echo "installed to $GAME/Mods/save_language_converter"
fi
