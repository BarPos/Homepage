#!/usr/bin/env bash
# Adapted from DomainNotice (MIT), https://github.com/BarPos/DomainNotice
# Homepage backup and restore (Ubuntu 24.04+, installs made by install.sh). Run as root.
#
#   backup.sh create [--output PATH]   one archive with the database, the configuration, data/ (media uploads,
#                                      Data Protection keys, themes) and the running program. PATH: a folder or a
#                                      file name ending in .tar.gz (default folder: /opt/homepage/backups). The
#                                      folder and every folder above it must be owned by root and writable only by
#                                      root.
#   backup.sh restore FILE [--yes]     puts a backup back on this server: database, configuration, data and
#                                      program (the backup's version becomes the running one). A backup of the current
#                                      state is made first. Homepage must be installed (install.sh) on this
#                                      server: its database connection settings, update.sh, backup.sh and systemd
#                                      units are kept (never taken from a backup), so a backup can also be restored
#                                      onto a new server.
#   backup.sh list                     backups in /opt/homepage/backups
#
# The archive contains secrets (the Data Protection keys, the database password, encrypted settings): it is written
# with mode 600 and owned by root. Keep copies somewhere safe, off this server.
set -Eeuo pipefail

HERE="$(cd "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
# The archive checks are shared with update.sh, installed next to this file (bin/) or next to it in a package.
if [[ ! -f "$HERE/update.sh" || -L "$HERE/update.sh" ]]; then
    printf 'ERROR: %s not found; run install.sh --repair --package FILE\n' "$HERE/update.sh" >&2
    exit 1
fi
# shellcheck source=update.sh
source "$HERE/update.sh"
set -Eeuo pipefail

HP_ROOT="${HP_ROOT:-/opt/homepage}"
SERVICE_USER=homepage
UNIT_DIR=/etc/systemd/system
UNITS=(homepage.service homepage-update.path homepage-update.service)
FORMAT=1
CONFIG="$HP_ROOT/config/appsettings.Production.json"
UPDATE_ENV="$HP_ROOT/config/update.env"
STATE="$HP_ROOT/config/install.state"
WORK="" OUT=""

