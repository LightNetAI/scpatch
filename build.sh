#!/bin/sh
# Build scpatch.dll and ScreenConnect.SqlFixer.dll.
#
# The tool targets .NET 8; the fixer must target .NET Framework 4.x because it is
# loaded by ScreenConnect's 32/64-bit CLR.
set -e
cd "$(dirname "$0")"

DOTNET=${DOTNET:-dotnet}
DOTNET_DIR=$(dirname "$(readlink -f "$DOTNET")")

if [ ! -f bin/dnlib.dll ]; then
    echo "bin/dnlib.dll is missing - put the dnlib assembly there first" >&2
    exit 1
fi

REF=$(ls -d "$DOTNET_DIR"/packs/Microsoft.NETCore.App.Ref/*/ref/net8.0 2>/dev/null | tail -1)
CSC=$(ls -d "$DOTNET_DIR"/sdk/*/Roslyn/bincore/csc.dll 2>/dev/null | tail -1)
if [ -z "$REF" ] || [ -z "$CSC" ]; then
    echo "Could not find the .NET 8 SDK under $DOTNET_DIR" >&2
    exit 1
fi
ls "$REF"/*.dll | sed 's/^/-r:/' > /tmp/scpatch-refs.rsp

"$DOTNET" "$CSC" @/tmp/scpatch-refs.rsp \
    -r:bin/dnlib.dll -out:bin/scpatch.dll -nologo \
    src/Program.cs src/Rules.cs src/SiteModel.cs

# Strong name is mandatory: a strong-named assembly cannot reference an unsigned one.
mcs -target:library -sdk:4.5 -keyfile:fixer/fixer.snk \
    -out:fixer/ScreenConnect.SqlFixer.dll fixer/Fixer.cs fixer/SqlRewrite.cs fixer/AssemblyInfo.cs

echo "built bin/scpatch.dll and fixer/ScreenConnect.SqlFixer.dll"
