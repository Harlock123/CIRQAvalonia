#!/usr/bin/env bash
#
# Builds CirqAvalonia as a self-contained single-file executable for every supported
# platform, then archives each one ready to attach to a GitHub release.
#
#   ./scripts/publish-all.sh              # version 0.1.0
#   ./scripts/publish-all.sh 1.2.0        # explicit version
#   ./scripts/publish-all.sh 1.2.0 win-x64 linux-x64    # only those targets
#
# Output lands in dist/ : one archive per platform plus SHA256SUMS.
#
# Self-contained means the .NET runtime travels inside the executable, so there is
# nothing for a user to install first. Cross-building every target from one machine
# works because the runtime packs come from NuGet.

set -euo pipefail

VERSION="${1:-0.1.0}"
shift || true

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/src/Cirq.UI/Cirq.UI.csproj"
DIST="$ROOT/dist"
STAGE="$DIST/.stage"

ALL_RIDS=(win-x64 win-x86 win-arm64 osx-x64 osx-arm64 linux-x64 linux-arm64)
RIDS=("$@")
[ ${#RIDS[@]} -eq 0 ] && RIDS=("${ALL_RIDS[@]}")

# Friendly names for the release page, so a user does not have to know what a RID is.
describe() {
  case "$1" in
    win-x64)     echo "Windows (64-bit, Intel/AMD)" ;;
    win-x86)     echo "Windows (32-bit)" ;;
    win-arm64)   echo "Windows (ARM64)" ;;
    osx-x64)     echo "macOS (Intel)" ;;
    osx-arm64)   echo "macOS (Apple Silicon)" ;;
    linux-x64)   echo "Linux (64-bit, Intel/AMD)" ;;
    linux-arm64) echo "Linux (ARM64)" ;;
    *)           echo "$1" ;;
  esac
}

rm -rf "$DIST"
mkdir -p "$STAGE"

echo "CirqAvalonia $VERSION — publishing ${#RIDS[@]} target(s)"
echo

failed=()

for rid in "${RIDS[@]}"; do
  printf '  %-12s %s\n' "$rid" "$(describe "$rid")"

  out="$STAGE/$rid"
  log="$STAGE/$rid.log"

  # PublishTrimmed is deliberately OFF. The properties inspector and the .cirq
  # serializer both discover component parameters by reflection
  # (ComponentReflection.EditableProperties), so a trimmer would strip properties
  # that nothing appears to reference and break both at runtime rather than at build
  # time. Size is not worth a silent failure.
  #
  # ReadyToRun is also off: it needs a cross-compiler matching the target
  # architecture, which would stop this script running anywhere but the host's own
  # platform. Compression claws back more than R2R costs in download size.
  if ! dotnet publish "$PROJECT" \
      --configuration Release \
      --runtime "$rid" \
      --self-contained true \
      --output "$out" \
      --nologo \
      --verbosity quiet \
      -p:PublishSingleFile=true \
      -p:IncludeNativeLibrariesForSelfExtract=true \
      -p:EnableCompressionInSingleFile=true \
      -p:PublishTrimmed=false \
      -p:PublishReadyToRun=false \
      -p:DebugType=none \
      -p:DebugSymbols=false \
      -p:SatelliteResourceLanguages=en \
      -p:Version="$VERSION" \
      -p:InformationalVersion="$VERSION" \
      > "$log" 2>&1; then
    echo "      FAILED — see $log"
    tail -n 12 "$log" | sed 's/^/      /'
    failed+=("$rid")
    continue
  fi

  # Single-file publish still emits the odd loose file; the executable is what ships.
  name="cirqavalonia-$VERSION-$rid"
  pkg="$STAGE/$name"
  mkdir -p "$pkg"

  if [[ "$rid" == win-* ]]; then
    cp "$out/Cirq.UI.exe" "$pkg/CirqAvalonia.exe"
  else
    cp "$out/Cirq.UI" "$pkg/CirqAvalonia"
    chmod +x "$pkg/CirqAvalonia"
  fi

  cp "$ROOT/README.md" "$pkg/"
  cp "$ROOT/docs/USER_GUIDE.md" "$pkg/" 2>/dev/null || true

  if [[ "$rid" == osx-* ]]; then
    # Unsigned builds are quarantined by Gatekeeper on first run; tell the user how
    # to clear it rather than letting them hit "app is damaged" with no explanation.
    cat > "$pkg/MACOS-FIRST-RUN.txt" <<'EOF'
This build is not code-signed or notarized, so macOS quarantines it on download.

To run it:

    xattr -d com.apple.quarantine CirqAvalonia
    ./CirqAvalonia

Or open Finder, right-click CirqAvalonia, choose Open, and confirm the prompt.
EOF
  fi

  ( cd "$STAGE"
    if [[ "$rid" == win-* ]]; then
      # zip is not installed everywhere; python3 ships a zipfile module that is, and
      # it preserves the executable bit that Windows ignores anyway.
      if command -v zip >/dev/null 2>&1; then
        zip -qr "$DIST/$name.zip" "$name"
      else
        python3 -c 'import shutil,sys; shutil.make_archive(sys.argv[1], "zip", ".", sys.argv[2])' \
          "$DIST/$name" "$name"
      fi
    else
      tar -czf "$DIST/$name.tar.gz" "$name"
    fi )
done

# One checksum file covering every archive, so a download can be verified.
( cd "$DIST" && sha256sum ./*.zip ./*.tar.gz 2>/dev/null | sed 's|\./||' > SHA256SUMS ) || true

rm -rf "$STAGE"

echo
printf '%-46s %s\n' "ARCHIVE" "SIZE"
printf '%-46s %s\n' "----------------------------------------------" "------"
for f in "$DIST"/*.zip "$DIST"/*.tar.gz; do
  [ -e "$f" ] || continue
  printf '%-46s %s\n' "$(basename "$f")" "$(du -h "$f" | cut -f1)"
done

echo
if [ ${#failed[@]} -gt 0 ]; then
  echo "FAILED: ${failed[*]}"
  exit 1
fi
echo "All ${#RIDS[@]} target(s) published to dist/"
