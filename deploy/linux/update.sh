#!/usr/bin/env bash
# Adapted from DomainNotice (MIT), https://github.com/BarPos/DomainNotice
# Homepage self-update.
#
# Started as root by homepage-update.service when the server writes
# /opt/homepage/data/updates/request.conf (watched by homepage-update.path).
# install.sh installs this script root-owned as /opt/homepage/bin/update.sh. It is never run from a package
# being installed, and nothing from the package runs as root: migrations run as the homepage user.
#
# Request (written by the server, key=value, parsed without eval; every value is validated):
#   format=1  run_id=<guid>  version=<x.y.z>  from_version=<x.y.z>  arch=x64|arm64
#   file=Homepage.Server-<version>-linux-<arch>.tar.gz  sha256=<64 hex>
# The package and SHA256SUMS.txt are in /opt/homepage/data/updates/<version>/.
# Result (read by the server on start): /opt/homepage/data/updates/result.conf, written by the service user
# (root never opens a path in the service user's folders for writing)
#   format=1  run_id  version  from_version  state=Succeeded|RolledBack|Failed  message  finished_at
# Database dump before the update: /opt/homepage/backups/pre-update/ (root:root 700, only root can write it)
#
# Needs /opt/homepage/config/update.env (root:root 600, written by install.sh), key=value lines:
#   PGHOST PGPORT PGDATABASE PGUSER PGPASSWORD PGSSLMODE  (for pg_dump / pg_restore)
#   HEALTH_URL (e.g. http://127.0.0.1:5080/healthz or https://127.0.0.1:443/healthz), optional PG_BIN (folder of pg_dump matching the server)
#   GITHUB_REPOSITORY (owner/name): the package must match the SHA256SUMS.txt that root downloads from that
#   repository's release v<version> itself. Files the service user staged are never the only proof: a compromised
#   server process could stage any program with matching hashes.
#
# Usage: update.sh            apply a pending request (what the systemd unit runs)
#        update.sh --self-test   run the built-in checks of the validation functions (no root needed)
set -Eeuo pipefail

HP_ROOT="${HP_ROOT:-/opt/homepage}"
HP_USER="${HP_USER:-homepage}"
HP_SERVICE="homepage.service"
HEALTH_TIMEOUT_SECONDS=120
KEEP_RELEASES=3
MAX_REQUEST_BYTES=4096
MAX_PACKAGE_BYTES=$((1024 * 1024 * 1024))
# Where releases come from when update.env does not say (installs made before update.env had GITHUB_REPOSITORY).
# The same value as in install.sh. This script is root-owned and only ever comes from a package install.sh verified.
HP_DEFAULT_REPOSITORY="BarPos/Homepage"

# ------------------------------------------------------------------------------------------------ functions
# All functions below are side-effect free unless noted, so tests can source this file (main runs only when the
# file is executed, not sourced).

hp_log() { printf 'update.sh: %s\n' "$*" >&2; }

# Plain x.y.z only (the same rule as AppVersion.IsPlainTriplet).
hp_is_plain_version() { [[ "${1-}" =~ ^[0-9]{1,5}\.[0-9]{1,5}\.[0-9]{1,5}$ ]]; }

