#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
prefix="${PS2_EE_PREFIX:-mips64r5900el-ps2-elf-}"
assembler="${prefix}as"
linker="${prefix}ld"
objcopy="${prefix}objcopy"

for tool in "$assembler" "$linker" "$objcopy" cmp xxd sha256sum; do
    if ! command -v "$tool" >/dev/null 2>&1; then
        printf 'Required tool is unavailable: %s\n' "$tool" >&2
        exit 2
    fi
done

temporary="$(mktemp -d)"
trap 'rm -rf "$temporary"' EXIT

"$assembler" -EL -march=r5900 \
    -o "$temporary/guard.o" "$root/Core/Mips/shield-guard-quarter.s"
"$linker" -EL -nostdlib -Ttext=0x001481C8 -e shield_guard_quarter \
    -o "$temporary/guard.elf" "$temporary/guard.o"
"$objcopy" --only-section=.text -O binary \
    "$temporary/guard.elf" "$temporary/guard.bin"
xxd -r -p "$root/Tests/Fixtures/shield-guard-quarter.hex" "$temporary/expected.bin"

if ! cmp "$temporary/expected.bin" "$temporary/guard.bin"; then
    printf 'Shield guard machine words differ from the verified existing patch.\n' >&2
    printf 'Expected: '; sha256sum "$temporary/expected.bin"
    printf 'Actual:   '; sha256sum "$temporary/guard.bin"
    exit 1
fi

bytes="$(wc -c < "$temporary/guard.bin" | tr -d '[:space:]')"
if [[ "$bytes" != "56" ]]; then
    printf 'Unexpected cave size: %s bytes (expected 56).\n' "$bytes" >&2
    exit 1
fi

printf 'Shield guard cave: %s bytes match the existing rule.\n' "$bytes"
printf 'Toolchain: '; "$assembler" --version | head -n 1
sha256sum "$temporary/guard.bin"
