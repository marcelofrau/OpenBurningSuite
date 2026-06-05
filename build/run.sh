#!/usr/bin/env bash
export PATH="$HOME/.dotnet:$PATH"
cd "$(dirname "$0")/.."
dotnet run --project OpenBurningSuite/OpenBurningSuite.csproj
