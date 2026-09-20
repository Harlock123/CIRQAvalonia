#!/usr/bin/env bash
#
# Renders docs/USER_GUIDE.md to a printable PDF, and leaves the intermediate HTML beside it.
#
#   ./scripts/build-guide.sh                 # dist/CirqAvalonia-User-Guide.pdf
#   ./scripts/build-guide.sh 0.28.0          # with a version on the title page
#   PAPER=Letter ./scripts/build-guide.sh    # US paper instead of A4
#
# The Markdown stays the single source: this reads it rather than keeping a second copy, so the
# PDF cannot drift from the guide people read on GitHub.
#
# Two steps. A small .NET tool turns the Markdown into one self-contained HTML file with a title
# page, a contents page and a stylesheet written for paper; then a headless browser prints that to
# PDF. The browser is doing the hard part — pagination, widow and orphan control, font shaping for
# the Greek and the box-drawing characters, and keeping a table row off a page boundary — which is
# a great deal of machinery to get for the price of a command line.

set -euo pipefail

VERSION="${1:-}"
PAPER="${PAPER:-A4}"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GUIDE="$ROOT/docs/USER_GUIDE.md"
DIST="$ROOT/dist"
NAME="CirqAvalonia-User-Guide"

# The HTML is written next to the Markdown so its relative image paths resolve without rewriting
# them, then moved out. A browser resolves src="images/01-overview.png" against the file it loaded.
WORK="$ROOT/docs/.guide.html"

[[ -f "$GUIDE" ]] || { echo "error: no guide at $GUIDE" >&2; exit 1; }

# Whichever of the browser's several names this machine uses.
BROWSER=""
for candidate in "${CHROME:-}" chromium chromium-browser google-chrome google-chrome-stable; do
    [[ -n "$candidate" ]] && command -v "$candidate" >/dev/null 2>&1 && { BROWSER="$candidate"; break; }
done

if [[ -z "$BROWSER" ]]; then
    echo "error: no Chromium or Chrome on PATH — set CHROME=/path/to/browser" >&2
    exit 1
fi

mkdir -p "$DIST"

echo "==> HTML"
dotnet run --project "$ROOT/tools/Cirq.Docs" -c Release -v quiet -- \
    --in "$GUIDE" \
    --out "$WORK" \
    --title "CirqAvalonia User Guide" \
    --version "$VERSION" \
    --paper "$PAPER"

echo "==> PDF ($PAPER, $BROWSER)"

# --no-pdf-header-footer: the default footer prints the file:// URL of the temporary HTML across
# the bottom of every page, which is both ugly and wrong by the time anybody reads it.
"$BROWSER" \
    --headless \
    --disable-gpu \
    --no-sandbox \
    --no-pdf-header-footer \
    --generate-pdf-document-outline \
    --virtual-time-budget=20000 \
    --print-to-pdf="$DIST/$NAME.pdf" \
    "$WORK" 2>&1 | grep -viE "^\[|devtools|font|dbus" || true

mv -f "$WORK" "$DIST/$NAME.html"

[[ -s "$DIST/$NAME.pdf" ]] || { echo "error: the browser produced no PDF" >&2; exit 1; }

# The quiet failure this guards against is a contents page that came out empty — the guide still
# prints, looks fine on the first page, and has lost its way in. Compare what the Markdown offers
# against what reached the HTML.
expected=$(grep -cE '^#{2,3} ' "$GUIDE" || true)
actual=$(grep -c '<li class="l' "$DIST/$NAME.html" || true)

if [[ "$actual" -lt "$expected" ]]; then
    echo "error: contents has $actual entries for $expected headings" >&2
    exit 1
fi

size=$(du -h "$DIST/$NAME.pdf" | cut -f1)

echo
echo "  $DIST/$NAME.pdf   ($size, $actual contents entries)"
echo "  $DIST/$NAME.html  (intermediate; its images are relative to docs/)"
