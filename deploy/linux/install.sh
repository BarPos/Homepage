#!/usr/bin/env bash
# Adapted from DomainNotice (MIT), https://github.com/BarPos/DomainNotice
# Homepage server installer for Ubuntu 24.04 LTS and newer. No Docker.
#
#   sudo ./install.sh                                   interactive: asks every question
#   sudo ./install.sh --unattended --package FILE ...   unattended, for automated installs
#   sudo ./install.sh --repair | --upgrade              on an installed server (re-running offers both)
#   sudo ./install.sh --backup [--output PATH]          backup of the database, configuration, data (media, keys)
#                                                       and program
#   sudo ./install.sh --restore FILE                    restores such a backup on this server
#   sudo ./install.sh --reset-password [USER]           new password for a local account
#   sudo ./install.sh --uninstall                       removes EVERYTHING, including the database (offers a backup
#                                                       first and asks for confirmation); --keep-data keeps config,
#                                                       data and the database and removes only the program
#
# Package source:
#   --package FILE        a Homepage.Server-<version>-linux-<arch>.tar.gz from build/build-server.sh or a release
#                         (checked against SHA256SUMS.txt next to it when present)
#   --version X.Y.Z       download that release from GitHub (default: latest), from GITHUB_REPOSITORY below.
#                         Without --package and --version, the latest release is downloaded.
#                         --repair uses a package only when one of them is given: then update.sh, backup.sh and the
#                         units are reinstalled from it; without, the installed copies are kept.
# Database:
#   --db=local|existing   local: PostgreSQL on this machine. Installed when there is none; an existing one (that
#                         other applications may use) is used as it is: Homepage only adds its own new role and
#                         database (named "homepage", or "homepage_2" ... when that name is taken) and never
#                         installs, upgrades, reconfigures or restarts the server or changes other roles and databases.
#                         --db-port picks the cluster when there are several; --db-name sets the name (it must be free).
#   --db-host H --db-port P --db-name N --db-user U --db-sslmode disable|allow|prefer|require|verify-ca|verify-full
# Site:
#   --mode=https|http        https (default): Kestrel serves HTTPS with a certificate on 0.0.0.0. http: plain HTTP,
#                            no certificate, on 127.0.0.1: for running behind Caddy or nginx on this machine, which
#                            handle HTTPS with Let's Encrypt (the usual choice for a public website)
#   --port N                 site port (default 443 for https, 5080 for http)
#   --bind ADDRESS           address Kestrel listens on (default 0.0.0.0 for https, 127.0.0.1 for http);
#                            127.0.0.1, 0.0.0.0, ::1 or :: (the health check must reach it on loopback)
#   --server-name NAME       DNS name browsers use (default: hostname -f)
#   --cert FILE --cert-key FILE   https mode: existing PEM certificate and key; without them a self-signed one is made
#   --admin-user NAME        first local account (an administrator)
# Other:
#   --open-firewall=yes|no   open the site port when ufw is active and the site is not bound to loopback
#                            (unattended default: no)
#   --yes                    confirm --uninstall / --restore in unattended mode
#   --backup-to FILE         --uninstall: write a backup to FILE first (unattended; interactive asks)
#
# Secrets never come from command-line flags (other users can see the process list). Sources, in order:
#   --secrets-file FILE   KEY=VALUE lines, the file must be mode 600 (or 400)
#   --secrets-stdin       the same KEY=VALUE lines on standard input
#   environment           HOMEPAGE_DB_PASSWORD, HOMEPAGE_ADMIN_PASSWORD
#   prompt                hidden input (interactive mode)
# Keys: DB_PASSWORD (existing database), ADMIN_PASSWORD (first local account or --reset-password, at least 8 characters).
set -Eeuo pipefail

# GitHub repository whose releases (built by .github/workflows/release.yml) install.sh and the server's updater use.
# Blank = no network access to GitHub (then only --package works).
GITHUB_REPOSITORY="BarPos/Homepage"

INSTALL_ROOT=/opt/homepage
SERVICE_USER=homepage
UNIT_DIR=/etc/systemd/system
UNITS=(homepage.service homepage-update.path homepage-update.service)
CONFIG_FILE="$INSTALL_ROOT/config/appsettings.Production.json"
UPDATE_ENV="$INSTALL_ROOT/config/update.env"
STATE_FILE="$INSTALL_ROOT/config/install.state"
USER_AGENT="Homepage-Installer"

MODE="" UNATTENDED=0 ASSUME_YES=0
BACKUP_OUTPUT="" RESTORE_FILE="" RESET_USER="" KEEP_DATA=0 BACKUP_TO=""
PACKAGE="" VERSION_ARG=""
DB_MODE="" DB_HOST="" DB_PORT="" DB_NAME="" DB_USER="" DB_SSLMODE="" DB_CREATED=0
SITE_PORT="" SITE_BIND="" SERVER_NAME="" CERT_FILE="" CERT_KEY_FILE="" ADMIN_USER="" OPEN_FIREWALL="" SITE_MODE=""
SECRETS_FILE="" SECRETS_STDIN=0
DB_PASSWORD="" ADMIN_PASSWORD=""
ARCH="" PKG_FILE="" PKG_VERSION="" PG_BIN="" WORK=""

# ------------------------------------------------------------------------------------------------ helpers

info() { printf '==> %s\n' "$*"; }
warn() { printf 'WARNING: %s\n' "$*" >&2; }
die() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }
usage() { sed -n '3,51p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; }

INSTALL_DONE=0
cleanup() {
    if [[ -n "$WORK" && -d "$WORK" ]]; then rm -rf -- "$WORK"; fi
    # A fresh install that failed after creating its own role and database removes them again (and only them), so a
    # second attempt starts clean instead of moving on to homepage_2.
    if [[ "${DB_CREATED:-0}" -eq 1 && $INSTALL_DONE -eq 0 && "$DB_MODE" == local ]]; then
        warn "the install did not finish: removing the database '$DB_NAME' and role '$DB_USER' it created"
        runuser -u postgres -- psql -X -q -p "$DB_PORT" -d postgres -c "DROP DATABASE IF EXISTS \"$DB_NAME\" WITH (FORCE)" >/dev/null 2>&1 || true
        runuser -u postgres -- psql -X -q -p "$DB_PORT" -d postgres -c "DROP ROLE IF EXISTS \"$DB_USER\"" >/dev/null 2>&1 || true
    fi
}
trap cleanup EXIT
trap 'die "install.sh failed at line $LINENO"' ERR

tty_read() { # tty_read VAR PROMPT [DEFAULT]
    local __var="$1" __prompt="$2" __def="${3-}" __ans
    if [[ -n "$__def" ]]; then __prompt="$__prompt [$__def]"; fi
    read -r -p "$__prompt: " __ans </dev/tty || die "no answer"
    printf -v "$__var" '%s' "${__ans:-$__def}"
}

tty_secret() { # tty_secret VAR PROMPT [confirm]
    local __var="$1" __prompt="$2" __a __b
    while true; do
        read -r -s -p "$__prompt: " __a </dev/tty || die "no answer"; echo >/dev/tty
        if [[ "${3-}" == confirm ]]; then
            read -r -s -p "Repeat: " __b </dev/tty || die "no answer"; echo >/dev/tty
            [[ "$__a" == "$__b" ]] || { echo "The entries differ, try again." >/dev/tty; continue; }
        fi
        [[ -n "$__a" ]] && break
    done
    printf -v "$__var" '%s' "$__a"
}

ask() { # ask VAR PROMPT DEFAULT: keeps a value given by flag; unattended takes the default
    local __var="$1"
    [[ -n "${!__var}" ]] && return 0
    if [[ $UNATTENDED -eq 1 ]]; then printf -v "$__var" '%s' "${3-}"; return 0; fi
    tty_read "$__var" "$2" "${3-}"
}

ask_yes_no() { # ask_yes_no PROMPT DEFAULT(y|n) -> status
    local ans
    if [[ $UNATTENDED -eq 1 ]]; then [[ "$2" == y ]]; return; fi
    read -r -p "$1 [$([[ "$2" == y ]] && echo Y/n || echo y/N)]: " ans </dev/tty || return 1
    ans="${ans:-$2}"
    [[ "$ans" == [yY]* ]]
}

json_escape() { # prints the JSON string body for $1
    local s="$1" out="" c i
    for ((i = 0; i < ${#s}; i++)); do
        c="${s:i:1}"
        case "$c" in
            '"') out+='\"' ;;
            '\') out+='\\' ;;
            $'\n') out+='\n' ;;
            $'\r') out+='\r' ;;
            $'\t') out+='\t' ;;
            *) if [[ "$c" < ' ' ]]; then printf -v c '\\u%04x' "'$c"; fi; out+="$c" ;;
        esac
    done
    printf '%s' "$out"
}

cs_value() { # Npgsql connection-string value; quoted when needed, embedded quotes doubled
    local v="$1"
    if [[ "$v" == *[\;\"\'=]* || "$v" != "${v#[[:space:]]}" || "$v" != "${v%[[:space:]]}" ]]; then
        printf '"%s"' "${v//\"/\"\"}"
    else
        printf '%s' "$v"
    fi
}

random_alnum() { # random_alnum LENGTH
    local out=""
    while [[ ${#out} -lt $1 ]]; do out+="$(openssl rand -base64 48 | tr -dc 'A-Za-z0-9')"; done
    printf '%s' "${out:0:$1}"
}

new_uuid() { cat /proc/sys/kernel/random/uuid; }

npgsql_sslmode() {
    case "$1" in
        disable) echo Disable ;; allow) echo Allow ;; prefer) echo Prefer ;; require) echo Require ;;
        verify-ca) echo VerifyCA ;; verify-full) echo VerifyFull ;; *) return 1 ;;
    esac
}

