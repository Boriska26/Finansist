#!/usr/bin/env bash
set -euo pipefail
source "$(dirname -- "${BASH_SOURCE[0]}")/common.sh"
project=tests/Finansist.Core.Tests/Finansist.Core.Tests.csproj
dotnet restore "$project" "${restore_args[@]}"
dotnet build "$project" --no-restore -m:1 -p:UseSharedCompilation=false -warnaserror
if [[ " ${*} " == *" --in-process "* ]]; then
    dotnet tests/Finansist.Core.Tests/bin/Debug/net10.0/Finansist.Core.Tests.dll
else
    dotnet test "$project" --no-build --no-restore -m:1
fi
