#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$SCRIPT_DIR/skua"
SOURCE_PATH="$PROJECT_ROOT/src"
MAIN_SOURCE="$SOURCE_PATH/skua/Main.as"
OUTPUT_PATH="${OUTPUT_PATH:-$PROJECT_ROOT/bin/skua.swf}"

if [[ ! -f "$MAIN_SOURCE" ]]; then
    echo "AS3 entry point was not found: $MAIN_SOURCE" >&2
    exit 1
fi

MXMLC="${MXMLC_PATH:-}"
if [[ -z "$MXMLC" ]]; then
    MXMLC="$(command -v mxmlc || true)"
fi

if [[ -z "$MXMLC" && -n "${FLEX_HOME:-}" && -x "$FLEX_HOME/bin/mxmlc" ]]; then
    MXMLC="$FLEX_HOME/bin/mxmlc"
fi

if [[ -z "$MXMLC" || ! -x "$MXMLC" ]]; then
    echo "Flex mxmlc was not found. Add mxmlc to PATH, set FLEX_HOME, or set MXMLC_PATH." >&2
    exit 1
fi

if [[ -z "${FLEX_HOME:-}" ]]; then
    FLEX_BIN="$(cd -- "$(dirname -- "$MXMLC")" && pwd)"
    FLEX_HOME="$(cd -- "$FLEX_BIN/.." && pwd)"
fi

export FLEX_HOME
export PLAYERGLOBAL_HOME="${PLAYERGLOBAL_HOME:-$FLEX_HOME/frameworks/libs/player}"

mkdir -p "$(dirname -- "$OUTPUT_PATH")"

"$MXMLC" \
    -source-path "$SOURCE_PATH" \
    -default-size 958 550 \
    -output "$OUTPUT_PATH" \
    "$MAIN_SOURCE" \
    -target-player 28.0 \
    -optimize

if [[ ! -f "$OUTPUT_PATH" ]]; then
    echo "AS3 compilation did not produce the expected output: $OUTPUT_PATH" >&2
    exit 1
fi

printf 'AS3 game content built: %s (%s bytes)\n' "$OUTPUT_PATH" "$(wc -c < "$OUTPUT_PATH" | tr -d ' ')"
