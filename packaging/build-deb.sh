#!/usr/bin/env bash
# Build a Debian/Ubuntu package from an existing publish tree.
#
# Usage: packaging/build-deb.sh PUBLISH_DIR VERSION [CLI_BINARY] [OUT_DIR]
#   PUBLISH_DIR  output of packaging/publish.sh (contains Kirin-Tool, fastboot/, loaders/, docs/)
#   VERSION      package version, e.g. 2.4.9
#   CLI_BINARY   optional self-contained KirinTool.Cli single-file binary to bundle
#   OUT_DIR      where the .deb is written (default: repo root)
#
# The package installs the app tree under /usr/lib/kirin-tool-linux (same layout as
# the Arch package) and only adds symlinks under /usr/bin, so the app keeps resolving
# its bundled fastboot/ and loaders/ relative to its own directory.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PUBLISH="${1:?usage: build-deb.sh PUBLISH_DIR VERSION [CLI_BINARY] [OUT_DIR]}"
VERSION="${2:?usage: build-deb.sh PUBLISH_DIR VERSION [CLI_BINARY] [OUT_DIR]}"
CLI_BINARY="${3:-}"
OUT_DIR="${4:-$ROOT}"

PKG=kirin-tool-linux
APP=/usr/lib/$PKG
ARCH=amd64

# ---- runtime dependencies ----
# dpkg alternatives ("a | b") accept only exact package names: there is no
# wildcard/soname-range syntax, so every ICU soname the app may meet has to be
# listed explicitly. ICU sonames grow with each distribution:
#   Ubuntu 20.04 = libicu66, 22.04/24.04 = libicu70/74, 26.04 = libicu78, ...
# The app is a self-contained .NET build but .NET still P/Invokes the *system*
# ICU, so keep this list newest-first, keep libicu66 for the glibc 2.31
# container builds, and end with libicu-dev as the catch-all that always exists
# in the archive. When a new distribution bumps the soname again, add one item
# here (and nothing else).
ICU_DEPENDS='libicu78 | libicu76 | libicu74 | libicu72 | libicu71 | libicu70 | libicu66 | libicu-dev'

# Ubuntu 24.04+ renamed the OpenSSL 3 runtime package for the 64-bit time_t
# transition (libssl3 -> libssl3t64), so both names have to be alternated;
# libssl1.1 keeps older releases (20.04 and earlier) installable.
SSL_DEPENDS='libssl3t64 | libssl3 | libssl1.1'

[ -d "$PUBLISH" ] || { echo "✗ publish dir not found: $PUBLISH" >&2; exit 1; }
[ -x "$PUBLISH/Kirin-Tool" ] || { echo "✗ $PUBLISH/Kirin-Tool is not executable (run packaging/publish.sh first)" >&2; exit 1; }
if [ -n "$CLI_BINARY" ] && [ ! -x "$CLI_BINARY" ]; then echo "✗ CLI binary not executable: $CLI_BINARY" >&2; exit 1; fi

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
SUITE="$STAGE/$PKG"
mkdir -p "$SUITE$APP" "$SUITE/usr/bin" "$SUITE/usr/share/applications" "$SUITE/usr/share/pixmaps" \
         "$SUITE/usr/lib/udev/rules.d" "$SUITE/usr/share/doc/$PKG" "$SUITE/usr/share/licenses/$PKG" \
         "$SUITE/DEBIAN"

# ---- payload ----
cp -a "$PUBLISH/." "$SUITE$APP/"
if [ -n "$CLI_BINARY" ]; then
  install -m755 "$CLI_BINARY" "$SUITE$APP/KirinTool.Cli"
fi
chmod 755 "$SUITE$APP/Kirin-Tool"
[ -x "$SUITE$APP/fastboot/fastboot" ] && chmod 755 "$SUITE$APP/fastboot/fastboot" || true

