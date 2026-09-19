#!/usr/bin/env bash
#
# Prints the CHANGELOG section for one version, so a release can carry notes the build
# pipeline could never infer for itself.
#
#   ./scripts/changelog-section.sh 0.2.0
#   ./scripts/changelog-section.sh 0.2.0 path/to/CHANGELOG.md
#
# Exits 0 with the section on stdout, or 1 with nothing on stdout when the version has no
# entry — the caller decides whether that is fatal.
#
# A heading is recognised in any of the shapes Keep a Changelog produces:
#
#   ## [0.2.0] - 2026-09-19
#   ## 0.2.0 — 2026-09-19
#   ## [v0.2.0]
#
# and the section runs to the next "## " heading or the end of the file.

set -uo pipefail

VERSION="${1:-}"
CHANGELOG="${2:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/CHANGELOG.md}"

if [ -z "$VERSION" ]; then
  echo "usage: $(basename "$0") <version> [changelog]" >&2
  exit 2
fi

if [ ! -f "$CHANGELOG" ]; then
  echo "no changelog at $CHANGELOG" >&2
  exit 1
fi

python3 - "$VERSION" "$CHANGELOG" <<'PY'
import re, sys

version, path = sys.argv[1], sys.argv[2]

with open(path, encoding='utf-8') as handle:
    lines = handle.read().splitlines()

heading = re.compile(r'^##\s+(.+?)\s*$')

def version_of(title):
    """The version a heading names, or None.

    The token is parsed out and compared whole rather than pattern-matched inside the line.
    Matching a pattern let 0.2.0 hit '## [0.2.0-rc.1] - 2026-09-01', because the trailing
    '-rc.1]...' was absorbed by the optional date group — which would have published release
    candidate notes on a final release.
    """
    # A date is separated by a dash with spaces around it; a pre-release suffix is not.
    token = re.split(r'\s+[-–—]\s+', title, maxsplit=1)[0].strip()
    token = token.strip('[]').strip()
    return token[1:] if token[:1] in ('v', 'V') else token

start = None
for i, line in enumerate(lines):
    match = heading.match(line.strip())
    if match and version_of(match.group(1)).casefold() == version.casefold():
        start = i + 1
        break

if start is None:
    sys.exit(1)

end = len(lines)
for i in range(start, len(lines)):
    if lines[i].startswith('## '):
        end = i
        break

section = lines[start:end]

# Trim the blank lines either side so the caller can concatenate without gaps piling up.
while section and not section[0].strip():
    section.pop(0)

# The link definitions Keep a Changelog puts at the foot of the file sit inside whichever
# section happens to be last, with no heading to stop at — drop them rather than printing
# a release note that trails off into reference links.
link = re.compile(r'^\[[^\]]+\]:\s')
while section and (not section[-1].strip() or link.match(section[-1].strip())):
    section.pop()

if not section:
    sys.exit(1)

print('\n'.join(section))
PY
