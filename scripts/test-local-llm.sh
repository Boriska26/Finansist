#!/usr/bin/env bash
set -euo pipefail
source "$(dirname -- "${BASH_SOURCE[0]}")/common.sh"
profile="${FINANSIST_TEST_PROFILE:-qwen3-1.7b}"
profile_args=()
case "$profile" in
  gemma3) filename=gemma-3-4b-it-Q4_K_M.gguf; digest=04a43a22e8d2003deda5acc262f68ec1005fa76c735a9962a8c77042a74a7d19; profile_args=(--gemma-recipes) ;;
  qwen3-1.7b) filename=Qwen3-1.7B-Q8_0.gguf; digest=061b54daade076b5d3362dac252678d17da8c68f07560be70818cace6590cb1a ;;
  instruct-2507) filename=Qwen3-4B-Instruct-2507-Q4_K_M.gguf; digest=3605803b982cb64aead44f6c1b2ae36e3acdb41d8e46c8a94c6533bc4c67e597; profile_args=(--instruct-2507) ;;
  *) echo 'Неизвестный профиль модели.' >&2; exit 1 ;;
esac
model="${FINANSIST_TEST_MODEL:-$FINANSIST_ROOT/.cache/llm/$filename}"
[[ -f "$model" ]] || { echo "Для реального теста нужен $filename. Укажите FINANSIST_TEST_MODEL." >&2; exit 1; }
printf '%s  %s\n' "$digest" "$model" | sha256sum -c -
cmake -S native/finansist-llm -B .cache/llm/build-host -DLLAMA_SOURCE="$FINANSIST_ROOT/.cache/llm/llama.cpp" -DCMAKE_BUILD_TYPE=Release
cmake --build .cache/llm/build-host --target finansist_llm -j4
dotnet restore tools/Finansist.LlmSmoke --source .cache/empty-source -p:NuGetAudit=false "-p:RestoreAdditionalProjectFallbackFolders=$HOME/.nuget/packages"
dotnet build tools/Finansist.LlmSmoke --no-restore -m:1 -p:UseSharedCompilation=false -warnaserror
dotnet tools/Finansist.LlmSmoke/bin/Debug/net10.0/Finansist.LlmSmoke.dll "$FINANSIST_ROOT/.cache/llm/build-host/libfinansist_llm.so" "$model" "${profile_args[@]}"
