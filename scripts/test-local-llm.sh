#!/usr/bin/env bash
set -euo pipefail
source "$(dirname -- "${BASH_SOURCE[0]}")/common.sh"
model="${FINANSIST_TEST_MODEL:-$FINANSIST_ROOT/.cache/llm/Qwen3-1.7B-Q8_0.gguf}"
[[ -f "$model" ]] || { echo 'Для реального теста нужен официальный Qwen3-1.7B-Q8_0.gguf. Укажите FINANSIST_TEST_MODEL.' >&2; exit 1; }
printf '%s  %s\n' 061b54daade076b5d3362dac252678d17da8c68f07560be70818cace6590cb1a "$model" | sha256sum -c -
cmake -S native/finansist-llm -B .cache/llm/build-host -DLLAMA_SOURCE="$FINANSIST_ROOT/.cache/llm/llama.cpp" -DCMAKE_BUILD_TYPE=Release
cmake --build .cache/llm/build-host --target finansist_llm -j4
dotnet restore tools/Finansist.LlmSmoke --source .cache/empty-source -p:NuGetAudit=false "-p:RestoreAdditionalProjectFallbackFolders=$HOME/.nuget/packages"
dotnet build tools/Finansist.LlmSmoke --no-restore -m:1 -p:UseSharedCompilation=false -warnaserror
dotnet tools/Finansist.LlmSmoke/bin/Debug/net10.0/Finansist.LlmSmoke.dll "$FINANSIST_ROOT/.cache/llm/build-host/libfinansist_llm.so" "$model"