apt_has_candidate() {
    local c
    c="$(apt-cache policy "$1" 2>/dev/null | awk '/Candidate:/ {print $2; exit}')"
    [[ -n "$c" && "$c" != "(none)" ]]
}

run_server() { # run_server RELEASE_DIR ARGS...: the server CLI as the service user (never as root)
    local release="$1"
    shift
    (cd "$release" && runuser -u "$SERVICE_USER" -- env -i \
        PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin HOME=/nonexistent LANG=C.UTF-8 \
        ASPNETCORE_ENVIRONMENT=Production HOMEPAGE_CONFIG="$CONFIG_FILE" \
        DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 \
        "$release/Homepage.Server" "$@")
}

installed_version() {
    local t
    t="$(readlink "$INSTALL_ROOT/current" 2>/dev/null)" || return 1
    basename "$t"
}

read_state() { # read_state KEY
    [[ -f "$STATE_FILE" ]] || return 1
    local line
    while IFS= read -r line; do
        [[ "$line" == "$1="* ]] && { printf '%s' "${line#*=}"; return 0; }
    done <"$STATE_FILE"
    return 1
}

# ------------------------------------------------------------------------------------------------ arguments

parse_args() {
    local a v
    while [[ $# -gt 0 ]]; do
        a="$1"
        v=""
        if [[ "$a" == --*=* ]]; then v="${a#*=}"; a="${a%%=*}"; fi
        case "$a" in
            -h|--help) usage; exit 0 ;;
            --unattended) UNATTENDED=1 ;;
            --yes|-y) ASSUME_YES=1 ;;
            --repair) MODE=repair ;;
            --upgrade) MODE=upgrade ;;
            --uninstall|--purge) MODE=uninstall ;;
            --keep-data) KEEP_DATA=1 ;;
            --backup) MODE=backup ;;
            --reset-password)
                MODE=reset-password
                if [[ -n "$v" ]]; then RESET_USER="$v"; elif [[ $# -ge 2 && "$2" != -* ]]; then RESET_USER="$2"; shift; fi ;;
            --secrets-stdin) SECRETS_STDIN=1 ;;
            --package|--version|--db|--db-host|--db-port|--db-name|--db-user|--db-sslmode|--port|--bind|--server-name|\
            --cert|--cert-key|--admin-user|--open-firewall|--secrets-file|--mode|--restore|--output|--backup-to)
                local val
                if [[ -n "$v" ]]; then val="$v"; else [[ $# -ge 2 ]] || die "$a needs a value"; val="$2"; shift; fi
                case "$a" in
                    --package) PACKAGE="$val" ;; --version) VERSION_ARG="$val" ;; --db) DB_MODE="$val" ;;
                    --db-host) DB_HOST="$val" ;; --db-port) DB_PORT="$val" ;; --db-name) DB_NAME="$val" ;;
                    --db-user) DB_USER="$val" ;; --db-sslmode) DB_SSLMODE="$val" ;; --port) SITE_PORT="$val" ;;
                    --server-name) SERVER_NAME="$val" ;; --cert) CERT_FILE="$val" ;; --cert-key) CERT_KEY_FILE="$val" ;;
                    --admin-user) ADMIN_USER="$val" ;; --open-firewall) OPEN_FIREWALL="$val" ;; --secrets-file) SECRETS_FILE="$val" ;;
                    --mode) SITE_MODE="$val" ;; --bind) SITE_BIND="$val" ;;
                    --restore) MODE=restore; RESTORE_FILE="$val" ;; --output) BACKUP_OUTPUT="$val" ;; --backup-to) BACKUP_TO="$val" ;;
                esac ;;
            --db-password|--admin-password|--password*)
                die "$a: passwords are never accepted as flags; use --secrets-file, --secrets-stdin or the environment" ;;
            *) die "unknown argument: $1 (see --help)" ;;
        esac
        shift
    done
}

load_secrets() {
    local line key value
    parse_secret_line() {
        line="${1%$'\r'}"
        [[ -z "$line" || "$line" == \#* || "$line" != *=* ]] && return 0
        key="${line%%=*}"
        value="${line#*=}"
        case "$key" in
            DB_PASSWORD) [[ -n "$DB_PASSWORD" ]] || DB_PASSWORD="$value" ;;
            ADMIN_PASSWORD) [[ -n "$ADMIN_PASSWORD" ]] || ADMIN_PASSWORD="$value" ;;
            *) warn "ignoring unknown secret key '$key'" ;;
        esac
    }
    if [[ -n "$SECRETS_FILE" ]]; then
        [[ -f "$SECRETS_FILE" && ! -L "$SECRETS_FILE" ]] || die "secrets file $SECRETS_FILE not found"
        local mode owner
        mode="$(stat -c %a -- "$SECRETS_FILE")"
        owner="$(stat -c %u -- "$SECRETS_FILE")"
        [[ "$mode" == 600 || "$mode" == 400 ]] || die "secrets file $SECRETS_FILE must be mode 600 (is $mode)"
        [[ "$owner" == 0 ]] || die "secrets file $SECRETS_FILE must be owned by root"
        while IFS= read -r line || [[ -n "$line" ]]; do parse_secret_line "$line"; done <"$SECRETS_FILE"
    fi
    if [[ $SECRETS_STDIN -eq 1 ]]; then
        while IFS= read -r line || [[ -n "$line" ]]; do parse_secret_line "$line"; done
    fi
    [[ -n "$DB_PASSWORD" ]] || DB_PASSWORD="${HOMEPAGE_DB_PASSWORD-}"
    [[ -n "$ADMIN_PASSWORD" ]] || ADMIN_PASSWORD="${HOMEPAGE_ADMIN_PASSWORD-}"
    unset HOMEPAGE_DB_PASSWORD HOMEPAGE_ADMIN_PASSWORD
    unset -f parse_secret_line
}

# ------------------------------------------------------------------------------------------------ checks

check_system() {
    [[ $EUID -eq 0 ]] || die "run as root: sudo $0"
    [[ -r /etc/os-release ]] || die "/etc/os-release not found"
    local id="" version=""
    while IFS='=' read -r k v; do
        v="${v%\"}"; v="${v#\"}"
        case "$k" in ID) id="$v" ;; VERSION_ID) version="$v" ;; esac
    done </etc/os-release
    [[ "$id" == ubuntu ]] || die "Ubuntu 24.04 LTS or newer is required (found '$id')"
    local major="${version%%.*}" minor="${version#*.}"
    [[ "$major" =~ ^[0-9]+$ && "$minor" =~ ^[0-9]+$ ]] || die "cannot read the Ubuntu version '$version'"
    if ((10#$major < 24 || (10#$major == 24 && 10#$minor < 4))); then die "Ubuntu 24.04 LTS or newer is required (found $version)"; fi
    case "$(uname -m)" in
        x86_64) ARCH=x64 ;;
        aarch64) ARCH=arm64 ;;
        *) die "unsupported CPU architecture $(uname -m) (x64 or arm64)" ;;
    esac
    command -v systemctl >/dev/null || die "systemd is required"
    if [[ $UNATTENDED -eq 0 && ! -r /dev/tty ]]; then die "no terminal: use --unattended"; fi
}

# ------------------------------------------------------------------------------------------------ packages

install_packages() {
    info "Installing packages"
    export DEBIAN_FRONTEND=noninteractive
    apt-get update -q
    local pkgs=(ca-certificates curl tar openssl tzdata)
    # A PostgreSQL client is already there when this machine runs PostgreSQL (shared with other applications): keep it
    # as it is. ensure_pg_client adds a matching pg_dump later when the server needs a newer one.
    command -v psql >/dev/null 2>&1 || pkgs+=(postgresql-client)
    local icu
    icu="$(apt-cache pkgnames libicu 2>/dev/null | grep -E '^libicu[0-9]+$' | sort -V | tail -n1 || true)"
    [[ -n "$icu" ]] || die "no ICU library package (libicuNN) found"
    pkgs+=("$icu")
    local ssl=""
    for p in libssl3t64 libssl3; do if apt_has_candidate "$p"; then ssl="$p"; break; fi; done
    [[ -n "$ssl" ]] || die "no OpenSSL 3 library package found"
    pkgs+=("$ssl")
    # Npgsql probes GSSAPI at start and logs an error when the library is missing (it replaced libldap2 here).
    apt_has_candidate libgssapi-krb5-2 && pkgs+=(libgssapi-krb5-2)
    # --no-upgrade: packages that are already installed (libraries other applications use too) stay as they are.
    apt-get install -y -q --no-install-recommends --no-upgrade "${pkgs[@]}"
}

# ------------------------------------------------------------------------------------------------ package source

sha256_of() { sha256sum -- "$1" | cut -d' ' -f1; }

verify_against_sums() { # verify_against_sums FILE SUMSFILE (before anything from the package is used)
    local name expected actual
    name="$(basename "$1")"
    expected="$(awk -v n="$name" '{ f = $2; sub(/^\*/, "", f); sub(/^\.\//, "", f)
        if (f == n && length($1) == 64 && $1 ~ /^[0-9a-fA-F]+$/) { print tolower($1); exit } }' "$2")"
    [[ -n "$expected" ]] || die "$(basename "$2") has no entry for $name"
    actual="$(sha256_of "$1")"
    [[ "$actual" == "$expected" ]] || die "SHA-256 mismatch for $name"
    info "SHA-256 of $name verified"
}

