#!/usr/bin/env bash
set -euo pipefail
FINANSIST_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$FINANSIST_ROOT"
export DOTNET_CLI_HOME="$FINANSIST_ROOT/.cache/dotnet"
export NUGET_PACKAGES="$FINANSIST_ROOT/.cache/nuget"
export NUGET_HTTP_CACHE_PATH="$FINANSIST_ROOT/.cache/http"
export DOTNET_CLI_USE_MSBUILD_SERVER=0
export DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=true
mkdir -p "$NUGET_PACKAGES" "$NUGET_HTTP_CACHE_PATH" "$FINANSIST_ROOT/artifacts"
restore_args=(-m:1 --locked-mode)
if [[ -d "$HOME/.nuget/packages" ]]; then
    restore_args+=("-p:RestoreAdditionalProjectFallbackFolders=$HOME/.nuget/packages")
fi
if [[ "${1:-}" == "--offline" ]]; then
    mkdir -p "$FINANSIST_ROOT/.cache/empty-source"
    restore_args+=(--source "$FINANSIST_ROOT/.cache/empty-source" -p:NuGetAudit=false)
fi
