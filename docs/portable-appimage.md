# Portable AppImage

Build-image policy (2026-11): the release container stays `ubuntu:20.04` (glibc 2.31)
even though the *runner host* is now pinned to `ubuntu-24.04`. Aligning the container
itself to 24.04 would raise the floor to glibc 2.39 and invalidate every "runs on an old
distro" claim for the AppImage, the deb and the static CLI - that is a product support
decision, not CI hygiene, so it is not part of a workflow cleanup. 20.04 is EOL upstream,
but it is only a build sandbox here: nothing from that image runs on a user's machine, and
`packaging/check-portable-elf.py` still enforces the 2.31 floor. If the floor is ever
raised on purpose, do it in one commit: base image, `check-portable-elf.py`, this document,
the dependency floor in [fork-release.md](fork-release.md) and the release notes
(`debian:bullseye` is the only still-supported base that keeps 2.31).

Version 2.4.8 retains the portable.1 build baseline. Build in
Ubuntu 20.04 / glibc 2.31 with baseline x86-64 flags, not on the rolling-release
host. No x86-64-v3/AVX2 requirement is introduced by the native client/USB bridge.
.NET/Skia may select optional CPU optimizations at runtime. The self-contained
.NET 10 runtime is bundled (the official linux-x64 runtime only needs glibc 2.27, so the 2.31 baseline still holds); Linux desktop graphics/font libraries, ICU and OpenSSL
remain host dependencies. This does not mean literally every Linux system works.

Build example inside a Ubuntu 20.04 container with .NET 10 SDK installed:

```sh
apt-get update
apt-get install -y build-essential gcc-10 g++-10 cmake ninja-build libprotobuf-dev protobuf-compiler libbrotli-dev liblz4-dev libpcre2-dev libzstd-dev libgtest-dev pkg-config libusb-1.0-0-dev zlib1g-dev python3 curl binutils
./packaging/build-appimage.sh /work/new-publish 2.4.8
```

Supply APPIMAGETOOL and APPIMAGE_RUNTIME paths for offline assembly. The shared
publish script builds managed/native/probe components. AppImage assembly forces
fresh native rebuilds and checks every bundled ELF for glibc <= 2.31 and higher
x86 ISA notes. libusb 1.0.30 is built from SHA256-pinned official source with the
netlink backend, avoiding host libudev/systemd dependencies. Corresponding source
and LGPL license accompany the release. Native flags are -O2 -Wall -march=x86-64
-mtune=generic. Do not bundle libc/ld-linux or replace system glibc.

AppRun sets KIRIN_APPIMAGE=1, including when run from an extracted AppDir. Only
that mode shows the existing yellow notice style with title "Apply udev rules".
The notice appears when common vendor-wide Huawei USB or VCOM desktop permission
rules cannot be confirmed. It reads /etc, /run, /usr/local/lib, /usr/lib and /lib
udev rule directories, respecting filename precedence and /dev/null masks. Rules
can be split between files; it checks content, not just a known filename. Commented
rules, other vendors, product-specific rules and unreadable effective files do not
silently establish complete mode coverage. This conservative check is not a full
udev interpreter and does not promise that the current device has permission.

Copy commands writes a self-contained script to the clipboard. It uses uaccess
without distro-specific group names, installs /etc/udev/rules.d/51-kirin-tool-fastboot.rules
and reloads rules. The user executes it in a terminal and reconnects the phone.
The app never invokes sudo, writes host rules or touches a device for this check.
It rechecks when activated or when "Check again" is clicked. Existing complete
rules hide the notice. The installed Arch app does not show this AppImage notice.
Commands remain valid after the image closes; no transient mount path is copied.

Validation includes the actual copied clipboard text, shell syntax without
execution, rule precedence/masking, visible/hidden UI states, old-glibc/limited-CPU
launch checks and packaged UI regression tests. No new phone protocol validation
is implied. Stable's existing flashing/USB validation limitations remain.

The native fastboot engine uses pinned android-tools 35.0.2 AOSP sources plus
our Huawei extensions, built with GCC 10 in this same glibc 2.31 container.
C++/GCC runtime support is linked statically. Formatting helpers and their
configuration sit beside fastboot. The complete original source archive,
modifications, build instructions and dependency notices accompany the bundle
in sources/ and licenses/, including GPL filesystem-helper sources.