github_download() { # github_download URL DEST
    curl -fsSL --proto '=https' --tlsv1.2 -A "$USER_AGENT" -o "$2" -- "$1" || die "download failed: $1"
}

obtain_package() {
    WORK="$(mktemp -d /tmp/homepage-install.XXXXXX)"
    chmod 700 "$WORK"
    if [[ -z "$PACKAGE" && -z "$VERSION_ARG" && -z "$GITHUB_REPOSITORY" && $UNATTENDED -eq 0 ]]; then
        tty_read PACKAGE "Path to Homepage.Server-<version>-linux-$ARCH.tar.gz"
    fi
    if [[ -n "$PACKAGE" ]]; then
        [[ -f "$PACKAGE" ]] || die "package $PACKAGE not found"
        local base
        base="$(basename "$PACKAGE")"
        [[ "$base" =~ ^Homepage\.Server-([0-9]{1,5}\.[0-9]{1,5}\.[0-9]{1,5})-linux-(x64|arm64)\.tar\.gz$ ]] ||
            die "the package must be named Homepage.Server-<x.y.z>-linux-<x64|arm64>.tar.gz"
        PKG_VERSION="${BASH_REMATCH[1]}"
        [[ "${BASH_REMATCH[2]}" == "$ARCH" ]] || die "the package is for ${BASH_REMATCH[2]}, this machine is $ARCH"
        PKG_FILE="$WORK/$base"
        cp -- "$PACKAGE" "$PKG_FILE"
        local sums
        sums="$(dirname "$PACKAGE")/SHA256SUMS.txt"
        if [[ -f "$sums" ]]; then
            verify_against_sums "$PKG_FILE" "$sums"
        else
            warn "no SHA256SUMS.txt next to the package; its hash is not checked"
        fi
        bootstrap_functions "$PKG_FILE"
        return 0
    fi

    [[ -n "$GITHUB_REPOSITORY" ]] || die "updates from GitHub are not configured yet: use --package FILE (a tarball from build/build-server.sh)"
    local version="$VERSION_ARG"
    if [[ -z "$version" ]]; then
        local json
        json="$(curl -fsSL --proto '=https' -A "$USER_AGENT" -H 'Accept: application/vnd.github+json' \
            "https://api.github.com/repos/$GITHUB_REPOSITORY/releases/latest")" || die "cannot read the latest release"
        version="$(printf '%s' "$json" | sed -n 's/.*"tag_name"[[:space:]]*:[[:space:]]*"v\{0,1\}\([^"]*\)".*/\1/p' | head -n1)"
    fi
    [[ "$version" =~ ^[0-9]{1,5}\.[0-9]{1,5}\.[0-9]{1,5}$ ]] || die "version '$version' is not a plain x.y.z"
    PKG_VERSION="$version"
    local name="Homepage.Server-$version-linux-$ARCH.tar.gz" base="https://github.com/$GITHUB_REPOSITORY/releases/download/v$version"
    info "Downloading $name"
    github_download "$base/$name" "$WORK/$name"
    github_download "$base/SHA256SUMS.txt" "$WORK/SHA256SUMS.txt"
    PKG_FILE="$WORK/$name"
    verify_against_sums "$PKG_FILE" "$WORK/SHA256SUMS.txt"
    bootstrap_functions "$PKG_FILE"
}

# The validation and unpack functions live in update.sh (one implementation). Take them from the package.
bootstrap_functions() {
    local pkg="$1" dir="$WORK/bootstrap" m
    mkdir -p "$dir"
    for m in ./deploy/linux/update.sh deploy/linux/update.sh; do
        if tar -xzf "$pkg" -C "$dir" --no-same-owner "$m" 2>/dev/null; then break; fi
    done
    local f="$dir/deploy/linux/update.sh"
    [[ -f "$f" && ! -L "$f" ]] || die "the package has no deploy/linux/update.sh"
    # shellcheck source=update.sh
    source "$f"
    set -Eeuo pipefail
    trap 'die "install.sh failed at line $LINENO"' ERR
}

# ------------------------------------------------------------------------------------------------ layout and user

create_user() {
    if ! id -u "$SERVICE_USER" >/dev/null 2>&1; then
        info "Creating system user $SERVICE_USER"
        useradd --system --user-group --home-dir /nonexistent --no-create-home --shell /usr/sbin/nologin "$SERVICE_USER"
    fi
}

create_layout() {
    info "Preparing $INSTALL_ROOT"
    install -d -o root -g root -m 755 "$INSTALL_ROOT" "$INSTALL_ROOT/releases" "$INSTALL_ROOT/bin"
    install -d -o root -g "$SERVICE_USER" -m 750 "$INSTALL_ROOT/config"
    [[ ! -L "$INSTALL_ROOT/data" ]] || die "$INSTALL_ROOT/data is a symbolic link"
    install -d -o "$SERVICE_USER" -g "$SERVICE_USER" -m 700 "$INSTALL_ROOT/data"
    # The service user owns data/ and could have put links there; install -d follows them, so the folders below
    # are made by the service user itself, never by root.
    as_service_user install -d -m 700 "$INSTALL_ROOT/data/media" "$INSTALL_ROOT/data/keys" \
        "$INSTALL_ROOT/data/updates" "$INSTALL_ROOT/data/themes" ||
        warn "could not create every folder in $INSTALL_ROOT/data (is one of them a link?)"
    secure_backups_dir
}

# backups/ (archives from backup.sh) and backups/pre-update/ (dumps from update.sh) are root:root 700: root writes
# there, so the service user must not be able to plant links. Should backups/ have another owner (set by hand), the
# folder is taken over first, then everything in it. Links and other non-regular entries in it are left as they are
# and never followed (chown -h, find -type f/d).
secure_backups_dir() {
    local top="$INSTALL_ROOT/backups" pre="$INSTALL_ROOT/backups/pre-update"
    [[ ! -L "$top" ]] || die "$top is a symbolic link: backups must be in a real folder owned by root. Move it away (mv $top $top.old; mkdir -m 700 $top) and run this again"
    [[ -e "$top" ]] || install -d -o root -g root -m 700 "$top"
    [[ -d "$top" ]] || die "$top is not a folder"
    chown -h root:root -- "$top"
    chmod 700 -- "$top"
    chown -hR root:root -- "$top"
    find "$top" -mindepth 1 -type d -exec chmod 700 {} +
    find "$top" -mindepth 1 -type f -exec chmod 600 {} +
    # Only the service user can have planted a link here (root never does): replace it with a folder.
    if [[ -L "$pre" ]]; then rm -f -- "$pre"; fi
    [[ -e "$pre" ]] || install -d -o root -g root -m 700 "$pre"
    [[ -d "$pre" && ! -L "$pre" && "$(stat -c '%u %a' -- "$pre")" == "0 700" ]] || die "$pre is not a root-only folder"
}

fix_permissions() { # repair: owners and modes as install.sh sets them, without touching contents
    create_layout
    chown -hR "$SERVICE_USER:$SERVICE_USER" "$INSTALL_ROOT/data"
    chmod 700 "$INSTALL_ROOT/data"
    local f
    for f in "$CONFIG_FILE" "$INSTALL_ROOT/config/site.pem" "$INSTALL_ROOT/config/site.key"; do
        [[ -f "$f" ]] && { chown root:"$SERVICE_USER" "$f"; chmod 640 "$f"; }
    done
    for f in "$UPDATE_ENV" "$STATE_FILE"; do [[ -f "$f" ]] && { chown root:root "$f"; chmod 600 "$f"; }; done
    find "$INSTALL_ROOT/releases" -mindepth 1 -maxdepth 1 -type d -exec chown -hR root:root {} + -exec chmod -R go-w {} +
    for f in "$INSTALL_ROOT/bin/update.sh" "$INSTALL_ROOT/bin/backup.sh"; do
        [[ -f "$f" && ! -L "$f" ]] && { chown root:root "$f"; chmod 755 "$f"; }
    done
    for f in "${UNITS[@]}"; do [[ -f "$UNIT_DIR/$f" && ! -L "$UNIT_DIR/$f" ]] && { chown root:root "$UNIT_DIR/$f"; chmod 644 "$UNIT_DIR/$f"; }; done
    return 0
}

unpack_release() {
    local release="$INSTALL_ROOT/releases/$PKG_VERSION" staging="$INSTALL_ROOT/releases/.staging-$PKG_VERSION"
    info "Unpacking $PKG_VERSION into $release"
    rm -rf -- "$staging"
    HP_ERROR=""
    hp_safe_unpack "$PKG_FILE" "$staging" || { rm -rf -- "$staging"; die "unsafe or broken package: $HP_ERROR"; }
    [[ -x "$staging/Homepage.Server" ]] || { rm -rf -- "$staging"; die "the package has no Homepage.Server"; }
    rm -rf -- "$release"
    mv -T -- "$staging" "$release"
}

# install_deploy_files DIR: root-owned update.sh, backup.sh and the systemd units, which root runs. DIR is always a
# package the admin gave this script (--package, or --version downloaded and hash-checked), unpacked by this run:
# never current/ or releases/, whose content a self-update staged by the service user can decide.
install_deploy_files() {
    local src="$1/deploy/linux" u f
    for f in update.sh "${UNITS[@]}"; do
        [[ -f "$src/$f" && ! -L "$src/$f" ]] || die "the package has no deploy/linux/$f"
    done
    [[ ! -L "$src/backup.sh" ]] || die "deploy/linux/backup.sh in the package is a link"
    install -o root -g root -m 755 "$src/update.sh" "$INSTALL_ROOT/bin/update.sh"
    if [[ -f "$src/backup.sh" ]]; then install -o root -g root -m 755 "$src/backup.sh" "$INSTALL_ROOT/bin/backup.sh"; fi
    for u in "${UNITS[@]}"; do install -o root -g root -m 644 "$src/$u" "$UNIT_DIR/$u"; done
    systemctl daemon-reload
}

missing_deploy_files() { # prints the installed root-run files that are missing (repair without a package)
    local f
    for f in "$INSTALL_ROOT/bin/update.sh" "$INSTALL_ROOT/bin/backup.sh" "${UNITS[@]/#/$UNIT_DIR/}"; do
        [[ -f "$f" && ! -L "$f" ]] || printf '%s\n' "$f"
    done
}

as_service_user() { # as_service_user CMD...: runs a command as the service user with a clean environment
    runuser -u "$SERVICE_USER" -- env -i PATH=/usr/sbin:/usr/bin:/sbin:/bin LANG=C.UTF-8 "$@"
}

put_as_service_user() { # put_as_service_user DEST <CONTENT: the service user writes DEST (temp file + rename), so
    # root never opens a path in the service user's folders for writing
    # shellcheck disable=SC2016 # expanded by the inner shell
    as_service_user /bin/sh -c 'umask 077; t="$(mktemp "$1.XXXXXX")" || exit 1
        if cat >"$t" && mv -fT -- "$t" "$1"; then exit 0; fi; rm -f -- "$t"; exit 1' sh "$1"
}

# ------------------------------------------------------------------------------------------------ database

pg_query() { # pg_query SQL: against the configured database with the configured role
    PGPASSWORD="$DB_PASSWORD" PGSSLMODE="$DB_SSLMODE" PGCONNECT_TIMEOUT=10 psql -X -At -v ON_ERROR_STOP=1 \
        -h "$DB_HOST" -p "$DB_PORT" -U "$DB_USER" -d "$DB_NAME" -c "$1" 2>&1
}

pg_test_connection() { # sets PG_SERVER_NUM; returns 1 with a message on failure
    local out
    if ! out="$(pg_query "SELECT current_setting('server_version_num') || '|' || (SELECT pg_catalog.pg_get_userbyid(datdba) = current_user FROM pg_database WHERE datname = current_database())::text")"; then
        printf 'Connection failed: %s\n' "$out" >&2
        return 1
    fi
    PG_SERVER_NUM="${out%%|*}"
    PG_IS_OWNER="${out#*|}"
    return 0
}

ensure_pg_client() { # ensure_pg_client MAJOR: pg_dump must not be older than the server
    local major="$1" have=0
    if [[ -x "/usr/lib/postgresql/$major/bin/pg_dump" ]]; then have=1; fi
    if [[ $have -eq 0 ]]; then
        info "PostgreSQL $major server: adding the PostgreSQL project's apt repository for a matching client"
        apt-get install -y -q --no-install-recommends postgresql-common
        /usr/share/postgresql-common/pgdg/apt.postgresql.org.sh -y
        apt-get install -y -q --no-install-recommends "postgresql-client-$major"
    fi
    PG_BIN="/usr/lib/postgresql/$major/bin"
    [[ -x "$PG_BIN/pg_dump" ]] || die "pg_dump for PostgreSQL $major not found"
}

setup_database() {
    if [[ -z "$DB_MODE" ]]; then
        if [[ $UNATTENDED -eq 1 ]]; then die "missing --db=local|existing"; fi
        echo "Database: 1) PostgreSQL on this machine (installed if missing; an existing one is shared)  2) a PostgreSQL server elsewhere"
        local choice
        tty_read choice "Choose 1 or 2" 1
        case "$choice" in 1) DB_MODE=local ;; 2) DB_MODE=existing ;; *) die "invalid choice" ;; esac
    fi
    case "$DB_MODE" in
        local) setup_local_database ;;
        existing) setup_existing_database ;;
        *) die "--db must be local or existing" ;;
    esac
}

