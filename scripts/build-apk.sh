#!/usr/bin/env bash
set -euo pipefail
source "$(dirname -- "${BASH_SOURCE[0]}")/common.sh"
project=src/Finansist.App/Finansist.App.csproj
dotnet restore "$project" "${restore_args[@]}"
sdk_args=()
if [[ -n "${ANDROID_HOME:-}" ]]; then sdk_args+=("-p:AndroidSdkDirectory=$ANDROID_HOME"); fi
if [[ -n "${JAVA_HOME:-}" ]]; then sdk_args+=("-p:JavaSdkDirectory=$JAVA_HOME"); fi
dotnet build "$project" --no-restore -c Debug -m:1 -p:UseSharedCompilation=false -warnaserror "${sdk_args[@]}"
cp src/Finansist.App/bin/Debug/net10.0-android/ru.finansist.personal-Signed.apk artifacts/Finansist-0.4.3.apk
sha256sum artifacts/Finansist-0.4.3.apk > artifacts/Finansist-0.4.3.apk.sha256
