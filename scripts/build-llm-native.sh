#!/usr/bin/env bash
set -euo pipefail
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"
revision=620d2225cc436ab0b001e3200e590e3b286ef9ab
archive_sha=1d4a987d9bd048f6e93107e838026438df70fbde237fc0c69c63d600512b4bd9
cache="$root/.cache/llm"
mkdir -p "$cache"
if [[ ! -f "$cache/llama.cpp/include/llama.h" ]]; then
    if [[ " ${*} " == *" --offline "* ]]; then echo 'Нет исходников llama.cpp в кэше. Запустите ./scripts/build-llm-native.sh с доступом в интернет.' >&2; exit 1; fi
    curl --fail --location --retry 2 "https://codeload.github.com/ggml-org/llama.cpp/tar.gz/$revision" -o "$cache/llama-source.tar.gz"
    printf '%s  %s\n' "$archive_sha" "$cache/llama-source.tar.gz" | sha256sum -c -
    mkdir -p "$cache/llama.cpp"
    tar -xzf "$cache/llama-source.tar.gz" --strip-components=1 -C "$cache/llama.cpp"
fi
ndk="${FINANSIST_NDK:-$root/.cache/android-sdk/ndk/27.3.13750724}"
if [[ ! -f "$ndk/build/cmake/android.toolchain.cmake" ]]; then
    echo 'Нужен NDK 27.3.13750724. Укажите FINANSIST_NDK или установите в .cache/android-sdk по docs/LLM-VALIDATION.md.' >&2; exit 1
fi
for abi in arm64-v8a x86_64; do
    case "$abi" in arm64-v8a) build=build-arm64;; *) build=build-x64;; esac
    cmake -S native/finansist-llm -B "$cache/$build" -DLLAMA_SOURCE="$cache/llama.cpp" \
      -DCMAKE_TOOLCHAIN_FILE="$ndk/build/cmake/android.toolchain.cmake" -DANDROID_ABI="$abi" \
      -DANDROID_PLATFORM=android-28 -DANDROID_STL=c++_static -DCMAKE_BUILD_TYPE=Release \
      -DLLAMA_BUILD_COMMIT="$revision" -DGGML_BUILD_COMMIT="$revision"
    cmake --build "$cache/$build" --target finansist_llm -j4
    mkdir -p "$cache/android/$abi"
    cp "$cache/$build/libfinansist_llm.so" "$cache/android/$abi/"
    "$ndk/toolchains/llvm/prebuilt/linux-x86_64/bin/llvm-strip" --strip-unneeded "$cache/android/$abi/libfinansist_llm.so"
done