# ---- PostgreSQL on this machine. It may already serve other applications, so Homepage only ever adds its own
# role and database: it never installs, upgrades, reconfigures or restarts an existing server, and never alters or drops
# a role or database it did not create.
local_pg_clusters() { # "version port status" per cluster of this machine (Ubuntu/Debian layout); empty when none
    command -v pg_lsclusters >/dev/null 2>&1 || return 0
    pg_lsclusters --no-header 2>/dev/null | awk 'NF >= 4 {print $1, $3, $4}'
}
port_in_use() { # port_in_use PORT: something listens on it (TCP)
    command -v ss >/dev/null 2>&1 && ss -Hltn "sport = :$1" 2>/dev/null | grep -q .
}
pg_local() { # pg_local SQL: as the postgres superuser over the local socket, on the chosen cluster ($DB_PORT)
    runuser -u postgres -- psql -X -At -q -v ON_ERROR_STOP=1 -p "$DB_PORT" -d postgres -c "$1"
}
pg_name_free() { # pg_name_free NAME: no role and no database of that name
    [[ "$(pg_local "SELECT (SELECT count(*) FROM pg_roles WHERE rolname = '$1') + (SELECT count(*) FROM pg_database WHERE datname = '$1')")" == 0 ]]
}

setup_local_database() {
    local clusters online
    clusters="$(local_pg_clusters)"
    if [[ -z "$clusters" ]]; then
        port_in_use "${DB_PORT:-5432}" && die "port ${DB_PORT:-5432} is in use, but there is no PostgreSQL cluster from Ubuntu's packages (pg_lsclusters). If it is a PostgreSQL server, use --db=existing with a role and database made for Homepage."
        info "Installing PostgreSQL on this machine"
        apt-get install -y -q --no-install-recommends postgresql
        systemctl enable --now postgresql
        clusters="$(local_pg_clusters)"
        [[ -n "$clusters" ]] || die "PostgreSQL was installed but no cluster was found (pg_lsclusters)"
    else
        info "PostgreSQL is already installed: it is shared as it is (Homepage only adds its own role and database)"
    fi
    online="$(awk '$3 == "online" {print $2}' <<<"$clusters")"
    if [[ -n "$DB_PORT" ]]; then
        grep -qx -- "$DB_PORT" <<<"$online" || die "no running PostgreSQL cluster on port $DB_PORT (see pg_lsclusters)"
    elif grep -qx 5432 <<<"$online"; then
        DB_PORT=5432
    elif [[ -n "$online" && "$(wc -l <<<"$online")" -eq 1 ]]; then
        DB_PORT="$online"
    elif [[ -z "$online" ]]; then
        die "no PostgreSQL cluster is running (see pg_lsclusters); start it, then run the installer again"
    else
        die "several PostgreSQL clusters are running (ports $(tr '\n' ' ' <<<"$online")); choose one with --db-port"
    fi
    DB_HOST=127.0.0.1 DB_SSLMODE=prefer

    local num
    num="$(pg_local 'SHOW server_version_num')"
    [[ "$num" =~ ^[0-9]+$ ]] || die "cannot query the PostgreSQL cluster on port $DB_PORT"
    ((num >= 160000)) || die "PostgreSQL 16 or newer is required (the cluster on port $DB_PORT is $num); it is not upgraded for you"
    local listen
    listen="$(pg_local 'SHOW listen_addresses')"
    [[ "$listen" == localhost || "$listen" == 127.0.0.1 || "$listen" == "127.0.0.1,::1" ]] ||
        warn "PostgreSQL listens on '$listen', not only on localhost"

    # A new role and database. A name given with --db-name/--db-user must be free; otherwise the first free one of
    # homepage, homepage_2, ... is used for both.
    local name_re='^[a-z_][a-z0-9_]{0,62}$'
    if [[ -n "$DB_NAME" || -n "$DB_USER" ]]; then
        DB_NAME="${DB_NAME:-${DB_USER}}" DB_USER="${DB_USER:-${DB_NAME}}"
        [[ "$DB_NAME" =~ $name_re && "$DB_USER" =~ $name_re ]] || die "database and role names: lowercase letters, digits and _"
        [[ "$DB_NAME" != postgres && "$DB_NAME" != template0 && "$DB_NAME" != template1 && "$DB_USER" != postgres ]] ||
            die "'$DB_NAME'/'$DB_USER' are PostgreSQL's own names"
        [[ "$(pg_local "SELECT count(*) FROM pg_database WHERE datname = '$DB_NAME'")" == 0 ]] ||
            die "the database '$DB_NAME' already exists on port $DB_PORT; Homepage never takes over an existing database. Choose another --db-name."
        [[ "$(pg_local "SELECT count(*) FROM pg_roles WHERE rolname = '$DB_USER'")" == 0 ]] ||
            die "the role '$DB_USER' already exists on port $DB_PORT; Homepage never takes over an existing role. Choose another --db-user."
    else
        local n candidate=homepage
        for n in $(seq 2 99); do
            pg_name_free "$candidate" && break
            candidate="homepage_$n"
        done
        pg_name_free "$candidate" || die "no free role/database name homepage ... homepage_99 on port $DB_PORT"
        [[ "$candidate" == homepage ]] ||
            info "A role or database named 'homepage' already exists (another installation?); using '$candidate'"
        DB_NAME="$candidate" DB_USER="$candidate"
    fi

    info "Creating the role '$DB_USER' and the database '$DB_NAME' (PostgreSQL on port $DB_PORT)"
    DB_PASSWORD="$(random_alnum 32)"
    # SQL on stdin, never on the command line. The password is alphanumeric and the names match $name_re, so neither
    # needs escaping. No IF EXISTS / ALTER: the names were checked to be free, and an existing object makes this fail.
    runuser -u postgres -- psql -X -q -v ON_ERROR_STOP=1 -p "$DB_PORT" -d postgres <<SQL || die "could not create the role and database"
CREATE ROLE "$DB_USER" WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '$DB_PASSWORD';
CREATE DATABASE "$DB_NAME" OWNER "$DB_USER";
REVOKE ALL ON DATABASE "$DB_NAME" FROM PUBLIC;
SQL
    DB_CREATED=1
    if ! pg_test_connection; then
        # Remove only what this run just created, so a second attempt starts clean.
        pg_local "DROP DATABASE IF EXISTS \"$DB_NAME\"" >/dev/null 2>&1 || true
        pg_local "DROP ROLE IF EXISTS \"$DB_USER\"" >/dev/null 2>&1 || true
        die "cannot connect to 127.0.0.1:$DB_PORT as '$DB_USER' with a password. If this PostgreSQL's pg_hba.conf does not allow password logins from localhost, add a line such as 'host $DB_NAME $DB_USER 127.0.0.1/32 scram-sha-256', reload PostgreSQL, and run the installer again."
    fi
    ensure_pg_client "$((PG_SERVER_NUM / 10000))"
}