ln -s "$APP/Kirin-Tool" "$SUITE/usr/bin/kirin-tool"
ln -s "$APP/UsbUpdateProbe" "$SUITE/usr/bin/kirin-usb-update-probe"
ln -s "$APP/VcomOpenProbe" "$SUITE/usr/bin/kirin-vcom-open-probe"
# The CLI may either be passed in or already sit in the publish tree (release pipeline does the latter).
if [ -x "$SUITE$APP/KirinTool.Cli" ]; then ln -s "$APP/KirinTool.Cli" "$SUITE/usr/bin/kirin-tool-cli"; fi

# ---- freedesktop / udev integration ----
install -Dm644 "$ROOT/packaging/kirin-tool-appimage.desktop" "$SUITE/usr/share/applications/kirin-tool.desktop"
sed -i 's|^Exec=.*|Exec=kirin-tool|' "$SUITE/usr/share/applications/kirin-tool.desktop"
install -Dm644 "$ROOT/packaging/kirin-tool.png" "$SUITE/usr/share/pixmaps/kirin-tool.png"
# Debian/Ubuntu have no "adbusers" group (that is an Arch convention); the USB
# node rule grants GROUP="adbusers", so a non-logind session (ssh, container, CI)
# would get "permission denied". Rewrite only the USB rule to plugdev, which
# exists on every Debian/Ubuntu release; the tty rule already uses uucp.
install -Dm644 "$ROOT/packaging/51-kirin-tool-fastboot.rules" "$SUITE/usr/lib/udev/rules.d/51-kirin-tool-fastboot.rules"
sed -i 's/GROUP="adbusers"/GROUP="plugdev"/' "$SUITE/usr/lib/udev/rules.d/51-kirin-tool-fastboot.rules"

# ---- docs / licenses ----
[ -d "$SUITE$APP/licenses" ] && cp -a "$SUITE$APP/licenses/." "$SUITE/usr/share/licenses/$PKG/"
[ -d "$SUITE$APP/docs" ] && cp -a "$SUITE$APP/docs/." "$SUITE/usr/share/doc/$PKG/"
install -Dm644 "$ROOT/README.md" "$SUITE/usr/share/doc/$PKG/README.md"
install -Dm644 "$ROOT/packaging/51-kirin-tool-fastboot.rules" "$SUITE/usr/share/doc/$PKG/51-kirin-tool-fastboot.rules"

# ---- maintainer scripts ----
cat > "$SUITE/DEBIAN/postinst" <<'POSTINST'
#!/bin/sh
set -e
if [ -d /run/systemd/system ] && command -v udevadm >/dev/null 2>&1; then
  udevadm control --reload-rules || true
  udevadm trigger || true
fi
# The deb ships the rule with GROUP="plugdev" (see build-deb.sh); add the user
# to plugdev and uucp for non-desktop sessions. Keep the message non-fatal.
if ! getent group plugdev >/dev/null 2>&1; then
  echo "kirin-tool-linux: 提示：未发现 plugdev 组；非桌面会话需自行把 udev 规则里的 GROUP 改成有权限的组。" >&2
elif ! id -nG 2>/dev/null | grep -qw plugdev; then
  echo "kirin-tool-linux: 提示：当前用户不在 plugdev 组；无桌面会话时 fastboot 可能无权限（sudo usermod -aG plugdev,uucp \$USER 后重新登录）。" >&2
fi

# Runtime self-check (non-fatal). The app is a self-contained .NET build, but
# .NET still loads the *system* ICU for globalization. A missing ICU makes the
# GUI fail at startup on some distributions, so warn loudly -- and never fail
# the installation because of a diagnostic, hence the trailing "exit 0".
icu_missing=1
if ldconfig -p 2>/dev/null | grep -q 'libicuuc\.so'; then icu_missing=0; fi
if [ "$icu_missing" -ne 0 ]; then
  # printf (a shell builtin) instead of cat + heredoc: the diagnostic must not be
  # able to turn into a non-zero exit even on a stripped-down PATH.
  printf '%s\n' \
    'kirin-tool-linux: 警告：未检测到 ICU 运行库（libicuuc.so*）。' \
    '  本程序是 self-contained .NET 构建，但仍需要系统 ICU 才能正常启动（大小写/排序/区域化）。' \
    '  处置：' \
    '    1) sudo apt install libicu-dev   # 或发行版对应的 libicu<版本号> 包' \
    '    2) 若确实无法安装 ICU，可设置 DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 以不变模式运行' \
    '       （会失去本地化/排序正确性，仅作兜底）。' \
    '  安装本身已成功，此提示不影响安装结果。' >&2 2>/dev/null || true