# hp_version_cmp A B -> prints -1, 0 or 1. Both must be plain versions.
hp_version_cmp() {
    local a="$1" b="$2" i
    hp_is_plain_version "$a" && hp_is_plain_version "$b" || return 2
    local -a pa pb
    IFS=. read -r -a pa <<<"$a"
    IFS=. read -r -a pb <<<"$b"
    for i in 0 1 2; do
        if ((10#${pa[i]} > 10#${pb[i]})); then echo 1; return 0; fi
        if ((10#${pa[i]} < 10#${pb[i]})); then echo -1; return 0; fi
    done
    echo 0
}

# True when A is newer than B.
hp_version_gt() { local c; c="$(hp_version_cmp "$1" "$2")" || return 1; [[ "$c" == 1 ]]; }

hp_is_guid() { [[ "${1-}" =~ ^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$ ]]; }
hp_is_sha256() { [[ "${1-}" =~ ^[0-9a-f]{64}$ ]]; }
hp_is_arch() { [[ "${1-}" == x64 || "${1-}" == arm64 ]]; }

# owner/name of a GitHub repository.
hp_is_repo() { [[ "${1-}" =~ ^[A-Za-z0-9_.-]{1,100}/[A-Za-z0-9_.-]{1,100}$ && "${1-}" != *..* ]]; }

# TCP port 1-65535, no leading zeros.
hp_is_port() { [[ "${1-}" =~ ^[1-9][0-9]{0,4}$ ]] && ((10#$1 <= 65535)); }

# Addresses the site may bind to: loopback or wildcard only. /healthz answers only from loopback, so the health
# check must reach Kestrel on a loopback address (a site bound to one specific public address could never pass it).
hp_is_bind() { [[ "${1-}" == 127.0.0.1 || "${1-}" == 0.0.0.0 || "${1-}" == ::1 || "${1-}" == :: ]]; }

# hp_health_url SCHEME BIND PORT -> the loopback /healthz URL for a site bound to BIND:PORT.
hp_health_url() {
    local scheme="${1-}" bind="${2-}" port="${3-}" host
    [[ "$scheme" == http || "$scheme" == https ]] || return 1
    hp_is_port "$port" || return 1
    case "$bind" in
        127.0.0.1|0.0.0.0) host=127.0.0.1 ;;
        ::1|::) host='[::1]' ;;
        *) return 1 ;;
    esac
    printf '%s://%s:%s/healthz\n' "$scheme" "$host" "$port"
}

hp_machine_arch() {
    case "$(uname -m)" in
        x86_64|amd64) echo x64 ;;
        aarch64|arm64) echo arm64 ;;
        *) return 1 ;;
    esac
}

hp_tarball_name() { printf 'Homepage.Server-%s-linux-%s.tar.gz' "$1" "$2"; }

# hp_parse_request FILE: sets REQ_FORMAT REQ_RUN_ID REQ_VERSION REQ_FROM REQ_ARCH REQ_FILE REQ_SHA256.
# Unknown keys are ignored; nothing is evaluated. Returns 1 when a value is missing or invalid.
hp_parse_request() {
    local file="$1" key value
    REQ_FORMAT="" REQ_RUN_ID="" REQ_VERSION="" REQ_FROM="" REQ_ARCH="" REQ_FILE="" REQ_SHA256=""
    while IFS= read -r line || [[ -n "$line" ]]; do
        line="${line%$'\r'}"
        [[ -z "$line" || "$line" == \#* ]] && continue
        [[ "$line" == *=* ]] || continue
        key="${line%%=*}"
        value="${line#*=}"
        case "$key" in
            format) REQ_FORMAT="$value" ;;
            run_id) REQ_RUN_ID="$value" ;;
            version) REQ_VERSION="$value" ;;
            from_version) REQ_FROM="$value" ;;
            arch) REQ_ARCH="$value" ;;
            file) REQ_FILE="$value" ;;
            sha256) REQ_SHA256="$value" ;;
        esac
    done <"$file"
    [[ "$REQ_FORMAT" == 1 ]] || { HP_ERROR="unsupported request format"; return 1; }
    hp_is_guid "$REQ_RUN_ID" || { REQ_RUN_ID=""; HP_ERROR="invalid run_id"; return 1; }
    hp_is_plain_version "$REQ_VERSION" || { REQ_VERSION=""; HP_ERROR="version is not a plain x.y.z"; return 1; }
    [[ -z "$REQ_FROM" ]] || hp_is_plain_version "$REQ_FROM" || { REQ_FROM=""; HP_ERROR="invalid from_version"; return 1; }
    hp_is_arch "$REQ_ARCH" || { HP_ERROR="invalid arch"; return 1; }
    hp_is_sha256 "$REQ_SHA256" || { HP_ERROR="invalid sha256"; return 1; }
    # The file name is derived from version and arch; the request must agree with it.
    [[ "$REQ_FILE" == "$(hp_tarball_name "$REQ_VERSION" "$REQ_ARCH")" ]] || { HP_ERROR="unexpected file name"; return 1; }
    return 0
}

# hp_read_env_file FILE: reads key=value lines of update.env into variables (known keys only, no eval).
hp_read_env_file() {
    local file="$1" key value
    while IFS= read -r line || [[ -n "$line" ]]; do
        line="${line%$'\r'}"
        [[ -z "$line" || "$line" == \#* || "$line" != *=* ]] && continue
        key="${line%%=*}"
        value="${line#*=}"
        case "$key" in
            PGHOST|PGPORT|PGDATABASE|PGUSER|PGPASSWORD|PGSSLMODE) export "$key=$value" ;;
            HEALTH_URL) HEALTH_URL="$value" ;;
            PG_BIN) PG_BIN="$value" ;;
            GITHUB_REPOSITORY) GITHUB_REPOSITORY="$value" ;;
        esac
    done <"$file"
}

# hp_sha256 FILE -> lowercase hex
hp_sha256() {
    if command -v sha256sum >/dev/null 2>&1; then sha256sum -- "$1" | cut -d' ' -f1
    else shasum -a 256 -- "$1" | cut -d' ' -f1; fi
}

# hp_sums_lookup SUMSFILE NAME -> hash of NAME in a sha256sum-style file ("<hex>  name" or "<hex> *name").
hp_sums_lookup() {
    local sums="$1" name="$2" hash rest
    while IFS= read -r line || [[ -n "$line" ]]; do
        line="${line%$'\r'}"
        [[ "$line" =~ ^([0-9a-fA-F]{64})\ [\ \*](.+)$ ]] || continue
        hash="${BASH_REMATCH[1]}"
        rest="${BASH_REMATCH[2]}"
        rest="${rest#./}"
        if [[ "$rest" == "$name" ]]; then printf '%s\n' "$hash" | tr 'A-F' 'a-f'; return 0; fi
    done <"$sums"
    return 1
}