setup_existing_database() {
    while true; do
        ask DB_HOST "PostgreSQL host" ""
        ask DB_PORT "Port" 5432
        ask DB_NAME "Database" homepage
        ask DB_USER "User" homepage
        ask DB_SSLMODE "SSL mode (disable/allow/prefer/require/verify-ca/verify-full)" require
        [[ $UNATTENDED -eq 0 || -n "$DB_HOST" ]] || die "missing --db-host"
        npgsql_sslmode "$DB_SSLMODE" >/dev/null || die "invalid SSL mode '$DB_SSLMODE'"
        [[ "$DB_PORT" =~ ^[0-9]+$ ]] || die "invalid port '$DB_PORT'"
        if [[ -z "$DB_PASSWORD" ]]; then
            [[ $UNATTENDED -eq 0 ]] || die "missing DB_PASSWORD (secrets file, stdin or HOMEPAGE_DB_PASSWORD)"
            tty_secret DB_PASSWORD "Password for $DB_USER"
        fi
        info "Testing the connection to $DB_HOST:$DB_PORT/$DB_NAME"
        if pg_test_connection; then break; fi
        [[ $UNATTENDED -eq 0 ]] || die "cannot connect to the database"
        ask_yes_no "Try again with other values?" y || die "cannot connect to the database"
        DB_HOST="" DB_PORT="" DB_NAME="" DB_USER="" DB_SSLMODE="" DB_PASSWORD=""
    done
    [[ "$PG_SERVER_NUM" =~ ^[0-9]+$ ]] || die "unexpected server version '$PG_SERVER_NUM'"
    ((PG_SERVER_NUM >= 160000)) || die "PostgreSQL 16 or newer is required (server is $PG_SERVER_NUM)"
    [[ "$PG_IS_OWNER" == true ]] || die "the role $DB_USER must own the database $DB_NAME (the updater drops and restores it on rollback)"
    ensure_pg_client "$((PG_SERVER_NUM / 10000))"
}

# ------------------------------------------------------------------------------------------------ config, HTTPS

# ---- Site mode: https (Kestrel serves HTTPS with a certificate, on 0.0.0.0 by default) or http (plain HTTP, no
# certificate, on 127.0.0.1 by default: for Caddy or nginx on this machine in front, with Let's Encrypt)
site_scheme() { if [[ "$SITE_MODE" == http ]]; then echo http; else echo https; fi; }
default_bind() { if [[ "$SITE_MODE" == http ]]; then echo 127.0.0.1; else echo 0.0.0.0; fi; }
valid_port() { [[ "${1-}" =~ ^[1-9][0-9]{0,4}$ ]] && ((10#$1 <= 65535)); }
# Loopback or wildcard only: /healthz answers only from loopback, so update.sh must reach Kestrel on a loopback
# address (a site bound to one specific public address would fail every health check and roll back every update).
valid_bind() { [[ "${1-}" == 127.0.0.1 || "${1-}" == 0.0.0.0 || "${1-}" == ::1 || "${1-}" == :: ]]; }
bind_is_loopback() { [[ "$SITE_BIND" == 127.0.0.1 || "$SITE_BIND" == ::1 ]]; }
url_host() { if [[ "$1" == *:* ]]; then echo "[$1]"; else echo "$1"; fi; } # IPv6 literals in brackets
health_host() { if [[ "$SITE_BIND" == *:* ]]; then echo '[::1]'; else echo 127.0.0.1; fi; }
health_url() { echo "$(site_scheme)://$(health_host):$SITE_PORT/healthz"; }
kestrel_url() { echo "$(site_scheme)://$(url_host "$SITE_BIND"):$SITE_PORT"; }
read_site_state() { # repair / upgrade: the values install.sh recorded, or the defaults
    SITE_MODE="$(read_state SITE_MODE || echo https)"
    [[ "$SITE_MODE" == http ]] || SITE_MODE=https
    SITE_BIND="$(read_state SITE_BIND || default_bind)"
    valid_bind "$SITE_BIND" || SITE_BIND="$(default_bind)"
    SITE_PORT="$(read_state SITE_PORT || true)"
    if ! valid_port "$SITE_PORT"; then if [[ "$SITE_MODE" == http ]]; then SITE_PORT=5080; else SITE_PORT=443; fi; fi
}
collect_site_mode() { # asked before anything is changed on the machine
    if [[ -z "$SITE_MODE" ]]; then
        if [[ $UNATTENDED -eq 0 ]]; then
            cat >/dev/tty <<'EOF'
How should the site be served?
  1) https  Kestrel serves HTTPS itself on all addresses (port 443), with your certificate or a self-signed one.
            Such a certificate is not renewed automatically.
  2) http   plain HTTP on 127.0.0.1 only (port 5080), meant for running behind Caddy or nginx on this machine,
            which serve HTTPS with Let's Encrypt certificates and renew them. The usual choice for a public website.
            (--bind 0.0.0.0 makes Kestrel reachable from other machines.)
EOF
            local choice
            tty_read choice "Choose 1 or 2" 1
            case "$choice" in 1) SITE_MODE=https ;; 2) SITE_MODE=http ;; *) die "invalid choice" ;; esac
        else
            SITE_MODE=https
        fi
    fi
    [[ "$SITE_MODE" == https || "$SITE_MODE" == http ]] || die "--mode must be https or http"
    [[ -n "$SITE_BIND" ]] || SITE_BIND="$(default_bind)"
    valid_bind "$SITE_BIND" || die "--bind must be 127.0.0.1, 0.0.0.0, ::1 or :: (the update health check reaches the site on loopback)"
    if [[ "$SITE_MODE" == http ]]; then
        ask SITE_PORT "Site HTTP port" 5080
        [[ -z "$CERT_FILE" ]] || warn "--cert is ignored with --mode=http"
        if ! bind_is_loopback; then
            warn "the site will use plain HTTP on $SITE_BIND: sign-in passwords travel unencrypted unless an HTTPS proxy is in front of it"
        fi
    else
        ask SITE_PORT "Site HTTPS port" 443
    fi
    valid_port "$SITE_PORT" || die "invalid port '$SITE_PORT'"
}

write_config() {
    [[ ! -e "$CONFIG_FILE" ]] || { info "Keeping the existing $CONFIG_FILE"; return 0; }
    info "Writing $CONFIG_FILE"
    local cs
    cs="Host=$(cs_value "$DB_HOST");Port=$DB_PORT;Database=$(cs_value "$DB_NAME");Username=$(cs_value "$DB_USER");Password=$(cs_value "$DB_PASSWORD");SSL Mode=$(npgsql_sslmode "$DB_SSLMODE")"
    local tmp site_cert
    if [[ "$SITE_MODE" == http ]]; then
        site_cert='"Certificate": "", "CertificateKey": ""'
    else
        site_cert="\"Certificate\": \"$INSTALL_ROOT/config/site.pem\", \"CertificateKey\": \"$INSTALL_ROOT/config/site.key\""
    fi
    tmp="$(mktemp "$INSTALL_ROOT/config/.appsettings.XXXXXX")"
    (umask 077; cat >"$tmp" <<JSON
{
  "ConnectionStrings": { "Default": "$(json_escape "$cs")" },
  "Site":      { "Urls": "$(json_escape "$(kestrel_url)")", $site_cert,
                 "ForwardedHeaders": { "Enabled": false, "KnownProxies": [], "KnownNetworks": [] } },
  "Paths":     { "Data": "$INSTALL_ROOT/data", "Backups": "$INSTALL_ROOT/backups" },
  "Updates":   { "GitHubRepository": "$(json_escape "$GITHUB_REPOSITORY")", "Channel": "stable", "CheckIntervalHours": 24, "GitHubToken": "" }
}
JSON
    )
    chown root:"$SERVICE_USER" "$tmp"
    chmod 640 "$tmp"
    mv -T -- "$tmp" "$CONFIG_FILE"
}

