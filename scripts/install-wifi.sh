#!/usr/bin/env bash
set -euo pipefail
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
usage() {
    cat <<'HELP'
Установка Финансиста через ADB по Wi-Fi (Linux).
  ./scripts/install-wifi.sh                       Интерактивное сопряжение и установка готового APK
  ./scripts/install-wifi.sh --device IP:PORT       Подключение уже сопряжённого телефона
  ./scripts/install-wifi.sh --pair IP:PAIR_PORT --device IP:CONNECT_PORT
  ./scripts/install-wifi.sh --build --offline     Сначала пересобрать APK из локального кэша
  ./scripts/install-wifi.sh --connect-only        Только подключить телефон для отладки в IDE
Код сопряжения вводится по запросу adb, не передаётся аргументом и не записывается скриптом.
Порты сопряжения и подключения различаются. Для Android 11+ включите «Беспроводная отладка».
HELP
}
fail() { printf '%s\n' "$*" >&2; exit 1; }
endpoint() {
    # Accept host:port and bracketed IPv6; reject whitespace and option-like inputs.
    [[ "$1" =~ ^([a-zA-Z0-9][a-zA-Z0-9.-]*|\[[a-fA-F0-9:]+\]):([0-9]{1,5})$ ]] || fail 'Нужен адрес IP:порт, показанный телефоном.'
    local port="${BASH_REMATCH[2]}"
    (( 10#$port >= 1 && 10#$port <= 65535 )) || fail 'Порт должен быть от 1 до 65535.'
}
device=''
pair=''
build=false
offline=false
connect_only=false
while (( $# )); do
    case "$1" in
        --device|--pair)
            (( $# >= 2 )) || fail "Не указан адрес после $1"
            endpoint "$2"
            if [[ "$1" == --device ]]; then device="$2"; else pair="$2"; fi
            shift 2 ;;
        --build) build=true; shift ;;
        --offline) offline=true; shift ;;
        --connect-only) connect_only=true; shift ;;
        --help|-h) usage; exit 0 ;;
        *) fail "Неизвестный аргумент: $1. Используйте --help." ;;
    esac
done
if [[ -n "${ADB:-}" ]]; then
    adb="$ADB"
elif [[ -x "${ANDROID_HOME:-}/platform-tools/adb" ]]; then
    adb="$ANDROID_HOME/platform-tools/adb"
elif [[ -x "${ANDROID_SDK_ROOT:-}/platform-tools/adb" ]]; then
    adb="$ANDROID_SDK_ROOT/platform-tools/adb"
elif command -v adb >/dev/null 2>&1; then
    adb="$(command -v adb)"
else
    fail 'ADB не найден. Укажите ANDROID_HOME или путь к adb в переменной ADB.'
fi
if ! "$adb" start-server; then
    fail 'Не удалось запустить ADB. Если сокеты запрещены песочницей, запустите этот скрипт в обычном терминале компьютера.'
fi
if [[ -z "$device" && -z "$pair" ]]; then
    printf '%s\n' 'Телефон и компьютер должны быть в одной Wi-Fi сети.'
    read -r -p 'Адрес из «Сопряжение с помощью кода» (Enter, если уже сопряжены): ' pair
fi
if [[ -n "$pair" ]]; then
    endpoint "$pair"
    "$adb" pair "$pair" || fail 'Сопряжение не выполнено. Откройте новый код на телефоне и повторите.'
fi
if [[ -z "$device" ]]; then
    read -r -p 'IP:порт с главного экрана «Беспроводная отладка» (не порт сопряжения): ' device
fi
endpoint "$device"
"$adb" connect "$device"
# adb connect may return zero even on a refused connection.
state="$("$adb" -s "$device" get-state 2>/dev/null || true)"
[[ "$state" == device ]] || fail 'Телефон не подключён. Проверьте основной порт, Wi-Fi и разрешение отладки на телефоне.'
if $connect_only; then
    printf '%s\n' 'Телефон подключён. Выберите его в IDE как цель Android-отладки.'
    exit 0
fi
if $build; then
    args=()
    if $offline; then args+=(--offline); fi
    "$root/scripts/build-apk.sh" "${args[@]}"
fi
apk="$root/artifacts/Finansist-0.4.4.apk"
[[ -f "$apk" ]] || fail 'APK не найден. Повторите команду с --build (и --offline, если нет сети NuGet).'
# Keep existing app data. Never uninstall on signing/version errors.
"$adb" -s "$device" install -r "$apk" || fail 'Не удалось установить APK. Данные приложения не удалялись; см. сообщение ADB выше.'
"$adb" -s "$device" shell am start -W -a android.intent.action.MAIN -c android.intent.category.LAUNCHER -p ru.finansist.personal
printf '%s\n' 'APK установлен. Команда запуска отправлена на телефон.'