info() { printf '==> %s\n' "$*"; }
warn() { printf 'WARNING: %s\n' "$*" >&2; }
die() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }
usage() { sed -n '3,19p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; }
cleanup() {
    if [[ -n "$WORK" && -d "$WORK" ]]; then rm -rf -- "$WORK"; fi
    if [[ -n "$OUT" && -d "$OUT" ]]; then rm -rf -- "$OUT"; fi
}
trap cleanup EXIT

require_install() {
    [[ $EUID -eq 0 ]] || die "run as root: sudo $0"
    [[ -f "$UPDATE_ENV" && -f "$CONFIG" && -L "$HP_ROOT/current" ]] ||
        die "Homepage is not installed here ($HP_ROOT). Install it with install.sh first."
}

# update.env holds KEY=VALUE lines written by install.sh (values are taken literally, never evaluated).
load_db_env() {
    local key value
    while IFS= read -r line || [[ -n "$line" ]]; do
        [[ "$line" =~ ^(PGHOST|PGPORT|PGDATABASE|PGUSER|PGPASSWORD|PGSSLMODE|PG_BIN|HEALTH_URL)=(.*)$ ]] || continue
        key="${BASH_REMATCH[1]}"; value="${BASH_REMATCH[2]}"
        printf -v "$key" '%s' "$value"
        export "${key?}"
    done <"$UPDATE_ENV"
    [[ -n "${PGDATABASE-}" && -n "${PGUSER-}" ]] || die "$UPDATE_ENV has no database settings"
}

pg() { local tool="$1"; shift; if [[ -n "${PG_BIN-}" && -x "$PG_BIN/$tool" ]]; then "$PG_BIN/$tool" "$@"; else "$tool" "$@"; fi; }

current_version() { basename "$(readlink -f "$HP_ROOT/current")"; }

state_value() { # state_value FILE KEY
    sed -n "s/^$2=//p" "$1" 2>/dev/null | head -n1
}

# write_root_file DEST OWNER MODE <CONTENT: temp file in the same root-owned folder, then rename (never writes
# through a link at DEST).
write_root_file() {
    local dest="$1" tmp
    tmp="$(mktemp "$(dirname -- "$dest")/.restore.XXXXXX")"
    cat >"$tmp"
    chown -h "$2" -- "$tmp"
    chmod "$3" -- "$tmp"
    mv -T -- "$tmp" "$dest"
}

# ------------------------------------------------------------------------------------------------ create

do_create() {
    local output=""
    while [[ $# -gt 0 ]]; do
        case "$1" in
            --output) [[ $# -ge 2 ]] || die "--output needs a path"; output="$2"; shift ;;
            --output=*) output="${1#*=}" ;;
            *) die "unknown argument: $1" ;;
        esac
        shift
    done
    require_install
    load_db_env

    local version stamp dir name file
    version="$(current_version)"
    stamp="$(date -u +%Y%m%dT%H%M%SZ)"
    HP_ERROR=""
    if [[ -z "$output" || "$output" == "$HP_ROOT/backups" || "$output" == "$HP_ROOT/backups/" ]]; then
        # The default folder is root-only (an owner or mode set by hand is taken over here).
        hp_root_dir "$HP_ROOT/backups" || die "$HP_ERROR (run install.sh --repair)"
        output="$HP_ROOT/backups"
    fi
    if [[ "$output" == *.tar.gz ]]; then
        dir="$(dirname -- "$output")"
        name="$(basename -- "$output")"
    else
        dir="$output"
        name="homepage-backup-$version-$(hostname -s)-$stamp.tar.gz"
    fi
    if [[ ! -e "$dir" && ! -L "$dir" ]]; then (umask 077; mkdir -p -- "$dir"); fi
    # Root writes the archive into this folder: refuse one that anybody else can change (or a link).
    hp_trusted_dir "$dir" || die "the backup folder is not safe: $HP_ERROR. Use a folder that only root can write to"
    dir="$HP_TRUSTED_DIR"
    file="$dir/$name"
    [[ ! -e "$file" && ! -L "$file" ]] || die "$file already exists"

    WORK="$(mktemp -d /var/tmp/homepage-backup.XXXXXX)"
    chmod 700 "$WORK"
    mkdir "$WORK/backup"

    info "Dumping the database $PGDATABASE"
    pg pg_dump --format=custom --no-password --file="$WORK/backup/database.dump" ||
        die "pg_dump failed (is the client as new as the server? see PG_BIN in $UPDATE_ENV)"

    info "Collecting configuration, data (media, keys, themes) and the program ($version)"
    # update.sh, backup.sh and the units are not included: a restore keeps the installed ones.
    local -a paths=("${HP_ROOT#/}/config" "${HP_ROOT#/}/data" "${HP_ROOT#/}/releases/$version")
    # Staged self-update downloads are not worth keeping; everything else in data/ is (media uploads, Data
    # Protection keys, generated theme CSS). The server keeps running: a file in data/ (e.g. a media upload or a
    # regenerated theme) may change or disappear while tar reads it. tar then exits with 1, which still leaves a
    # usable archive; 2 and above are real errors.
    #
    # Only what a restore accepts goes in: folders, and regular files with a single link and no setuid/setgid bit.
    # Links, FIFOs, sockets, devices and hard links in data/ (the service user can create those) are left out and
    # reported, so a compromised server cannot make every later backup unrestorable. --sparse keeps a huge sparse
    # file from filling the disk.
    local list="$WORK/files.list" skipped="$WORK/skipped.list" rc=0
    (cd / && find "${paths[@]}" -path "${HP_ROOT#/}/data/updates" -prune -o \
        \( -type d -o \( -type f -links 1 ! -perm /6000 \) \) -print0) >"$list"
    (cd / && find "${paths[@]}" -path "${HP_ROOT#/}/data/updates" -prune -o \
        ! -type d ! \( -type f -links 1 ! -perm /6000 \) -print) >"$skipped"
    if [[ -s "$skipped" ]]; then
        warn "$(wc -l <"$skipped") entries were left out because a restore would refuse them (links, special or hard-linked files, setuid/setgid):"
        head -n 20 "$skipped" | sed 's|^|  /|' >&2
    fi
    # -C must come before --files-from (GNU tar applies it positionally).
    tar --create --numeric-owner --sparse --warning=no-file-changed --warning=no-file-removed \
        --file="$WORK/backup/files.tar" -C / --no-recursion --null --files-from="$list" || rc=$?
    ((rc <= 1)) || die "tar failed ($rc) while collecting the files"
    ((rc == 0)) || warn "some files in data/ changed while they were read (the server is running); the backup is still usable"

    printf '%s\n' "format=$FORMAT" "version=$version" "created=$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
        "host=$(hostname -f 2>/dev/null || hostname)" "database=$PGDATABASE" >"$WORK/backup/manifest.txt"

    # Built in a new root-only folder inside the destination (same file system), then renamed into place: nothing
    # is opened, chowned or chmodded by a predictable name in the destination folder.
    OUT="$(mktemp -d "$dir/.homepage-backup.XXXXXX")"
    (umask 077; tar --create --gzip --file="$OUT/backup.tar.gz" -C "$WORK/backup" manifest.txt database.dump files.tar)
    chown -h root:root -- "$OUT/backup.tar.gz"
    chmod 600 -- "$OUT/backup.tar.gz"
    [[ ! -e "$file" && ! -L "$file" ]] || die "$file already exists"
    mv -T -- "$OUT/backup.tar.gz" "$file"
    rmdir -- "$OUT"
    OUT=""
    info "Backup written: $file ($(du -h "$file" | cut -f1))"
    echo "It contains secrets (Data Protection keys, database password): keep a copy somewhere safe, off this server."
}

# ------------------------------------------------------------------------------------------------ restore

do_restore() {
    local file="" yes=0 safety=1
    while [[ $# -gt 0 ]]; do
        case "$1" in
            --yes|-y) yes=1 ;;
            --no-safety-backup) safety=0 ;;
            -*) die "unknown argument: $1" ;;
            *) [[ -z "$file" ]] || die "only one backup file"; file="$1" ;;
        esac
        shift
    done
    [[ -n "$file" ]] || die "usage: $0 restore FILE [--yes]"
    [[ -f "$file" ]] || die "$file not found"
    require_install
    # update.sh, backup.sh and the units run as root: they stay this server's, never the backup's.
    local u
    for u in "${UNITS[@]}"; do
        [[ -f "$UNIT_DIR/$u" ]] || die "$UNIT_DIR/$u is missing: run install.sh --repair --package FILE first"
    done
    [[ -f "$HP_ROOT/bin/update.sh" ]] || warn "$HP_ROOT/bin/update.sh is missing: self-updates fail until install.sh --repair --package FILE"

    WORK="$(mktemp -d /var/tmp/homepage-restore.XXXXXX)"
    chmod 700 "$WORK"
    local listing m
    listing="$(tar --list --verbose --gzip --file="$file")" || die "$file is not a Homepage backup"
    HP_ERROR=""
    hp_check_member_types <<<"$listing" || die "$file: $HP_ERROR"
    tar --extract --gzip --no-same-owner --no-same-permissions --file="$file" -C "$WORK" manifest.txt database.dump files.tar ||
        die "$file is not a Homepage backup"
    for m in manifest.txt database.dump files.tar; do
        [[ -f "$WORK/$m" && ! -L "$WORK/$m" ]] || die "$file: $m is not a regular file"
    done
    [[ "$(state_value "$WORK/manifest.txt" format)" == "$FORMAT" ]] || die "unsupported backup format"
    local bversion
    bversion="$(state_value "$WORK/manifest.txt" version)"
    [[ "$bversion" =~ ^[0-9]{1,5}\.[0-9]{1,5}\.[0-9]{1,5}$ ]] || die "the backup has no valid version"

    # Refuse anything that would land outside the expected paths. bin/ and the units are accepted (a hand-made
    # archive may have them) but never extracted.
    local entry
    listing="$(tar --list --file="$WORK/files.tar")" || die "the backup's files.tar is broken"
    hp_check_member_names <<<"$listing" || die "unsafe backup: $HP_ERROR"
    while IFS= read -r entry; do
        case "$entry" in
            "${HP_ROOT#/}/config"|"${HP_ROOT#/}/config/"*|"${HP_ROOT#/}/data"|"${HP_ROOT#/}/data/"*) ;;
            "${HP_ROOT#/}/releases/$bversion"|"${HP_ROOT#/}/releases/$bversion/"*|"${HP_ROOT#/}/bin"|"${HP_ROOT#/}/bin/"*) ;;
            "${UNIT_DIR#/}/homepage.service"|"${UNIT_DIR#/}/homepage-update.path"|"${UNIT_DIR#/}/homepage-update.service") ;;
            *) die "unexpected path in the backup: $entry" ;;
        esac
        [[ "$entry" != *..* ]] || die "unexpected path in the backup: $entry"
    done <<<"$listing"
    # Only regular files, folders and links; no hard links, devices, FIFOs, setuid or setgid entries.
    listing="$(tar --list --verbose --file="$WORK/files.tar")" || die "the backup's files.tar is broken"
    hp_check_member_types <<<"$listing" || die "unsafe backup: $HP_ERROR"
    hp_check_member_modes <<<"$listing" || die "unsafe backup: $HP_ERROR"

    # Unpack and check everything before this server is touched.
    mkdir "$WORK/root"
    local src="$WORK/root${HP_ROOT}"
    tar --extract --no-same-owner --preserve-permissions --file="$WORK/files.tar" -C "$WORK/root" \
        "${HP_ROOT#/}/config" "${HP_ROOT#/}/data" "${HP_ROOT#/}/releases/$bversion" ||
        die "the backup lacks config, data or releases/$bversion"
    hp_check_tree "$WORK/root" || die "unsafe backup: $HP_ERROR"
    for m in config data "releases/$bversion"; do
        [[ -d "$src/$m" && ! -L "$src/$m" ]] || die "the backup's $m is not a folder"
    done
    # Root rewrites files in config/ after the restore: only plain files and folders there.
    [[ -z "$(find "$src/config" ! -type f ! -type d -print -quit)" ]] || die "unsafe backup: links or special files in config/"
    [[ -f "$src/releases/$bversion/Homepage.Server" && ! -L "$src/releases/$bversion/Homepage.Server" ]] ||
        die "the backup's program has no Homepage.Server"

    echo "Backup:  $(basename "$file")"
    sed 's/^/         /' "$WORK/manifest.txt"
    echo "This server: Homepage $(current_version)"
    echo
    echo "Restoring REPLACES this server's database, configuration, data (media, keys) and program with the backup's."
    echo "The database connection settings, update.sh, backup.sh and the systemd units of this server are kept."
    if [[ $yes -eq 0 ]]; then
        local answer
        read -r -p "Type 'restore' to continue: " answer </dev/tty || die "not confirmed"
        [[ "$answer" == restore ]] || die "not confirmed"
    fi

    if [[ $safety -eq 1 ]]; then
        info "Backing up the current state first"
        bash "${BASH_SOURCE[0]}" create --output "$HP_ROOT/backups" || die "the safety backup failed; nothing was changed"
    fi

    load_db_env
    local keep_cs keep_env keep_state
    keep_cs="$(grep -m1 '"ConnectionStrings"' "$CONFIG" || true)"
    keep_env="$(cat "$UPDATE_ENV")"
    keep_state="$(cat "$STATE" 2>/dev/null || true)"

    info "Stopping Homepage"
    systemctl stop homepage-update.path homepage.service 2>/dev/null || true

    info "Restoring files"
    rm -rf -- "${HP_ROOT:?}/config" "${HP_ROOT:?}/data"
    cp -a -- "$src/config" "$HP_ROOT/config"
    cp -a -- "$src/data" "$HP_ROOT/data"
    mkdir -p "$HP_ROOT/data/updates"
    rm -rf -- "${HP_ROOT:?}/releases/$bversion"
    mkdir -p "$HP_ROOT/releases"
    cp -a -- "$src/releases/$bversion" "$HP_ROOT/releases/$bversion"
    ln -sfn "releases/$bversion" "$HP_ROOT/.current.new" && mv -T -- "$HP_ROOT/.current.new" "$HP_ROOT/current"

    # Keep this server's database settings (a backup from another server has another password).
    printf '%s\n' "$keep_env" | write_root_file "$UPDATE_ENV" root:root 600
    if [[ -n "$keep_cs" && -f "$CONFIG" ]]; then
        awk -v line="$keep_cs" '/"ConnectionStrings"/ && !done { print line; done = 1; next } { print }' "$CONFIG" |
            write_root_file "$CONFIG" "root:$SERVICE_USER" 640
    fi
    # install.state: the backup's values (site mode, bind address, port, name) when they are valid, this server's
    # database settings, and this server's values for anything missing or invalid.
    local restored_state="" line k v new_state=""
    if [[ -f "$STATE" ]]; then restored_state="$(cat "$STATE")"; fi
    while IFS= read -r line; do
        [[ "$line" == *=* ]] || continue
        k="${line%%=*}"; v="${line#*=}"
        case "$k" in
            DB_MODE|DB_NAME|DB_USER|DB_PORT|DB_CREATED) continue ;;
            SITE_PORT) hp_is_port "$v" || continue ;;
            SITE_MODE) [[ "$v" == http || "$v" == https ]] || continue ;;
            SITE_BIND) hp_is_bind "$v" || continue ;;
            SERVER_NAME) [[ "$v" =~ ^[A-Za-z0-9.-]{1,253}$ ]] || continue ;;
            [A-Z_]*) [[ "$k" =~ ^[A-Z_]+$ && "$v" =~ ^[A-Za-z0-9.:_-]*$ ]] || continue ;;
            *) continue ;;
        esac
        new_state+="$line"$'\n'
    done <<<"$restored_state"
    for k in DB_MODE DB_NAME DB_USER DB_PORT DB_CREATED SITE_PORT SITE_MODE SITE_BIND SERVER_NAME; do
        [[ "$new_state" == *$'\n'"$k="* || "$new_state" == "$k="* ]] && continue
        v="$(printf '%s\n' "$keep_state" | sed -n "s/^$k=//p" | head -n1)"
        [[ -z "$v" ]] || new_state+="$k=$v"$'\n'
    done
    printf '%s' "$new_state" | write_root_file "$STATE" root:root 600
    # The health check follows the restored site mode, bind address and port. hp_health_url accepts only a plain
    # port and a loopback or wildcard address, so nothing from the backup reaches the sed replacement unchecked.
    local mode bind port health
    mode="$(state_value "$STATE" SITE_MODE)"; bind="$(state_value "$STATE" SITE_BIND)"; port="$(state_value "$STATE" SITE_PORT)"
    [[ "$mode" == http ]] || mode=https
    if [[ -z "$bind" ]]; then if [[ "$mode" == http ]]; then bind=127.0.0.1; else bind=0.0.0.0; fi; fi
    if health="$(hp_health_url "$mode" "$bind" "$port")"; then
        sed "s|^HEALTH_URL=.*|HEALTH_URL=$health|" "$UPDATE_ENV" | write_root_file "$UPDATE_ENV" root:root 600
    else
        warn "install.state has no valid site mode, bind address and port: HEALTH_URL in $UPDATE_ENV is kept"
    fi

    # Owners and modes as install.sh sets them; no setuid or setgid bits anywhere (links are not followed).
    chown -hR root:"$SERVICE_USER" "$HP_ROOT/config"; chmod 750 "$HP_ROOT/config"
    find "$HP_ROOT/config" -type f -exec chmod 640 {} +
    for f in "$UPDATE_ENV" "$STATE"; do [[ -f "$f" ]] && { chown root:root "$f"; chmod 600 "$f"; }; done
    chown -hR "$SERVICE_USER:$SERVICE_USER" "$HP_ROOT/data"; chmod 700 "$HP_ROOT/data"
    chmod -R ug-s "$HP_ROOT/data"
    chown -hR root:root "$HP_ROOT/releases/$bversion"; chmod -R go-w,ug-s "$HP_ROOT/releases/$bversion"

    info "Restoring the database $PGDATABASE"
    load_db_env
    pg psql --no-password -X -v ON_ERROR_STOP=1 -q -c 'DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;' ||
        die "could not reset the database schema (the role must own the database)"
    pg pg_restore --no-password --no-owner --no-privileges --exit-on-error -d "$PGDATABASE" "$WORK/database.dump" ||
        die "pg_restore failed; the safety backup is in $HP_ROOT/backups"

    info "Starting Homepage $bversion"
    systemctl daemon-reload
    systemctl enable homepage.service homepage-update.path >/dev/null 2>&1 || true
    systemctl start homepage.service homepage-update.path
    load_db_env
    local i
    for ((i = 0; i < 40; i++)); do
        if curl -fsk --max-time 5 "$HEALTH_URL" >/dev/null 2>&1; then
            info "Restored. Homepage $bversion is running."
            return 0
        fi
        sleep 3
    done
    warn "the server did not answer on $HEALTH_URL yet: journalctl -u homepage"
}

do_list() {
    ls -lh "$HP_ROOT"/backups/*.tar.gz 2>/dev/null || echo "No backups in $HP_ROOT/backups."
}

case "${1-}" in
    create) shift; do_create "$@" ;;
    restore) shift; do_restore "$@" ;;
    list) do_list ;;
    -h|--help|"") usage ;;
    *) die "unknown command: $1 (see --help)" ;;
esac
