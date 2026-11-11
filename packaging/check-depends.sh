#!/usr/bin/env bash
# Answer "will this .deb install on THIS host?" without touching the system.
#
# Usage: packaging/check-depends.sh PATH/TO/kirin-tool-linux_<ver>_amd64.deb
#
# Why: the deb's Depends names are distribution-specific (libicu<soname> goes
# 66 -> 70 -> 74 -> 78 across Ubuntu releases, libssl3 became libssl3t64 in
# 24.04+). A package that builds fine can still be uninstallable on a newer
# release, so resolve it against the local archive before shipping or after
# downloading a release asset. apt-get -s needs no root and installs nothing.
set -euo pipefail

DEB="${1:?usage: check-depends.sh <deb>}"
[ -f "$DEB" ] || { echo "✗ not a file: $DEB" >&2; exit 2; }
DEB="$(cd "$(dirname "$DEB")" && pwd)/$(basename "$DEB")"

. /etc/os-release
echo "host: ${PRETTY_NAME:-unknown}"
echo "--- control"
dpkg-deb -f "$DEB" Package Version Architecture Depends | sed 's/^/  /'

out="$(mktemp)"
trap 'rm -f "$out"' EXIT

set +e
apt-get -s --no-install-recommends install "$DEB" >"$out" 2>&1
rc=$?
set -e

if [ "$rc" -ne 0 ] || grep -qiE 'unmet dependencies|not installable|Unable to satisfy|未满足的依赖关系|无法安装它|but none of the choices' "$out"; then
  echo "--- FAIL: Depends are not satisfiable on this host (apt rc=$rc)"
  grep -iE 'dependency|依赖|libicu|libssl|not installable|无法安装' "$out" | head -20 | sed 's/^/  /'
  echo
  echo "Remediation:"
  echo "  * if a libicu<NN> is missing, add that soname to ICU_DEPENDS in packaging/build-deb.sh;"
  echo "  * if libssl3 is missing but libssl3t64 exists, the alternatives list already covers it;"
  echo "  * to run anyway (no ICU, culture-invariant): DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1"
  exit 1
fi

echo "--- OK: apt simulated the install without unmet dependencies"
grep -E '^(Inst|Conf) ' "$out" | sed 's/^/  /' || true