write_update_env() { # root-only copy of the database settings for update.sh (pg_dump / pg_restore)
    if [[ -e "$UPDATE_ENV" ]]; then info "Keeping the existing $UPDATE_ENV"; return 0; fi
    local tmp
    tmp="$(mktemp "$INSTALL_ROOT/config/.update-env.XXXXXX")"
    (umask 077; printf '%s\n' "# Used by $INSTALL_ROOT/bin/update.sh. root only." \
        "PGHOST=$DB_HOST" "PGPORT=$DB_PORT" "PGDATABASE=$DB_NAME" "PGUSER=$DB_USER" "PGPASSWORD=$DB_PASSWORD" \
        "PGSSLMODE=$DB_SSLMODE" "PG_BIN=$PG_BIN" "HEALTH_URL=$(health_url)" >"$tmp")
    chown root:root "$tmp"
    chmod 600 "$tmp"
    mv -T -- "$tmp" "$UPDATE_ENV"
}

write_state() {
    local tmp
    tmp="$(mktemp "$INSTALL_ROOT/config/.state.XXXXXX")"
    printf '%s\n' "DB_MODE=$DB_MODE" "DB_NAME=$DB_NAME" "DB_USER=$DB_USER" "DB_PORT=$DB_PORT" "DB_CREATED=$DB_CREATED" \
        "SERVER_NAME=$SERVER_NAME" \
        "SITE_PORT=$SITE_PORT" "SITE_MODE=$SITE_MODE" "SITE_BIND=$SITE_BIND" "INSTALLED_AT=$(date -u +%Y-%m-%dT%H:%M:%SZ)" >"$tmp"
    chown root:root "$tmp"
    chmod 600 "$tmp"
    mv -T -- "$tmp" "$STATE_FILE"
}

setup_certificate() {
    if [[ "$SITE_MODE" == http ]]; then info "Site over plain HTTP: no certificate"; return 0; fi
    local pem="$INSTALL_ROOT/config/site.pem" key="$INSTALL_ROOT/config/site.key"
    if [[ -f "$pem" && -f "$key" ]]; then info "Keeping the existing site certificate"; return 0; fi
    if [[ -z "$CERT_FILE" && $UNATTENDED -eq 0 ]]; then
        if ask_yes_no "Use an existing certificate for the site (otherwise a self-signed one is made)?" n; then
            tty_read CERT_FILE "Certificate file (PEM)"
            tty_read CERT_KEY_FILE "Private key file (PEM)"
        fi
    fi
    if [[ -n "$CERT_FILE" ]]; then
        [[ -f "$CERT_FILE" && -f "$CERT_KEY_FILE" ]] || die "certificate or key file not found"
        openssl x509 -in "$CERT_FILE" -noout 2>/dev/null || die "$CERT_FILE is not a PEM certificate"
        openssl pkey -in "$CERT_KEY_FILE" -noout 2>/dev/null || die "$CERT_KEY_FILE is not an unencrypted PEM private key"
        [[ "$(openssl x509 -in "$CERT_FILE" -noout -pubkey)" == "$(openssl pkey -in "$CERT_KEY_FILE" -pubout)" ]] ||
            die "the key does not belong to the certificate"
        install -o root -g "$SERVICE_USER" -m 640 "$CERT_FILE" "$pem"
        install -o root -g "$SERVICE_USER" -m 640 "$CERT_KEY_FILE" "$key"
        info "Installed the site certificate"
        return 0
    fi
    info "Generating a self-signed certificate for $SERVER_NAME"
    local san="DNS:$SERVER_NAME,DNS:localhost,IP:127.0.0.1" ip
    for ip in $(hostname -I 2>/dev/null || true); do
        [[ "$ip" =~ ^[0-9.]+$ || "$ip" =~ ^[0-9a-fA-F:]+$ ]] && san+=",IP:$ip"
    done
    (umask 077; openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:P-256 -nodes -days 825 \
        -subj "/CN=$SERVER_NAME" -addext "subjectAltName=$san" -addext "extendedKeyUsage=serverAuth" \
        -keyout "$key" -out "$pem" 2>/dev/null) || die "openssl could not create the certificate"
    chown root:"$SERVICE_USER" "$pem" "$key"
    chmod 640 "$pem" "$key"
}

# ------------------------------------------------------------------------------------------------ admin account, services, firewall, summary