fi
exit 0
POSTINST
chmod 755 "$SUITE/DEBIAN/postinst"

# ---- control ----
INSTALLED_SIZE="$(du -sk --exclude=DEBIAN "$SUITE" | awk '{print $1}')"
cat > "$SUITE/DEBIAN/control" <<EOF
Package: $PKG
Version: $VERSION
Section: utils
Priority: optional
Architecture: $ARCH
Installed-Size: $INSTALLED_SIZE
Maintainer: Qianhui Xu <xiaqinghuan@mail.ustc.edu.cn>
Homepage: https://github.com/xiaquwan1001/kirin-tool-linux-refresh
Description: Huawei Kirin flash / unlock / repair tool (GUI + headless CLI)
 Kirin Tool for Linux bundles a Huawei-parity fastboot, the loader set needed for
 VCOM/BootROM flashing, a GTK-free Avalonia GUI and a headless CLI/MCP entry point.
 It talks to Kirin 620/820/950/960/970/980/985/990/9000 devices over USB only and
 never phones home.
 .
 The bundled fastboot is required: Google platform-tools fastboot queries
 getvar partition-size, which Huawei temporary loaders do not implement.
 .
 Headless usage: kirin-tool-cli --help, or wire it as an MCP stdio server
 (kirin-tool-cli mcp).
Depends: libc6 (>= 2.31), libx11-6, libice6, libsm6, libfontconfig1, libfreetype6,
 libusb-1.0-0, zlib1g, $ICU_DEPENDS,
 $SSL_DEPENDS
Conflicts: kirin-tool-linux-unstable
Replaces: kirin-tool-linux-unstable
Recommends: android-tools-adb, usbutils
EOF

if [ ! -s "$SUITE/DEBIAN/control" ]; then echo "✗ control file empty" >&2; exit 1; fi

# ---- build ----
mkdir -p "$OUT_DIR"
DEB="$OUT_DIR/${PKG}_${VERSION}_${ARCH}.deb"
rm -f "$DEB"
# drop debug symbols if the publish tree carries any (publish.sh already asks for none)
find "$SUITE$APP" -name '*.pdb' -delete 2>/dev/null || true

# --root-owner-group keeps root:root ownership without fakeroot/sudo (dpkg >= 1.19).
# xz -9: the payload is mostly assemblies + firmware blobs, worth the extra minute.
if ! dpkg-deb -Zxz -z9 --build --root-owner-group "$SUITE" "$DEB" >/dev/null 2>"$STAGE/build.err"; then
  dpkg-deb --build --root-owner-group "$SUITE" "$DEB" >/dev/null || { cat "$STAGE/build.err" >&2; echo "✗ dpkg-deb failed" >&2; exit 1; }
fi

# ---- self-check ----
# 清单先落到文件：直接 dpkg-deb --contents | grep -q 会提前关闭管道，把 tar 打成 SIGPIPE。
CONTENTS="$STAGE/contents.txt"
dpkg-deb --contents "$DEB" > "$CONTENTS"
for f in "$APP/Kirin-Tool" "usr/bin/kirin-tool" "usr/lib/udev/rules.d/51-kirin-tool-fastboot.rules" "usr/share/applications/kirin-tool.desktop"; do
  grep -qF "$f" "$CONTENTS" || { echo "✗ missing in deb: $f" >&2; exit 1; }
done
if [ -n "$CLI_BINARY" ]; then
  grep -qF "usr/bin/kirin-tool-cli" "$CONTENTS" || { echo "✗ missing CLI symlink in deb" >&2; exit 1; }
  grep -qF "$APP/KirinTool.Cli" "$CONTENTS" || { echo "✗ missing CLI binary in deb" >&2; exit 1; }
fi

echo "deb: $DEB"
echo "size: $(du -h "$DEB" | awk '{print $1}')  installed: ${INSTALLED_SIZE} KiB"
sha256sum "$DEB"
