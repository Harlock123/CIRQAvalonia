#!/usr/bin/env bash
#
# Checks that each archive in dist/ really contains an executable for the platform its
# name claims. A cross-build that silently targets the wrong architecture still exits 0
# from `dotnet publish`, so the only way to know is to look at the binary.
#
#   ./scripts/verify-artifacts.sh            # everything in dist/
#   ./scripts/verify-artifacts.sh dist       # explicit directory
#
# Exits non-zero if any archive is missing, empty, or holds the wrong binary.
#
# The architecture is read out of the executable's own header rather than from `file`.
# libmagic phrases its answer differently between versions — the same Windows ARM64
# binary is "ARM64" on Arch and "Aarch64" on Ubuntu — so matching its prose made this
# check pass or fail depending on which machine ran it.

set -uo pipefail

DIST="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/dist}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

KNOWN_RIDS=(win-x64 win-x86 win-arm64 osx-x64 osx-arm64 linux-x64 linux-arm64)

# What each RID's binary must report: "<format> <arch> <subsystem>".
expect_for() {
  case "$1" in
    win-x64)     echo "pe x64 gui" ;;
    win-x86)     echo "pe x86 gui" ;;
    win-arm64)   echo "pe arm64 gui" ;;
    osx-x64)     echo "macho x64 -" ;;
    osx-arm64)   echo "macho arm64 -" ;;
    linux-x64)   echo "elf x64 -" ;;
    linux-arm64) echo "elf arm64 -" ;;
    *)           echo "" ;;
  esac
}

# Reads the executable header and prints "<format> <arch> <subsystem>".
identify() {
  python3 - "$1" <<'PY'
import struct, sys

with open(sys.argv[1], 'rb') as handle:
    head = handle.read(4096)

def out(fmt, arch, subsystem='-'):
    print(fmt, arch, subsystem)
    raise SystemExit

if head[:2] == b'MZ':
    pe = struct.unpack_from('<I', head, 0x3C)[0]
    if head[pe:pe + 4] != b'PE\0\0':
        out('unknown', '-')
    machine = struct.unpack_from('<H', head, pe + 4)[0]
    # The COFF header is 20 bytes, so the optional header starts at pe+24. Subsystem
    # sits at +68 in both PE32 and PE32+ — the layouts differ earlier but realign.
    subsystem = struct.unpack_from('<H', head, pe + 24 + 68)[0]
    out(
        'pe',
        {0x8664: 'x64', 0x014C: 'x86', 0xAA64: 'arm64'}.get(machine, hex(machine)),
        {2: 'gui', 3: 'console'}.get(subsystem, str(subsystem)),
    )

if head[:4] == b'\x7fELF':
    machine = struct.unpack_from('<H', head, 18)[0]
    out('elf', {0x3E: 'x64', 0xB7: 'arm64'}.get(machine, hex(machine)))

if head[:4] in (b'\xcf\xfa\xed\xfe', b'\xce\xfa\xed\xfe'):
    cputype = struct.unpack_from('<I', head, 4)[0]
    out('macho', {0x01000007: 'x64', 0x0100000C: 'arm64'}.get(cputype, hex(cputype)))

out('unknown', '-')
PY
}

shopt -s nullglob
archives=("$DIST"/*.zip "$DIST"/*.tar.gz)

if [ ${#archives[@]} -eq 0 ]; then
  echo "No archives found in $DIST"
  exit 1
fi

printf '%-44s %-9s %s\n' "ARCHIVE" "BINARY" "CHECK"
printf '%-44s %-9s %s\n' "--------------------------------------------" "---------" "-----"

failures=0

for archive in "${archives[@]}"; do
  base="$(basename "$archive")"

  # Match the platform as a known suffix rather than parsing the version out of the
  # name: a pre-release version contains hyphens of its own (0.1.1-rc.1), so splitting
  # on "-" mistook part of the version for the platform.
  rid=""
  for candidate in "${KNOWN_RIDS[@]}"; do
    case "$base" in
      *"-$candidate".zip|*"-$candidate".tar.gz) rid="$candidate"; break ;;
    esac
  done

  if [ -z "$rid" ]; then
    printf '%-44s %-9s %s\n' "$base" "-" "FAIL  no recognised platform in the name"
    failures=$((failures + 1))
    continue
  fi

  expected="$(expect_for "$rid")"

  out="$WORK/$rid"
  rm -rf "$out"; mkdir -p "$out"

  if [[ "$archive" == *.zip ]]; then
    python3 -c 'import zipfile,sys; zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])' \
      "$archive" "$out" 2>/dev/null
  else
    tar -xzf "$archive" -C "$out" 2>/dev/null
  fi

  binary="$(find "$out" -type f -name 'CirqAvalonia*' ! -name '*.txt' | head -1)"
  if [ -z "$binary" ]; then
    printf '%-44s %-9s %s\n' "$base" "-" "FAIL  no executable inside"
    failures=$((failures + 1))
    continue
  fi

  size=$(stat -c %s "$binary" 2>/dev/null || stat -f %z "$binary")
  human=$(( size / 1048576 ))

  # A self-contained build carries the whole runtime. Anything small means the runtime
  # did not make it in, which would fail on a machine without .NET installed — exactly
  # the situation these builds exist for.
  if [ "$size" -lt 20000000 ]; then
    printf '%-44s %-9s %s\n' "$base" "${human}MB" "FAIL  too small to be self-contained"
    failures=$((failures + 1))
    continue
  fi

  actual="$(identify "$binary")"

  if [ "$actual" != "$expected" ]; then
    printf '%-44s %-9s %s\n' "$base" "${human}MB" "FAIL  expected '$expected', got '$actual'"
    failures=$((failures + 1))
    continue
  fi

  # The command-line tool ships in the same archive, and has to be for the same machine: a cirq
  # built for the wrong architecture is a cirq that will not start on the one it was downloaded to.
  cli="$(find "$out" -type f -name 'cirq' -o -type f -name 'cirq.exe' | head -1)"

  if [ -z "$cli" ]; then
    printf '%-44s %-9s %s\n' "$base" "${human}MB" "FAIL  no cirq command-line tool inside"
    failures=$((failures + 1))
    continue
  fi

  clikind="$(identify "$cli")"

  if [ "$clikind" != "$expected" ]; then
    printf '%-44s %-9s %s\n' "$base" "${human}MB" "FAIL  cirq is '$clikind', expected '$expected'"
    failures=$((failures + 1))
    continue
  fi

  printf '%-44s %-9s %s\n' "$base" "${human}MB" "ok    $actual"
done

echo
if [ "$failures" -gt 0 ]; then
  echo "$failures archive(s) failed verification"
  exit 1
fi
echo "${#archives[@]} archive(s) verified"
