#!/usr/bin/env bash
# Adapted from DomainNotice (MIT), https://github.com/BarPos/DomainNotice
# Builds the Homepage server release tarballs:
#   artifacts/Homepage.Server-<version>-linux-x64.tar.gz
#   artifacts/Homepage.Server-<version>-linux-arm64.tar.gz
#   artifacts/install.sh
#   artifacts/SHA256SUMS.txt   (tarballs and install.sh)
#
# Each tarball holds the self-contained server at its root (no top-level folder, files owned by root) plus
# deploy/linux/ (install.sh, update.sh, backup.sh, systemd units). update.sh unpacks it into
# /opt/homepage/releases/<version>/.
#
# Usage: build/build-server.sh [--arch x64|arm64]... [--output DIR] [--sums-only]
# Works on Linux and macOS (cross-publish). Needs the .NET SDK from global.json.
set -Eeuo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="$ROOT/artifacts"
ARCHES=()
SUMS_ONLY=0

usage() { sed -n '3,14p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; }
die() { echo "build-server.sh: $*" >&2; exit 1; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --arch) [[ $# -ge 2 ]] || die "--arch needs a value"; ARCHES+=("$2"); shift 2 ;;
        --arch=*) ARCHES+=("${1#*=}"); shift ;;
        --output) [[ $# -ge 2 ]] || die "--output needs a value"; OUT="$2"; shift 2 ;;
        --output=*) OUT="${1#*=}"; shift ;;
        --sums-only) SUMS_ONLY=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) die "unknown argument: $1 (see --help)" ;;
    esac
done
[[ ${#ARCHES[@]} -gt 0 ]] || ARCHES=(x64 arm64)
for a in "${ARCHES[@]}"; do [[ "$a" == x64 || "$a" == arm64 ]] || die "unsupported arch '$a' (x64 or arm64)"; done
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"

VERSION="$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props" | head -n1)"
[[ "$VERSION" =~ ^[0-9]{1,5}\.[0-9]{1,5}\.[0-9]{1,5}$ ]] || die "Directory.Build.props <Version> '$VERSION' is not a plain x.y.z"
APP_VERSION="$(sed -n 's:.*const string Current = "\([^"]*\)".*:\1:p' "$ROOT/Homepage.Shared/AppVersion.cs" | head -n1)"
[[ "$APP_VERSION" == "$VERSION" ]] || die "AppVersion.Current ($APP_VERSION) differs from Directory.Build.props ($VERSION)"

if command -v sha256sum >/dev/null 2>&1; then SHA256=(sha256sum); else SHA256=(shasum -a 256); fi

# Reproducible-ish, portable tar: root-owned entries, no macOS metadata (AppleDouble, xattrs).
if tar --version 2>/dev/null | grep -q 'GNU tar'; then
    TAR_CREATE=(tar --owner=0 --group=0 --numeric-owner --sort=name -czf)
else
    export COPYFILE_DISABLE=1
    TAR_CREATE=(tar --uid 0 --gid 0 --uname root --gname root --no-xattrs --no-mac-metadata --no-acls -czf)
fi

DEPLOY_FILES=(install.sh update.sh backup.sh homepage.service homepage-update.path homepage-update.service)
for f in "${DEPLOY_FILES[@]}"; do [[ -f "$ROOT/deploy/linux/$f" ]] || die "missing deploy/linux/$f"; done

if [[ $SUMS_ONLY -eq 0 ]]; then
    for arch in "${ARCHES[@]}"; do
        rid="linux-$arch"
        name="Homepage.Server-$VERSION-$rid.tar.gz"
        pub="$OUT/publish/$rid"
        echo "==> Publishing $rid ($VERSION)"
        rm -rf "$pub"
        dotnet publish "$ROOT/Homepage.Server/Homepage.Server.csproj" \
            -c Release -r "$rid" --self-contained true \
            -p:DebugType=None -p:DebugSymbols=false -p:Version="$VERSION" \
            -o "$pub" --nologo
        [[ -f "$pub/Homepage.Server" ]] || die "publish output has no Homepage.Server executable"
        rm -f "$pub/appsettings.Development.json"

        mkdir -p "$pub/deploy/linux"
        for f in "${DEPLOY_FILES[@]}"; do cp "$ROOT/deploy/linux/$f" "$pub/deploy/linux/$f"; done

        # Plain files and folders only, nobody but the owner may write.
        if find "$pub" ! -type f ! -type d | grep -q .; then die "publish output contains links or special files"; fi
        find "$pub" -type d -exec chmod 755 {} +
        find "$pub" -type f -exec chmod 644 {} +
        chmod 755 "$pub/Homepage.Server" "$pub/deploy/linux/install.sh" "$pub/deploy/linux/update.sh" "$pub/deploy/linux/backup.sh"
        find "$pub" -type f -name '*.so' -exec chmod 755 {} +
        [[ -f "$pub/createdump" ]] && chmod 755 "$pub/createdump"

        echo "==> Packing $name"
        rm -f "$OUT/$name"
        (cd "$pub" && "${TAR_CREATE[@]}" "$OUT/$name" .)
    done
    cp "$ROOT/deploy/linux/install.sh" "$OUT/install.sh"
    chmod 755 "$OUT/install.sh"
fi

echo "==> Writing SHA256SUMS.txt"
(
    cd "$OUT"
    files=()
    for f in "Homepage.Server-$VERSION-linux-x64.tar.gz" "Homepage.Server-$VERSION-linux-arm64.tar.gz" install.sh; do
        [[ -f "$f" ]] && files+=("$f")
    done
    [[ ${#files[@]} -gt 0 ]] || die "nothing to checksum in $OUT"
    "${SHA256[@]}" "${files[@]}" > SHA256SUMS.txt
    cat SHA256SUMS.txt
)
echo "==> Done: $OUT"