collect_admin_account() { # asked before anything is changed on the machine
    ask ADMIN_USER "First local account, an administrator (user name)" admin
    [[ -n "$ADMIN_USER" && "$ADMIN_USER" != -* && "$ADMIN_USER" =~ ^[A-Za-z0-9._@-]{1,64}$ ]] ||
        die "invalid user name '$ADMIN_USER' (letters, digits, . _ @ -)"
    if [[ -z "$ADMIN_PASSWORD" ]]; then
        [[ $UNATTENDED -eq 0 ]] || die "missing ADMIN_PASSWORD (secrets file, stdin or HOMEPAGE_ADMIN_PASSWORD)"
        while true; do
            tty_secret ADMIN_PASSWORD "Password for $ADMIN_USER (at least 8 characters)" confirm
            [[ ${#ADMIN_PASSWORD} -ge 8 ]] && break
            echo "Too short." >/dev/tty
        done
    fi
    [[ ${#ADMIN_PASSWORD} -ge 8 ]] || die "ADMIN_PASSWORD must have at least 8 characters"
    [[ "$ADMIN_PASSWORD" != *$'\n'* ]] || die "ADMIN_PASSWORD must be one line"
}

create_admin_account() {
    info "Creating the local administrator account $ADMIN_USER"
    # The password goes through stdin, never the command line.
    printf '%s\n' "$ADMIN_PASSWORD" | run_server "$INSTALL_ROOT/current" --create-local-account "$ADMIN_USER" ||
        die "creating the local account failed"
}

migrate() {
    info "Applying database migrations"
    run_server "$1" --migrate-only || die "--migrate-only failed"
}

start_services() {
    info "Starting the services"
    systemctl enable homepage.service homepage-update.path
    systemctl restart homepage.service
    systemctl start homepage-update.path
    local i
    for ((i = 0; i < 40; i++)); do
        if curl -fsk --max-time 5 "$(health_url)" >/dev/null 2>&1; then return 0; fi
        sleep 3
    done
    warn "the server did not answer on $(health_url) yet (journalctl -u homepage)"
}

configure_firewall() {
    command -v ufw >/dev/null 2>&1 || return 0
    ufw status 2>/dev/null | head -n1 | grep -q 'Status: active' || return 0
    if bind_is_loopback; then
        # Only a proxy on this machine reaches the site port: there is nothing to open for it.
        [[ "$OPEN_FIREWALL" != yes ]] || info "The site listens on $SITE_BIND only: no firewall rule is needed for port $SITE_PORT"
        info "ufw is active: open the ports of your reverse proxy (usually 80 and 443) yourself"
        return 0
    fi
    local open=no
    case "$OPEN_FIREWALL" in
        yes) open=yes ;;
        no) open=no ;;
        "") if [[ $UNATTENDED -eq 0 ]] && ask_yes_no "ufw is active. Open port $SITE_PORT (site)?" y; then open=yes; fi ;;
        *) die "--open-firewall must be yes or no" ;;
    esac
    if [[ "$open" == yes ]]; then
        ufw allow "$SITE_PORT/tcp" comment 'Homepage site'
    else
        warn "ufw is active: open TCP $SITE_PORT yourself if needed"
    fi
}

print_summary() {
    local version url scheme
    version="$(installed_version)"
    scheme="$(site_scheme)"
    url="$scheme://$SERVER_NAME/"
    if [[ ("$scheme" == http && "$SITE_PORT" != 80) || ("$scheme" == https && "$SITE_PORT" != 443) ]]; then
        url="$scheme://$SERVER_NAME:$SITE_PORT/"
    fi
    if bind_is_loopback && [[ "$scheme" == http ]]; then
        cat <<EOF

Homepage $version is installed.

  Listening on:  $(kestrel_url) (this machine only)
  Logs:          journalctl -u homepage

Put Caddy or nginx in front of it for HTTPS with Let's Encrypt, for example with Caddy:

  $SERVER_NAME {
      reverse_proxy $(url_host "$SITE_BIND"):$SITE_PORT
  }

Then set "Site": { "ForwardedHeaders": { "Enabled": true, ... } } in $CONFIG_FILE
(restart: systemctl restart homepage), and block /healthz in the proxy.

EOF
    elif bind_is_loopback; then
        cat <<EOF

Homepage $version is installed.

  Listening on:  $(kestrel_url) (this machine only)
  Logs:          journalctl -u homepage

EOF
    else
        cat <<EOF

Homepage $version is installed.

  Site:  $url
  Logs:  journalctl -u homepage

EOF
    fi
}

# ------------------------------------------------------------------------------------------------ modes

do_install() {
    ask SERVER_NAME "Server DNS name (used by browsers)" "$(hostname -f 2>/dev/null || hostname)"
    [[ "$SERVER_NAME" =~ ^[A-Za-z0-9.-]{1,253}$ ]] || die "invalid server name '$SERVER_NAME'"
    collect_site_mode
    # Everything that can be missing in unattended mode is checked before the machine is changed.
    if [[ $UNATTENDED -eq 1 ]]; then
        [[ -n "$PACKAGE" || -n "$GITHUB_REPOSITORY" ]] || die "missing --package FILE (GitHub releases are not configured yet)"
        [[ "$DB_MODE" == local || "$DB_MODE" == existing ]] || die "missing or invalid --db=local|existing"
        if [[ "$DB_MODE" == existing ]]; then
            [[ -n "$DB_HOST" ]] || die "missing --db-host"
            [[ -n "$DB_PASSWORD" ]] || die "missing DB_PASSWORD (secrets file, stdin or HOMEPAGE_DB_PASSWORD)"
        fi
        [[ -z "$CERT_FILE" || -n "$CERT_KEY_FILE" ]] || die "--cert needs --cert-key"
    fi
    collect_admin_account

    install_packages
    obtain_package
    # Database before the layout: a failed connection test leaves nothing in /opt/homepage.
    setup_database
    create_user
    create_layout
    unpack_release
    hp_switch_current "$INSTALL_ROOT/releases/$PKG_VERSION"
    write_config
    write_update_env
    setup_certificate
    # Just unpacked by this run from the admin's package (unpack_release replaces any earlier folder of that name).
    install_deploy_files "$INSTALL_ROOT/releases/$PKG_VERSION"
    migrate "$INSTALL_ROOT/current"
    create_admin_account
    # install.state marks a finished install; without it a re-run starts over instead of offering repair.
    write_state
    INSTALL_DONE=1
    start_services
    configure_firewall
    print_summary
}

do_repair() {
    local version
    version="$(installed_version)" || die "no current release to repair"
    info "Repairing Homepage $version (config and data are kept)"
    SERVER_NAME="$(read_state SERVER_NAME || hostname -f)"
    read_site_state
    # update.sh, backup.sh and the units run as root, so they never come from current/ (a self-update staged by the
    # service user decides what is in there). With --package or --version they are taken from that package, after
    # the same checks as an install; without, the installed copies are kept.
    local missing pkgdir=""
    if [[ -z "$PACKAGE" && -z "$VERSION_ARG" ]]; then
        missing="$(missing_deploy_files)"
        [[ -z "$missing" ]] || die "missing: ${missing//$'\n'/, }. Run install.sh --repair --package FILE (or --version $version) to reinstall them from a verified package"
    fi
    install_packages
    if [[ -n "$PACKAGE" || -n "$VERSION_ARG" ]]; then
        obtain_package
        [[ "$PKG_VERSION" == "$version" ]] ||
            warn "the package is $PKG_VERSION, the running release is $version: only update.sh, backup.sh and the units are taken from it"
        pkgdir="$WORK/pkg"
        HP_ERROR=""
        hp_safe_unpack "$PKG_FILE" "$pkgdir" || die "unsafe or broken package: $HP_ERROR"
    fi
    create_user
    fix_permissions
    if [[ -n "$pkgdir" ]]; then
        install_deploy_files "$pkgdir"
    else
        info "Keeping the installed update.sh, backup.sh and units (pass --package or --version to replace them)"
        systemctl daemon-reload
    fi
    [[ -f "$UPDATE_ENV" ]] || warn "$UPDATE_ENV is missing: self-update will fail until it exists"
    [[ -f "$CONFIG_FILE" ]] || die "$CONFIG_FILE is missing; restore it or run --purge and install again"
    migrate "$INSTALL_ROOT/current"
    start_services
    print_summary
}

do_upgrade() {
    local current
    current="$(installed_version)" || die "no current release to upgrade"
    SERVER_NAME="$(read_state SERVER_NAME || hostname -f)"
    read_site_state
    install_packages
    obtain_package
    hp_version_gt "$PKG_VERSION" "$current" || die "$PKG_VERSION is not newer than the installed $current (downgrades are not offered)"
    [[ -f "$UPDATE_ENV" ]] || die "$UPDATE_ENV is missing; run --repair first"
    info "Upgrading $current to $PKG_VERSION with update.sh (backup, migrations, health check, rollback)"
    fix_permissions

    # New updater and units from the package (chosen by the admin running this script).
    local pkgdir="$WORK/pkg"
    HP_ERROR=""
    hp_safe_unpack "$PKG_FILE" "$pkgdir" || die "unsafe or broken package: $HP_ERROR"
    install_deploy_files "$pkgdir"

    # Stage the package like the server does, then run the update unit and wait for it. data/updates belongs to the
    # service user, so the service user writes there (root only hands over the content on stdin).
    local stage="$INSTALL_ROOT/data/updates/$PKG_VERSION" name sha run_id
    name="$(basename "$PKG_FILE")"
    as_service_user rm -rf -- "$stage"
    as_service_user mkdir -m 700 -- "$stage" || die "cannot create $stage"
    put_as_service_user "$stage/$name" <"$PKG_FILE" || die "cannot stage the package"
    sha="$(sha256_of "$PKG_FILE")"
    printf '%s  %s\n' "$sha" "$name" | put_as_service_user "$stage/SHA256SUMS.txt" || die "cannot stage SHA256SUMS.txt"
    run_id="$(new_uuid)"
    printf '%s\n' "# Written by install.sh --upgrade" "format=1" "run_id=$run_id" "version=$PKG_VERSION" \
        "from_version=$current" "arch=$ARCH" "file=$name" "sha256=$sha" |
        put_as_service_user "$INSTALL_ROOT/data/updates/request.conf" || die "cannot write the update request"
    systemctl start homepage-update.service || true

    local now_version result="$INSTALL_ROOT/data/updates/result.conf"
    now_version="$(installed_version || true)"
    as_service_user grep -E '^(state|message)=' -- "$result" 2>/dev/null || true
    [[ "$now_version" == "$PKG_VERSION" ]] || die "the upgrade did not complete; see journalctl -u homepage-update"
    print_summary
}

# The installed backup.sh, else (bin/backup.sh is missing) the one next to this install.sh, but only when only root
# can change it and its update.sh (install.sh is often downloaded alone into a shared folder). Never current/.
root_only_file() { [[ -f "$1" && ! -L "$1" && "$(stat -c %u -- "$1")" == 0 && "$(stat -c %A -- "$1")" != ?????w???? && "$(stat -c %A -- "$1")" != ????????w? ]]; }
backup_script() {
    local here
    here="$(dirname -- "${BASH_SOURCE[0]}")"
    if [[ -x "$INSTALL_ROOT/bin/backup.sh" ]]; then echo "$INSTALL_ROOT/bin/backup.sh"
    elif root_only_file "$here/backup.sh" && root_only_file "$here/update.sh" &&
        [[ "$(stat -c %u -- "$here")" == 0 && "$(stat -c %A -- "$here")" != ?????w???? && "$(stat -c %A -- "$here")" != ????????w? ]]; then
        echo "$here/backup.sh"
    else die "backup.sh not found; run install.sh --repair --package FILE (or --version X.Y.Z) to install it"; fi
}

do_backup() {
    [[ -f "$STATE_FILE" ]] || die "Homepage is not installed"
    local -a args=(create)
    [[ -z "$BACKUP_OUTPUT" ]] || args+=(--output "$BACKUP_OUTPUT")
    bash "$(backup_script)" "${args[@]}"
}

do_restore() {
    [[ -f "$STATE_FILE" ]] || die "Homepage is not installed here: install it first (same or newer version), then run --restore"
    [[ -n "$RESTORE_FILE" ]] || die "--restore needs a backup file"
    local -a args=(restore "$RESTORE_FILE")
    if [[ $UNATTENDED -eq 1 ]]; then
        [[ $ASSUME_YES -eq 1 ]] || die "--restore in unattended mode needs --yes"
        args+=(--yes)
    fi
    bash "$(backup_script)" "${args[@]}"
}

do_reset_password() {
    [[ -f "$STATE_FILE" ]] || die "Homepage is not installed"
    local accounts
    # The server CLI logs to stdout too: keep only the "name<TAB>enabled|disabled" lines.
    accounts="$(run_server "$INSTALL_ROOT/current" --list-local-accounts 2>/dev/null | grep -E $'^[^\t ]+\t(enabled|disabled)$' || true)"
    if [[ -z "$RESET_USER" ]]; then
        [[ $UNATTENDED -eq 0 ]] || die "--reset-password needs a user name in unattended mode"
        [[ -n "$accounts" ]] || die "there are no local accounts"
        echo "Local accounts:"
        printf '%s\n' "$accounts" | sed 's/\t/  (/; s/$/)/; s/^/  /'
        tty_read RESET_USER "Account" "$(printf '%s\n' "$accounts" | head -n1 | cut -f1)"
    fi
    [[ "$RESET_USER" =~ ^[A-Za-z0-9._@-]{1,64}$ ]] || die "invalid user name '$RESET_USER'"
    if [[ -z "$ADMIN_PASSWORD" ]]; then
        [[ $UNATTENDED -eq 0 ]] || die "missing ADMIN_PASSWORD (secrets file, stdin or HOMEPAGE_ADMIN_PASSWORD)"
        while true; do
            tty_secret ADMIN_PASSWORD "New password for $RESET_USER (at least 8 characters)" confirm
            [[ ${#ADMIN_PASSWORD} -ge 8 ]] && break
            echo "Too short." >/dev/tty
        done
    fi
    [[ ${#ADMIN_PASSWORD} -ge 8 && "$ADMIN_PASSWORD" != *$'\n'* ]] || die "the password must be one line of at least 8 characters"
    # The password goes through stdin, never the command line.
    printf '%s\n' "$ADMIN_PASSWORD" | run_server "$INSTALL_ROOT/current" --reset-local-password "$RESET_USER" 2>&1 |
        grep -vE '^(info|warn|dbug|trce): |^      ' || true
    run_server "$INSTALL_ROOT/current" --list-local-accounts 2>/dev/null | grep -qiE "^${RESET_USER//./\\.}"$'\t''enabled$' ||
        die "the password was not reset"
}

remove_program() { # program, units and service state; config, data, backups and the database stay
    systemctl disable --now homepage-update.path homepage.service 2>/dev/null || true
    systemctl stop homepage-update.service 2>/dev/null || true
    local u
    for u in "${UNITS[@]}"; do rm -f -- "$UNIT_DIR/$u"; done
    systemctl daemon-reload
    rm -rf -- "${INSTALL_ROOT:?}/releases" "${INSTALL_ROOT:?}/bin"
    rm -f -- "$INSTALL_ROOT/current"
}

drop_local_database() { # drop_local_database NAME ROLE PORT: only Homepage's own database and role
    local name="$1" role="$2" port="$3" owner super
    [[ "$name" =~ ^[A-Za-z0-9_]+$ && "$role" =~ ^[A-Za-z0-9_]+$ && "$port" =~ ^[0-9]+$ ]] ||
        { warn "unexpected database settings in $STATE_FILE; the database was not dropped"; return 0; }
    case "$name" in postgres|template0|template1) warn "refusing to drop '$name'"; return 0 ;; esac
    [[ "$role" != postgres ]] || { warn "refusing to drop the role postgres"; return 0; }
    hp_pgq() { runuser -u postgres -- psql -X -At -q -v ON_ERROR_STOP=1 -p "$port" -d postgres -c "$1"; }
    if ! super="$(hp_pgq "SELECT rolsuper FROM pg_roles WHERE rolname = '$role'")"; then
        warn "PostgreSQL on port $port is not reachable; drop the database '$name' and role '$role' yourself"; return 0
    fi
    [[ "$super" != t ]] || { warn "the role '$role' is a superuser: it was not created by Homepage and is left as it is"; return 0; }
    owner="$(hp_pgq "SELECT pg_catalog.pg_get_userbyid(datdba) FROM pg_database WHERE datname = '$name'")" ||
        { warn "could not look up the database '$name'; it was not dropped"; return 0; }
    if [[ -z "$owner" ]]; then
        info "The database '$name' does not exist any more"
    elif [[ "$owner" != "$role" ]]; then
        warn "the database '$name' is owned by '$owner', not by '$role': it is left as it is"
        return 0
    else
        info "Dropping the database '$name' (PostgreSQL on port $port; other databases are not touched)"
        hp_pgq "DROP DATABASE \"$name\" WITH (FORCE)" >/dev/null || warn "could not drop the database '$name'"
    fi
    if [[ -n "$super" ]]; then
        hp_pgq "DROP ROLE \"$role\"" >/dev/null ||
            warn "could not drop the role '$role' (it may own objects in other databases); it is left as it is"
    fi
}

do_uninstall() {
    [[ $EUID -eq 0 ]] || die "run as root: sudo $0"
    if [[ $KEEP_DATA -eq 1 ]]; then
        info "Removing the Homepage program and units (config, data, backups and the database are kept)"
        remove_program
        info "Kept: $INSTALL_ROOT/config, $INSTALL_ROOT/data, $INSTALL_ROOT/backups, the database and the $SERVICE_USER user"
        return 0
    fi

    local db_mode db_name db_user db_port
    db_mode="$(read_state DB_MODE || true)"
    db_name="$(read_state DB_NAME || echo homepage)"
    db_user="$(read_state DB_USER || echo homepage)"
    db_port="$(read_state DB_PORT || true)"
    if [[ -z "$db_port" && -f "$UPDATE_ENV" ]]; then db_port="$(sed -n 's/^PGPORT=//p' "$UPDATE_ENV" | head -n1)"; fi
    db_port="${db_port:-5432}"
    cat <<EOF

  !!  Uninstalling Homepage removes EVERYTHING on this server:
      - the program and its services
      - $INSTALL_ROOT: configuration, data (all media uploads, the Data Protection keys, themes) and backups
      - the DATABASE '$db_name' with all pages, posts, users, settings and history
EOF
    if [[ "$db_mode" == local ]]; then
        echo "        (only the database '$db_name' and its role '$db_user' are dropped; PostgreSQL itself and any other"
        echo "        databases and roles on it are left as they are)"
    elif [[ "$db_mode" == existing ]]; then echo "        (all tables in '$db_name' on the external PostgreSQL server are dropped; the database and role stay)"; fi
    echo "      This cannot be undone. Back up the database first if you may need it."
    echo

    local backup_file="$BACKUP_TO"
    if [[ $UNATTENDED -eq 1 ]]; then
        [[ $ASSUME_YES -eq 1 ]] || die "--uninstall in unattended mode needs --yes (and optionally --backup-to FILE)"
    elif [[ -z "$backup_file" && -f "$UPDATE_ENV" ]]; then
        if ask_yes_no "Create a backup (database, configuration, data and media, program) before uninstalling?" y; then
            tty_read backup_file "Backup file (outside $INSTALL_ROOT, which is deleted)" "/root/homepage-backup-$(date -u +%Y%m%dT%H%M%SZ).tar.gz"
        fi
    fi
    if [[ -n "$backup_file" ]]; then
        [[ "$backup_file" == *.tar.gz ]] || backup_file="$backup_file.tar.gz"
        [[ "$(readlink -m -- "$backup_file")" != "$INSTALL_ROOT"/* ]] || die "the backup must be outside $INSTALL_ROOT (it is deleted)"
        BACKUP_OUTPUT="$backup_file"
        do_backup || die "the backup failed; nothing was removed"
    fi
    if [[ $UNATTENDED -eq 0 ]]; then
        local answer
        read -r -p "Type 'uninstall' to remove Homepage and its database: " answer </dev/tty || die "not confirmed"
        [[ "$answer" == uninstall ]] || die "not confirmed"
    fi

    info "Removing Homepage"
    remove_program
    if [[ "$db_mode" == local ]] && command -v psql >/dev/null 2>&1; then
        drop_local_database "$db_name" "$db_user" "$db_port"
    elif [[ "$db_mode" == existing && -f "$UPDATE_ENV" ]]; then
        # The role owns the database: drop everything Homepage created in it.
        ( set -a; while IFS= read -r line; do
              [[ "$line" =~ ^(PGHOST|PGPORT|PGDATABASE|PGUSER|PGPASSWORD|PGSSLMODE)=(.*)$ ]] && printf -v "${BASH_REMATCH[1]}" '%s' "${BASH_REMATCH[2]}"
          done <"$UPDATE_ENV"; set +a
          psql -X -q -v ON_ERROR_STOP=1 -c 'DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;' ) ||
            warn "could not clear the external database '$db_name'; drop it yourself"
    fi
    rm -rf -- "$INSTALL_ROOT"
    if id -u "$SERVICE_USER" >/dev/null 2>&1; then userdel "$SERVICE_USER" || warn "could not remove the user $SERVICE_USER"; fi
    info "Homepage was removed. PostgreSQL packages and firewall rules were left as they are."
    [[ -z "$backup_file" ]] || info "Backup: $backup_file"
}

main() {
    parse_args "$@"
    if [[ "$MODE" == uninstall ]]; then load_secrets; do_uninstall; return; fi
    check_system
    load_secrets
    case "$MODE" in
        backup) do_backup; return ;;
        restore) do_restore; return ;;
        reset-password) do_reset_password; return ;;
    esac
    local installed=0
    if [[ -f "$STATE_FILE" ]]; then
        installed=1
    elif [[ -L "$INSTALL_ROOT/current" || -f "$CONFIG_FILE" ]]; then
        # A previous install stopped half way (install.state is written last). Start over; the config it wrote
        # is replaced (it may hold a database password that is no longer valid). data/ is kept.
        [[ -z "$MODE" ]] || die "a previous installation did not finish; run without --$MODE to install again, or --uninstall"
        warn "a previous installation did not finish; installing again (its config is replaced, data/ is kept)"
        if [[ $UNATTENDED -eq 0 ]]; then ask_yes_no "Continue?" y || exit 0; fi
        rm -f -- "$CONFIG_FILE" "$UPDATE_ENV" "$INSTALL_ROOT/current"
    fi
    if [[ $installed -eq 1 ]]; then
        if [[ -z "$MODE" ]]; then
            [[ $UNATTENDED -eq 0 ]] || die "Homepage is already installed: use --repair, --upgrade, --backup, --restore, --reset-password or --uninstall"
            echo "Homepage $(installed_version || echo '?') is installed. What do you want to do?"
            echo "  1) repair            (services, owners and modes; config and data are kept)"
            echo "  2) upgrade           (to the package or release given)"
            echo "  3) backup            (database, configuration, data and media, program)"
            echo "  4) restore a backup"
            echo "  5) reset a local account's password"
            echo "  6) uninstall         (removes everything, including the database)"
            echo "  7) cancel"
            local choice
            tty_read choice "Choice" 7
            case "$choice" in
                1) MODE=repair ;;
                2) MODE=upgrade ;;
                3) tty_read BACKUP_OUTPUT "Backup folder or .tar.gz file" "$INSTALL_ROOT/backups"; do_backup; return ;;
                4) tty_read RESTORE_FILE "Backup file to restore"; do_restore; return ;;
                5) do_reset_password; return ;;
                6) do_uninstall; return ;;
                *) exit 0 ;;
            esac
        fi
    else
        [[ -z "$MODE" ]] || die "Homepage is not installed; run without --$MODE"
        MODE=install
    fi
    case "$MODE" in
        install) do_install ;;
        repair) do_repair ;;
        upgrade) do_upgrade ;;
    esac
}

# Run only when executed (tests may source this file to call single steps).
if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then main "$@"; fi
