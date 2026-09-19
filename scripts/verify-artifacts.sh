#!/usr/bin/env bash
#
# Checks that each archive in dist/ really contains an executable for the platform its
# name claims. A cross-build that silently produces the wrong architecture still exits 0
# from `dotnet publish`, so the only way to know is to look at the binary.
#
#   ./scripts/verify-artifacts.sh            # everything in dist/
#   ./scripts/verify-artifacts.sh dist       # explicit directory
#
# Exits non-zero if any archive is missing, empty, or holds the wrong binary.

set -uo pipefail

DIST="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/dist}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

KNOWN_RIDS=(win-x64 win-x86 win-arm64 osx-x64 osx-arm64 linux-x64 linux-arm64)

# Each RID, and the two strings `file` must report for a correct binary.
expect_for() {
  case "$1" in
    win-x64)     echo "PE32+|x86-64" ;;
    win-x86)     echo "PE32|Intel i386" ;;
    win-arm64)   echo "PE32+|ARM64" ;;
    osx-x64)     echo "Mach-O|x86_64" ;;
    osx-arm64)   echo "Mach-O|arm64" ;;
    linux-x64)   echo "ELF|x86-64" ;;
    linux-arm64) echo "ELF|aarch64" ;;
    *)           echo "" ;;
  esac
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

  # Match the platform as a known suffix rather than parsing out the version. A
  # pre-release version contains hyphens of its own (0.1.1-rc.1), so splitting the
  # name on "-" mistook part of the version for the platform.
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

  described="$(file -b "$binary")"
  want_fmt="${expected%%|*}"
  want_arch="${expected##*|}"

  if [[ "$described" != *"$want_fmt"* || "$described" != *"$want_arch"* ]]; then
    printf '%-44s %-9s %s\n' "$base" "${human}MB" "FAIL  expected $want_fmt/$want_arch"
    echo "      got: $described"
    failures=$((failures + 1))
    continue
  fi

  # Windows builds must be GUI subsystem, or every launch pops a console window too.
  if [[ "$rid" == win-* && "$described" != *"(GUI)"* ]]; then
    printf '%-44s %-9s %s\n' "$base" "${human}MB" "FAIL  not a GUI subsystem binary"
    failures=$((failures + 1))
    continue
  fi

  printf '%-44s %-9s %s\n' "$base" "${human}MB" "ok    $want_fmt $want_arch"
done

echo
if [ "$failures" -gt 0 ]; then
  echo "$failures archive(s) failed verification"
  exit 1
fi
echo "${#archives[@]} archive(s) verified"