# hp_check_member_names: reads archive member names (tar -t output) on stdin.
# Rejects absolute paths, any ".." component, and empty names. A name with an embedded newline shows up as two
# lines; each line is checked, so such tricks fail safe.
hp_check_member_names() {
    local name n=0
    while IFS= read -r name || [[ -n "$name" ]]; do
        n=$((n + 1))
        if [[ -z "$name" ]]; then HP_ERROR="empty member name"; return 1; fi
        if [[ "$name" == /* ]]; then HP_ERROR="absolute path in package: $name"; return 1; fi
        case "/$name/" in
            */../*) HP_ERROR="'..' in package path: $name"; return 1 ;;
        esac
        if [[ "$name" == *\\* ]]; then HP_ERROR="backslash in package path: $name"; return 1; fi
    done
    [[ $n -gt 0 ]] || { HP_ERROR="empty package"; return 1; }
}

# hp_check_member_types: reads "tar -tv" output on stdin. Only regular files, folders and symbolic links
# (checked after unpacking) are allowed: no hard links, devices, FIFOs or sockets.
hp_check_member_types() {
    local line n=0
    while IFS= read -r line || [[ -n "$line" ]]; do
        n=$((n + 1))
        case "${line:0:1}" in
            -|d|l) ;;
            *) HP_ERROR="unsupported entry type in package: ${line:0:1}"; return 1 ;;
        esac
    done
    [[ $n -gt 0 ]] || { HP_ERROR="empty package"; return 1; }
}

# hp_check_member_modes: reads "tar -tv" output on stdin. Refuses setuid and setgid entries (a backup never needs
# them, and root would put them back as they are).
hp_check_member_modes() {
    local line perm
    while IFS= read -r line || [[ -n "$line" ]]; do
        perm="${line%% *}"
        case "${perm:3:1}${perm:6:1}" in
            *s*|*S*) HP_ERROR="setuid or setgid entry: ${line##* }"; return 1 ;;
        esac
    done
    return 0
}

# hp_normalize_relpath PATH: lexically resolves "." and ".." of a relative path. Prints the result (may be
# empty for the root itself). Fails when the path climbs above its root.
hp_normalize_relpath() {
    local path="$1" part out=() parts=()
    IFS=/ read -r -a parts <<<"$path"
    for part in "${parts[@]}"; do
        case "$part" in
            ''|.) ;;
            ..) [[ ${#out[@]} -gt 0 ]] || return 1; unset 'out[${#out[@]}-1]' ;;
            *) out+=("$part") ;;
        esac
    done
    local IFS=/
    printf '%s' "${out[*]}"
}

# hp_check_tree DIR: after unpacking. Only files, folders and symlinks; no hard-linked files; every symlink is
# relative and stays inside DIR: checked on the text of the target, and again with "realpath -m" against the real
# path of DIR where GNU realpath is available (defence in depth; macOS has no -m).
hp_check_tree() {
    local dir="$1" link rel target linkdir real="" resolved
    if [[ -n "$(find "$dir" ! -type f ! -type d ! -type l -print -quit)" ]]; then
        HP_ERROR="special files in package"; return 1
    fi
    if [[ -n "$(find "$dir" -type f -links +1 -print -quit)" ]]; then
        HP_ERROR="hard links in package"; return 1
    fi
    if realpath -m / >/dev/null 2>&1; then
        real="$(realpath -e -- "$dir")" || { HP_ERROR="cannot resolve $dir"; return 1; }
    fi
    while IFS= read -r -d '' link; do
        target="$(readlink "$link")"
        rel="${link#"$dir"/}"
        if [[ -z "$target" || "$target" == /* ]]; then HP_ERROR="link with absolute target: $rel"; return 1; fi
        linkdir="$(dirname "$rel")"
        [[ "$linkdir" == . ]] && linkdir=""
        if ! hp_normalize_relpath "${linkdir:+$linkdir/}$target" >/dev/null; then
            HP_ERROR="link pointing outside the package: $rel"; return 1
        fi
        if [[ -n "$real" ]]; then
            resolved="$(realpath -m -- "$link" 2>/dev/null)" || { HP_ERROR="cannot resolve link: $rel"; return 1; }
            [[ "$resolved" == "$real" || "$resolved" == "$real"/* ]] || { HP_ERROR="link pointing outside the package: $rel"; return 1; }
        fi
    done < <(find "$dir" -type l -print0)
    return 0
}

# hp_safe_unpack TARBALL DEST: validates and unpacks into DEST (must not exist). As root, files become root-owned.
hp_safe_unpack() {
    local tarball="$1" dest="$2" listing
    [[ ! -e "$dest" && ! -L "$dest" ]] || { HP_ERROR="$dest already exists"; return 1; }
    listing="$(tar -tzf "$tarball")" || { HP_ERROR="cannot read package"; return 1; }
    hp_check_member_names <<<"$listing" || return 1
    listing="$(tar -tvzf "$tarball")" || { HP_ERROR="cannot read package"; return 1; }
    hp_check_member_types <<<"$listing" || return 1
    mkdir -m 700 -- "$dest" || { HP_ERROR="cannot create $dest"; return 1; }
    if ! tar --no-same-owner --no-same-permissions -xzf "$tarball" -C "$dest"; then
        HP_ERROR="unpacking failed"; return 1
    fi
    hp_check_tree "$dest" || return 1
    if [[ $EUID -eq 0 ]]; then
        chown -hR root:root -- "$dest" || { HP_ERROR="chown failed"; return 1; }
    fi
    # Symlinks were checked above; chmod -R does not follow them below the top folder.
    chmod -R ug-s,u+rwX,go+rX,go-w "$dest" || { HP_ERROR="chmod failed"; return 1; }
    chmod 755 "$dest"
}

# hp_releases_to_prune RELEASES_DIR KEEP CURRENT [EXTRA...]: prints release folders to delete. Keeps the KEEP
# newest versions plus CURRENT and EXTRA versions in any case. Non-version folders are left alone.
hp_releases_to_prune() {
    local dir="$1" keep="$2"
    shift 2
    local -a protect=("$@") versions=()
    local v p skip
    for v in "$dir"/*; do
        [[ -d "$v" && ! -L "$v" ]] || continue
        v="$(basename "$v")"
        hp_is_plain_version "$v" && versions+=("$v")
    done
    [[ ${#versions[@]} -gt 0 ]] || return 0
    local sorted
    sorted="$(printf '%s\n' "${versions[@]}" | sort -t. -k1,1nr -k2,2nr -k3,3nr)"
    local i=0
    while IFS= read -r v; do
        i=$((i + 1))
        [[ $i -le $keep ]] && continue
        skip=0
        for p in "${protect[@]}"; do [[ "$p" == "$v" ]] && skip=1; done
        [[ $skip -eq 1 ]] || printf '%s\n' "$v"
    done <<<"$sorted"
}

hp_one_line() { printf '%s' "$*" | tr '\r\n\t' '   ' | cut -c1-1500; }

# hp_trusted_dir DIR: sets HP_TRUSTED_DIR to the real path of DIR. Fails unless DIR is a folder (not a symbolic
# link) that only root can change: DIR and every folder above it owned by root and not writable by group or others
# (a sticky folder such as /tmp is accepted above DIR, never as DIR). Root can then create files in it without
# races. Needs GNU stat.
# shellcheck disable=SC2034 # HP_TRUSTED_DIR is read by the callers (backup.sh)
hp_trusted_dir() {
    local dir="$1" path cur uid mode first=1
    HP_TRUSTED_DIR=""
    while [[ "$dir" == */ && "$dir" != / ]]; do dir="${dir%/}"; done
    [[ -d "$dir" && ! -L "$dir" ]] || { HP_ERROR="$dir is not a folder (or is a symbolic link)"; return 1; }
    path="$(realpath -e -- "$dir")" || { HP_ERROR="cannot resolve $dir"; return 1; }
    cur="$path"
    while true; do
        read -r uid mode < <(stat -c '%u %a' -- "$cur") || { HP_ERROR="cannot read the owner of $cur"; return 1; }
        [[ "$uid" == 0 ]] || { HP_ERROR="$cur is not owned by root"; return 1; }
        if (((8#$mode & 8#022) != 0)) && { ((first)) || (((8#$mode & 8#1000) == 0)); }; then
            HP_ERROR="$cur is writable by other users than root"; return 1
        fi
        [[ "$cur" == / ]] && break
        cur="$(dirname -- "$cur")"
        first=0
    done
    HP_TRUSTED_DIR="$path"
}

# hp_root_dir DIR [replace-link]: makes DIR a folder owned by root:root with mode 700 (created when missing). Its
# parent must already be a folder only root can change. A symbolic link at DIR is refused, or with replace-link
# removed and replaced by a folder. Only DIR itself is changed; links are never followed.
hp_root_dir() {
    local dir="$1"
    if [[ -L "$dir" ]]; then
        [[ "${2-}" == replace-link ]] || { HP_ERROR="$dir is a symbolic link; make it a real folder"; return 1; }
        rm -f -- "$dir" || { HP_ERROR="cannot remove the link $dir"; return 1; }
    fi
    if [[ ! -e "$dir" ]]; then mkdir -m 700 -- "$dir" || { HP_ERROR="cannot create $dir"; return 1; }; fi
    [[ -d "$dir" && ! -L "$dir" ]] || { HP_ERROR="$dir is not a folder"; return 1; }
    { chown -h root:root -- "$dir" && chmod 700 -- "$dir"; } || { HP_ERROR="cannot set the owner of $dir"; return 1; }
    [[ ! -L "$dir" && "$(stat -c '%u %a' -- "$dir")" == "0 700" ]] || { HP_ERROR="$dir is not root:root 700"; return 1; }
}

# ------------------------------------------------------------------------------------------------ main (root)

hp_write_result() {
    # data/updates belongs to the service user, so the service user writes the file itself (temp file + rename):
    # root never opens a path there for writing, and a planted link or FIFO reaches only what that user can reach.
    local state="$1" message="$2" dir="$HP_ROOT/data/updates"
    local now
    now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    # shellcheck disable=SC2016 # $1 and $t are expanded by the inner shell
    if ! printf 'format=1\nrun_id=%s\nversion=%s\nfrom_version=%s\nstate=%s\nmessage=%s\nfinished_at=%s\n' \
            "${REQ_RUN_ID-}" "${REQ_VERSION-}" "${CURRENT_VERSION:-${REQ_FROM-}}" "$state" "$(hp_one_line "$message")" "$now" |
        runuser -u "$HP_USER" -- env -i PATH=/usr/sbin:/usr/bin:/sbin:/bin LANG=C.UTF-8 /bin/sh -c \
            'umask 077; t="$(mktemp "$1/.result.XXXXXX")" || exit 1; if cat >"$t" && mv -fT -- "$t" "$1/result.conf"; then exit 0; fi; rm -f -- "$t"; exit 1' \
            sh "$dir"; then
        hp_log "cannot write $dir/result.conf"
        return 0
    fi
    hp_log "result: $state - $message"
}

hp_health_ok() {
    # Healthy = /healthz answers and reports the expected version.
    local expected="$1" body
    body="$(curl -fsk --max-time 5 -- "$HEALTH_URL" 2>/dev/null)" || return 1
    [[ "$body" == *"\"version\":\"$expected\""* ]]
}

hp_wait_healthy() {
    local expected="$1" timeout="$2" deadline
    deadline=$((SECONDS + timeout))
    while ((SECONDS < deadline)); do
        if hp_health_ok "$expected"; then return 0; fi
        sleep 3
    done
    return 1
}

hp_pg() { local tool="$1"; shift; if [[ -n "${PG_BIN-}" && -x "$PG_BIN/$tool" ]]; then "$PG_BIN/$tool" "$@"; else "$tool" "$@"; fi; }

# hp_prepare_backup_dir: backups/ and backups/pre-update/ as root:root 700, so only root can write there and the
# dump is created without races. Should backups/ have another owner or mode, it is taken over here (owner and mode
# of the folders only; install.sh also fixes what is inside). A link at backups/ is refused (only root can
# put one there); one at pre-update/ is replaced (the service user could have planted it).
hp_prepare_backup_dir() {
    local top="$HP_ROOT/backups"
    BACKUP_DIR="$top/pre-update"
    hp_root_dir "$top" || return 1
    hp_root_dir "$BACKUP_DIR" replace-link
}

hp_backup_db() {
    # Into the root-only folder from hp_prepare_backup_dir; the name must not exist yet, not even as a link.
    local stamp name
    stamp="$(date -u +%Y%m%dT%H%M%SZ)"
    name="homepage-${CURRENT_VERSION}-to-${REQ_VERSION}-$stamp.dump"
    BACKUP_FILE="$BACKUP_DIR/$name"
    [[ -n "$BACKUP_DIR" && -d "$BACKUP_DIR" && ! -L "$BACKUP_DIR" && "$(stat -c %u -- "$BACKUP_DIR")" == 0 ]] || return 1
    [[ ! -e "$BACKUP_FILE" && ! -L "$BACKUP_FILE" ]] || return 1
    ( set -o noclobber; umask 077; hp_pg pg_dump --format=custom --no-password >"$BACKUP_FILE" )
}

hp_restore_db() {
    [[ -n "${BACKUP_FILE-}" && -f "$BACKUP_FILE" && ! -L "$BACKUP_FILE" ]] || return 1
    # The role owns the database (install.sh checks it), so it can drop and recreate the public schema.
    hp_pg psql --no-password -v ON_ERROR_STOP=1 -q -c 'DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;' &&
        hp_pg pg_restore --no-password --no-owner --no-privileges --exit-on-error -d "$PGDATABASE" "$BACKUP_FILE"
}

hp_switch_current() {
    local target="$1" tmp="$HP_ROOT/.current.$$"
    rm -f -- "$tmp"
    ln -s -- "$target" "$tmp"
    mv -Tf -- "$tmp" "$HP_ROOT/current"
}

# hp_sandboxed_run USER WORKDIR CONFIG PROGRAM ARGS...: runs a release's program as USER with the same sandbox as
# homepage.service (systemd-run), detached from root's terminal and without root's open files. Without systemd
# (containers used for tests), it falls back to runuser in a new session. stdin/stdout/stderr are passed through.
hp_sandboxed_run() {
    local user="$1" workdir="$2" config="$3"
    shift 3
    local root
    root="$(dirname -- "$(dirname -- "$config")")"
    if [[ -d /run/systemd/system ]] && command -v systemd-run >/dev/null 2>&1; then
        systemd-run --quiet --wait --collect --pipe --service-type=exec \
            -p User="$user" -p Group="$user" -p WorkingDirectory="$workdir" \
            -p NoNewPrivileges=yes -p ProtectSystem=strict -p ReadWritePaths="$root/data" -p ProtectHome=yes \
            -p PrivateTmp=yes -p PrivateDevices=yes -p ProtectKernelTunables=yes -p ProtectKernelModules=yes \
            -p ProtectKernelLogs=yes -p ProtectControlGroups=yes -p ProtectClock=yes -p ProtectHostname=yes \
            -p RestrictSUIDSGID=yes -p RestrictRealtime=yes -p RestrictNamespaces=yes -p LockPersonality=yes \
            -p RestrictAddressFamilies="AF_UNIX AF_INET AF_INET6 AF_NETLINK" -p SystemCallArchitectures=native -p UMask=0077 \
            -E PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin -E HOME=/nonexistent -E LANG=C.UTF-8 \
            -E ASPNETCORE_ENVIRONMENT=Production -E HOMEPAGE_CONFIG="$config" -E DOTNET_NOLOGO=1 -E DOTNET_CLI_TELEMETRY_OPTOUT=1 \
            -- "$@" 9>&-
    else
        (cd "$workdir" && setsid --wait runuser -u "$user" -- env -i \
            PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin HOME=/nonexistent LANG=C.UTF-8 \
            ASPNETCORE_ENVIRONMENT=Production HOMEPAGE_CONFIG="$config" DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 \
            "$@" 9>&-)
    fi
}

hp_run_as_service_user() {
    # Runs a release's server binary as the service user, from the release folder, with the production config.
    local release="$1"
    shift
    hp_sandboxed_run "$HP_USER" "$release" "$HP_ROOT/config/appsettings.Production.json" "$release/Homepage.Server" "$@" </dev/null
}

# hp_read_as_service_user SRC DST MAXBYTES: copies a file from the service user's folders by reading it AS the service
# user (so links, swaps and FIFOs gain nothing root could read), with a size cap and a timeout. Fails when bigger.
hp_read_as_service_user() {
    local src="$1" dst="$2" max="$3"
    timeout 300 runuser -u "$HP_USER" -- head -c "$((max + 1))" -- "$src" >"$dst" 2>/dev/null </dev/null || return 1
    (($(stat -c %s -- "$dst") <= max))
}

# hp_verify_published NAME FILE: the package's SHA-256 must be the one in the SHA256SUMS.txt of release v$REQ_VERSION,
# downloaded by root from GitHub (HTTPS only), not the copy the service user staged.
hp_verify_published() {
    local name="$1" file="$2" official approved="$HP_ROOT/config/upgrade-approved" line=""
    # install.sh --upgrade with a package the admin chose (also offline) approves exactly that package: a root-only
    # file in config/, which the service user cannot write. Used once.
    if [[ -f "$approved" && ! -L "$approved" && "$(stat -c %u -- "$approved")" == 0 ]]; then
        IFS= read -r line <"$approved" || true
        rm -f -- "$approved"
        if [[ "$line" == "$REQ_VERSION $(hp_sha256 "$file")" ]]; then hp_log "package approved by install.sh"; return 0; fi
    fi
    hp_is_repo "${GITHUB_REPOSITORY-}" ||
        hp_fail "GITHUB_REPOSITORY in update.env is not owner/name; fix it, or remove the line and run install.sh --repair"
    curl -fsSL --proto '=https' --proto-redir '=https' --tlsv1.2 --max-time 120 --max-filesize 65536 \
        -o "$WORK/SHA256SUMS.published" \
        "https://github.com/$GITHUB_REPOSITORY/releases/download/v$REQ_VERSION/SHA256SUMS.txt" ||
        hp_fail "cannot download SHA256SUMS.txt of release v$REQ_VERSION from github.com/$GITHUB_REPOSITORY"
    official="$(hp_sums_lookup "$WORK/SHA256SUMS.published" "$name")" || hp_fail "release v$REQ_VERSION has no $name"
    [[ "$(hp_sha256 "$file")" == "$official" ]] ||
        hp_fail "$name is not the package published in release v$REQ_VERSION of $GITHUB_REPOSITORY"
}

hp_rollback() {
    local why="$1" state=RolledBack note=""
    hp_log "rolling back: $why"
    systemctl stop "$HP_SERVICE" || true
    if [[ "$PHASE" == migrated || "$PHASE" == switched ]]; then
        if hp_restore_db; then note="database restored from $(basename "$BACKUP_FILE")"
        else state=Failed; note="DATABASE RESTORE FAILED, backup: ${BACKUP_FILE-none}"; fi
    fi
    if [[ -n "${PREVIOUS_TARGET-}" ]]; then hp_switch_current "$PREVIOUS_TARGET"; fi
    # Write the result before the old version starts, so it finds the result on start.
    hp_write_result "$state" "$why; rolled back to $CURRENT_VERSION. $note"
    systemctl start --no-block "$HP_SERVICE" || true
    if ! hp_wait_healthy "$CURRENT_VERSION" 60; then hp_log "the old version $CURRENT_VERSION is not healthy either"; fi
    PHASE="done"
}

hp_fail() {
    # Before the service was stopped: nothing changed, just report. After: roll back, best effort.
    local why="$1"
    trap - ERR
    set +e
    if [[ "$PHASE" == stopped || "$PHASE" == migrated || "$PHASE" == switched ]]; then
        hp_rollback "$why"
    elif [[ "$PHASE" != "done" ]]; then
        hp_write_result Failed "$why"
        if [[ "$PHASE" == prepared ]]; then systemctl start --no-block "$HP_SERVICE" 2>/dev/null || true; fi
        PHASE="done"
    fi
    exit 1
}

hp_on_error() { local line="$1"; trap - ERR; hp_fail "unexpected error in update.sh at line $line"; }

hp_main() {
    PHASE=init
    REQ_RUN_ID="" REQ_VERSION="" REQ_FROM="" CURRENT_VERSION="" PREVIOUS_TARGET="" BACKUP_FILE="" BACKUP_DIR=""
    [[ $EUID -eq 0 ]] || { hp_log "must run as root"; exit 1; }
    umask 022

    local updates="$HP_ROOT/data/updates" request="$HP_ROOT/data/updates/request.conf"
    exec 9>/run/homepage-update.lock
    flock -n 9 || { hp_log "another update is running"; exit 0; }
    if [[ ! -e "$request" && ! -L "$request" ]]; then hp_log "no update request"; exit 0; fi

    # Work folder on disk in the root-owned releases folder (/run is a small tmpfs; the package is ~50 MB).
    [[ -d "$HP_ROOT/releases" && ! -L "$HP_ROOT/releases" ]] || { hp_log "$HP_ROOT/releases is missing"; exit 1; }
    rm -rf -- "$HP_ROOT"/releases/.work.*
    WORK="$(mktemp -d "$HP_ROOT/releases/.work.XXXXXX")"
    trap 'rm -rf -- "$WORK"' EXIT
    trap 'hp_on_error $LINENO' ERR

    # Take the request away first: the path unit fires again as long as request.conf exists.
    local size
    # A link at data/updates is removed itself (unlink does not follow it), never anything behind it.
    if [[ -L "$updates" || ! -d "$updates" ]]; then [[ ! -L "$updates" ]] || rm -f -- "$updates"; hp_fail "$updates is not a folder"; fi
    # Read and remove it as the service user: whatever it points to by then, root never follows it.
    hp_read_as_service_user "$request" "$WORK/request.conf" "$MAX_REQUEST_BYTES" || {
        runuser -u "$HP_USER" -- rm -f -- "$request" </dev/null || true
        hp_fail "the request is not a readable file of at most $MAX_REQUEST_BYTES bytes"
    }
    runuser -u "$HP_USER" -- rm -f -- "$request" </dev/null || hp_fail "cannot remove the request"

    HP_ERROR=""
    hp_parse_request "$WORK/request.conf" || hp_fail "invalid request: $HP_ERROR"

    local env_file="$HP_ROOT/config/update.env"
    [[ -f "$env_file" && ! -L "$env_file" ]] || hp_fail "$env_file is missing (re-run install.sh to repair)"
    [[ "$(stat -c %u -- "$env_file")" == 0 ]] || hp_fail "$env_file is not owned by root"
    HEALTH_URL="" PG_BIN="" GITHUB_REPOSITORY=""
    hp_read_env_file "$env_file"
    if [[ -z "$GITHUB_REPOSITORY" ]]; then
        GITHUB_REPOSITORY="$HP_DEFAULT_REPOSITORY"
        hp_log "$env_file has no GITHUB_REPOSITORY; using $GITHUB_REPOSITORY (install.sh --repair adds it)"
    fi
    [[ -n "$HEALTH_URL" && -n "${PGDATABASE-}" ]] || hp_fail "$env_file lacks HEALTH_URL or PGDATABASE"

    # Versions: plain x.y.z, and newer than the running release only.
    PREVIOUS_TARGET="$(readlink -- "$HP_ROOT/current")" || hp_fail "$HP_ROOT/current is not a symlink"
    CURRENT_VERSION="$(basename -- "$PREVIOUS_TARGET")"
    hp_is_plain_version "$CURRENT_VERSION" || hp_fail "cannot tell the running version from $HP_ROOT/current"
    hp_version_gt "$REQ_VERSION" "$CURRENT_VERSION" || hp_fail "version $REQ_VERSION is not newer than $CURRENT_VERSION"

    local arch
    arch="$(hp_machine_arch)" || hp_fail "unsupported CPU architecture $(uname -m)"
    [[ "$arch" == "$REQ_ARCH" ]] || hp_fail "package is for $REQ_ARCH, this machine is $arch"

    # Copy package and sums out of the service user's folder before checking them (no link games, no swaps).
    local stage_dir="$updates/$REQ_VERSION" name
    name="$(hp_tarball_name "$REQ_VERSION" "$arch")"
    hp_read_as_service_user "$stage_dir/$name" "$WORK/$name" "$MAX_PACKAGE_BYTES" || hp_fail "the package $name is missing or too large"

    # The staged hash only proves the copy is intact; the published SHA256SUMS.txt proves it is the real release.
    local actual
    actual="$(hp_sha256 "$WORK/$name")"
    [[ "$actual" == "$REQ_SHA256" ]] || hp_fail "SHA-256 mismatch for $name"
    hp_verify_published "$name" "$WORK/$name"

    # Unpack safely into releases/<version>.
    local releases="$HP_ROOT/releases" release="$HP_ROOT/releases/$REQ_VERSION" staging="$HP_ROOT/releases/.staging-$REQ_VERSION"
    rm -rf -- "$staging"
    HP_ERROR=""
    hp_safe_unpack "$WORK/$name" "$staging" || { rm -rf -- "$staging"; hp_fail "unsafe or broken package: $HP_ERROR"; }
    [[ -f "$staging/Homepage.Server" && -x "$staging/Homepage.Server" ]] || { rm -rf -- "$staging"; hp_fail "package has no Homepage.Server"; }
    rm -rf -- "$release"
    mv -T -- "$staging" "$release"
    hp_log "unpacked $REQ_VERSION into $release"

    # Stop, back up, migrate as the service user, switch, start. The backup folder is checked before the stop.
    HP_ERROR=""
    hp_prepare_backup_dir || hp_fail "backup folder: $HP_ERROR (run install.sh --repair)"
    PHASE=prepared
    systemctl stop "$HP_SERVICE"
    PHASE=stopped
    hp_backup_db || hp_fail "pg_dump failed"
    hp_log "database backup: $BACKUP_FILE"
    PHASE=migrated # from here on the database may have changed
    hp_run_as_service_user "$release" --migrate-only || hp_fail "migrations of $REQ_VERSION failed"
    hp_switch_current "$release"
    PHASE=switched
    # --no-block: with Type=notify a blocking start waits up to TimeoutStartSec; the 2-minute health check decides.
    systemctl start --no-block "$HP_SERVICE" || hp_fail "$HP_SERVICE did not start"
    hp_wait_healthy "$REQ_VERSION" "$HEALTH_TIMEOUT_SECONDS" || hp_fail "$REQ_VERSION was not healthy within $HEALTH_TIMEOUT_SECONDS seconds"
    PHASE="done"
    trap - ERR

    hp_write_result Succeeded "Updated from $CURRENT_VERSION to $REQ_VERSION. Backup: $(basename "$BACKUP_FILE")"

    # Keep the last releases for rollback (never the running one or the one before).
    local old
    while IFS= read -r old; do
        [[ -n "$old" ]] || continue
        hp_log "removing old release $old"
        rm -rf -- "${releases:?}/$old"
    done < <(hp_releases_to_prune "$releases" "$KEEP_RELEASES" "$REQ_VERSION" "$CURRENT_VERSION")
    rm -rf -- "$releases"/.staging-*
    return 0
}

# ------------------------------------------------------------------------------------------------ self-test

hp_self_test() {
    local fails=0
    t() { if "$@" >/dev/null; then :; else echo "FAIL: $*"; fails=$((fails + 1)); fi; }
    f() { if "$@" >/dev/null 2>&1; then echo "FAIL (expected false): $*"; fails=$((fails + 1)); fi; }
    t hp_is_plain_version 1.2.3
    t hp_is_plain_version 10.20.30
    f hp_is_plain_version 1.2
    f hp_is_plain_version 1.2.3.4
    f hp_is_plain_version 1.2.3-beta
    f hp_is_plain_version v1.2.3
    f hp_is_plain_version "1.2.3 "
    f hp_is_plain_version "1.2.3;rm"
    f hp_is_plain_version ""
    t hp_version_gt 1.2.10 1.2.9
    t hp_version_gt 2.0.0 1.99.99
    f hp_version_gt 1.2.3 1.2.3
    f hp_version_gt 1.2.2 1.2.3
    f hp_version_gt 1.2.3-rc 1.0.0
    t hp_is_repo BarPos/Homepage
    t hp_is_repo my-org/my.site_2
    f hp_is_repo BarPos
    f hp_is_repo "BarPos/Homepage/x"
    f hp_is_repo "../etc/passwd"
    f hp_is_repo "a/.."
    f hp_is_repo "evil.example/x?y"
    f hp_is_repo ""
    t hp_is_guid 0f8fad5b-d9cb-469f-a165-70867728950e
    f hp_is_guid ../../etc
    t hp_is_sha256 "$(printf 'a%.0s' {1..64})"
    f hp_is_sha256 "$(printf 'A%.0s' {1..64})"
    t hp_check_member_names <<<$'./\n./Homepage.Server\n./wwwroot/app.css'
    f hp_check_member_names <<<$'./ok\n/etc/passwd'
    f hp_check_member_names <<<$'./ok\n../evil'
    f hp_check_member_names <<<$'./a/../../evil'
    f hp_check_member_names <<<$'a/..'
    t hp_check_member_names <<<$'./a..b/c..'
    t hp_check_member_types <<<$'drwxr-xr-x root/root 0 x ./\n-rw-r--r-- root/root 1 x ./a\nlrwxrwxrwx root/root 0 x ./b -> a'
    f hp_check_member_types <<<$'-rw-r--r-- root/root 1 x ./a\nhrw-r--r-- root/root 0 x ./b link to ./a'
    f hp_check_member_types <<<$'crw-r--r-- root/root 1,3 x ./null'
    t hp_check_member_modes <<<$'drwxr-xr-x root/root 0 x ./\n-rwxr-xr-x root/root 1 x ./a\ndrwxrwxrwt root/root 0 x ./tmp'
    f hp_check_member_modes <<<$'-rwsr-xr-x root/root 1 x ./a'
    f hp_check_member_modes <<<$'-rwxr-Sr-x root/root 1 x ./a'
    f hp_check_member_modes <<<$'drwxr-sr-x root/root 0 x ./d'
    t hp_normalize_relpath a/b/../c
    f hp_normalize_relpath a/../../b
    f hp_normalize_relpath ..
    [[ "$(hp_normalize_relpath ./a/./b/../c)" == a/c ]] || { echo "FAIL: normalize ./a/./b/../c"; fails=$((fails + 1)); }
    t hp_is_port 5080
    t hp_is_port 65535
    f hp_is_port 0
    f hp_is_port 65536
    f hp_is_port 0443
    f hp_is_port "80;x"
    t hp_is_bind 127.0.0.1
    t hp_is_bind ::
    f hp_is_bind 192.168.1.10
    f hp_is_bind "127.0.0.1|x"
    [[ "$(hp_health_url http 127.0.0.1 5080)" == http://127.0.0.1:5080/healthz ]] || { echo "FAIL: health_url http loopback"; fails=$((fails + 1)); }
    [[ "$(hp_health_url https 0.0.0.0 443)" == https://127.0.0.1:443/healthz ]] || { echo "FAIL: health_url https wildcard"; fails=$((fails + 1)); }
    [[ "$(hp_health_url http :: 8080)" == 'http://[::1]:8080/healthz' ]] || { echo "FAIL: health_url ipv6 wildcard"; fails=$((fails + 1)); }
    f hp_health_url http 10.0.0.1 80
    f hp_health_url ftp 127.0.0.1 80
    f hp_health_url http 127.0.0.1 "80|x"
    if [[ $fails -eq 0 ]]; then echo "self-test passed"; return 0; fi
    echo "self-test: $fails failure(s)"
    return 1
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    case "${1-}" in
        --self-test) hp_self_test ;;
        "") hp_main ;;
        -h|--help) sed -n '3,24p' "$0" | sed 's/^# \{0,1\}//' ;;
        *) hp_log "unknown argument: $1"; exit 2 ;;
    esac
fi
