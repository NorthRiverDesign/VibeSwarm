#!/usr/bin/env bash
# Starts an install made by install.sh, applies staged updates, and rolls back one that fails.
#
#   <root>/launcher.sh        this script (the service's ExecStart)
#   <root>/current            symlink to versions/<version>
#   <root>/versions/<version> one folder per release, never edited once staged
#   <root>/data               working directory: .env, the SQLite database, the HTTPS certificate
#   <root>/state              pending.env (from the app), trial.env (from here), last-update.env
#   <root>/backups            the SQLite database as it was before each update
#
# To update, the app stages versions/<new>, writes state/pending.env and exits with code 75.
# This script backs up the SQLite database, points current at <new> and starts it as a trial.
# The app confirms the trial once it is serving by deleting state/trial.env. If it exits first,
# or hasn't confirmed within UPDATE_CONFIRM_TIMEOUT seconds, the database backup and the
# previous version are restored and started instead.
#
# State files are KEY=value lines. They are parsed here, never sourced.

set -uo pipefail

LAUNCHER_VERSION=1
RESTART_EXIT_CODE=75
MAX_TRIAL_STARTS=2

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
VERSIONS="$ROOT/versions"
DATA="$ROOT/data"
STATE="$ROOT/state"
BACKUPS="$ROOT/backups"
PENDING="$STATE/pending.env"
TRIAL="$STATE/trial.env"
LAST_UPDATE="$STATE/last-update.env"
CONFIRM_TIMEOUT="${UPDATE_CONFIRM_TIMEOUT:-300}"

CHILD=""
WATCHDOG=""
STOPPING=0

log() { printf '[launcher] %s\n' "$*" >&2; }

# kv_get FILE KEY: prints the value of KEY, or fails when it is absent.
kv_get() {
	local line
	[ -f "$1" ] || return 1
	while IFS= read -r line || [ -n "$line" ]; do
		if [ "${line%%=*}" = "$2" ]; then
			printf '%s' "${line#*=}"
			return 0
		fi
	done <"$1"
	return 1
}

# kv_write FILE KEY=value...: writes beside FILE and renames over it.
kv_write() {
	local file="$1" tmp
	shift
	tmp="$file.$$.tmp"
	printf '%s\n' "$@" >"$tmp" && mv -f "$tmp" "$file"
}

is_version_name() {
	[[ "$1" =~ ^[0-9A-Za-z][0-9A-Za-z._+-]*$ ]] && [[ "$1" != *..* ]]
}

# entry_dll DIR: the app's dll, found by the runtimeconfig that asks for ASP.NET Core.
entry_dll() {
	local config dll
	for config in "$1"/*.runtimeconfig.json; do
		[ -f "$config" ] || continue
		if grep -q '"Microsoft.AspNetCore.App"' "$config"; then
			dll="${config%.runtimeconfig.json}.dll"
			if [ -f "$dll" ]; then
				printf '%s' "$dll"
				return 0
			fi
		fi
	done
	return 1
}

current_version() {
	local target
	target="$(readlink "$ROOT/current" 2>/dev/null)" || return 1
	printf '%s' "${target##*/}"
}

point_current() {
	ln -sfn "versions/$1" "$ROOT/.current.new" || return 1
	# GNU mv -T swaps the link atomically; BSD mv has no -T, so replace it in place there.
	if mv -T "$ROOT/.current.new" "$ROOT/current" 2>/dev/null; then
		return 0
	fi
	rm -f "$ROOT/.current.new"
	ln -sfn "versions/$1" "$ROOT/current"
}

# record_result RESULT FROM TO MESSAGE: read by the app and shown in Settings.
record_result() {
	kv_write "$LAST_UPDATE" "RESULT=$1" "FROM=$2" "TO=$3" "MESSAGE=$4" "AT=$(date +%s)"
}

find_dotnet() {
	local configured
	if [ -x "$ROOT/dotnet/dotnet" ]; then
		export DOTNET_ROOT="$ROOT/dotnet"
		printf '%s' "$ROOT/dotnet/dotnet"
		return 0
	fi
	if configured="$(kv_get "$ROOT/install.conf" DOTNET)" && [ -x "$configured" ]; then
		printf '%s' "$configured"
		return 0
	fi
	command -v dotnet
}

backup_sqlite() { # SQLITE_PATH DESTINATION_DIR
	local db="$1" dest="$2" suffix
	mkdir -p "$dest" || return 1
	cp -p "$db" "$dest/" || return 1
	for suffix in -wal -shm; do
		if [ -f "$db$suffix" ]; then
			cp -p "$db$suffix" "$dest/" || return 1
		fi
	done
}

restore_sqlite() { # SQLITE_PATH BACKUP_DIR
	local db="$1" backup="$2" name suffix
	name="$(basename "$db")"
	[ -f "$backup/$name" ] || return 1
	cp -p "$backup/$name" "$db.restore.$$" || return 1
	rm -f "$db-wal" "$db-shm"
	mv -f "$db.restore.$$" "$db" || return 1
	for suffix in -wal -shm; do
		if [ -f "$backup/$name$suffix" ]; then
			cp -p "$backup/$name$suffix" "$db$suffix" || return 1
		fi
	done
}

# Turns state/pending.env into a trial of the staged version.
apply_pending() {
	local to from sqlite backup=""
	[ -f "$PENDING" ] || return 0

	if ! to="$(kv_get "$PENDING" VERSION)" || ! is_version_name "$to"; then
		log "Ignoring an unreadable pending update."
		mv -f "$PENDING" "$PENDING.invalid"
		return 0
	fi
	from="$(current_version || true)"

	if ! entry_dll "$VERSIONS/$to" >/dev/null; then
		log "Staged release $to is incomplete; staying on ${from:-the current version}."
		record_result failed "$from" "$to" "The downloaded release was incomplete, so it wasn't installed."
		rm -f "$PENDING"
		return 0
	fi

	sqlite="$(kv_get "$PENDING" SQLITE_PATH || true)"
	if [ -n "$sqlite" ] && [ -f "$sqlite" ]; then
		backup="$BACKUPS/$(date +%Y%m%d-%H%M%S)-${from:-unknown}-to-$to"
		if ! backup_sqlite "$sqlite" "$backup"; then
			log "Couldn't back up $sqlite; not updating."
			rm -rf "$backup"
			record_result failed "$from" "$to" "The database couldn't be backed up, so the update wasn't installed."
			rm -f "$PENDING"
			return 0
		fi
		log "Backed up the database to $backup"
	fi

	# The trial is recorded before current moves, so an interrupted switch can always be undone.
	kv_write "$TRIAL" "VERSION=$to" "PREVIOUS=$from" "BACKUP=$backup" "SQLITE_PATH=$sqlite" "STARTS=0" || return 1
	if ! point_current "$to"; then
		log "Couldn't switch to $to; staying on $from."
		rm -f "$TRIAL"
		record_result failed "$from" "$to" "The new version couldn't be switched in."
		rm -f "$PENDING"
		return 0
	fi
	rm -f "$PENDING"
	log "Switched from ${from:-nothing} to $to; starting it as a trial."
}

rollback() { # REASON
	local reason="$1" to previous backup sqlite
	to="$(kv_get "$TRIAL" VERSION || true)"
	previous="$(kv_get "$TRIAL" PREVIOUS || true)"
	backup="$(kv_get "$TRIAL" BACKUP || true)"
	sqlite="$(kv_get "$TRIAL" SQLITE_PATH || true)"

	if [ -z "$previous" ] || ! is_version_name "$previous" || ! entry_dll "$VERSIONS/$previous" >/dev/null; then
		log "Update to $to failed ($reason), and there is no previous version to go back to."
		record_result failed "$previous" "$to" "$to failed to start ($reason) and there was no previous version to restore."
		rm -f "$TRIAL"
		return 1
	fi

	log "Update to $to failed ($reason); rolling back to $previous."
	if [ -n "$backup" ] && [ -n "$sqlite" ]; then
		if restore_sqlite "$sqlite" "$backup"; then
			log "Restored the database from $backup"
		else
			log "Couldn't restore the database from $backup; the previous version will start on it as it is."
		fi
	fi
	point_current "$previous"
	record_result rolled-back "$previous" "$to" "$to didn't start ($reason), so $previous and its database were restored."
	rm -f "$TRIAL"
}

# Before each start: a trial that keeps failing to start is rolled back.
check_trial() {
	local starts version
	[ -f "$TRIAL" ] || return 0
	version="$(kv_get "$TRIAL" VERSION || true)"
	starts="$(kv_get "$TRIAL" STARTS || echo 0)"
	[[ "$starts" =~ ^[0-9]+$ ]] || starts=0
	if [ "$starts" -ge "$MAX_TRIAL_STARTS" ]; then
		rollback "it was started $starts times without finishing startup"
		return 0
	fi
	if [ "$(current_version || true)" != "$version" ]; then
		point_current "$version" || { rollback "current couldn't be pointed at it"; return 0; }
	fi
	kv_write "$TRIAL" \
		"VERSION=$version" \
		"PREVIOUS=$(kv_get "$TRIAL" PREVIOUS || true)" \
		"BACKUP=$(kv_get "$TRIAL" BACKUP || true)" \
		"SQLITE_PATH=$(kv_get "$TRIAL" SQLITE_PATH || true)" \
		"STARTS=$((starts + 1))"
}

# current is missing (a first start, or an interrupted switch): use the newest release folder.
recover_current() {
	local newest
	if [ -L "$ROOT/current" ] && [ -d "$ROOT/current" ]; then
		return 0
	fi
	newest="$(find "$VERSIONS" -mindepth 1 -maxdepth 1 -type d ! -name '.*' -exec basename {} \; 2>/dev/null | sort -V | tail -n 1)"
	if [ -z "$newest" ]; then
		log "No release is installed in $VERSIONS. Re-run install.sh."
		exit 1
	fi
	log "Pointing current at $newest."
	point_current "$newest"
}

# The app is stopped with SIGTERM, which .NET treats as a graceful shutdown. Not SIGINT: a
# background child of a non-interactive shell starts with SIGINT ignored.
on_signal() {
	STOPPING=1
	if [ -n "$CHILD" ]; then
		kill -TERM "$CHILD" 2>/dev/null
	fi
}

start_watchdog() {
	[ -f "$TRIAL" ] || return 0
	(
		deadline=$(($(date +%s) + CONFIRM_TIMEOUT))
		while kill -0 "$CHILD" 2>/dev/null; do
			[ -f "$TRIAL" ] || exit 0
			if [ "$(date +%s)" -ge "$deadline" ]; then
				log "The new version hasn't finished starting after ${CONFIRM_TIMEOUT}s; stopping it."
				: >"$STATE/.trial-timed-out"
				kill -TERM "$CHILD" 2>/dev/null
				for _ in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20 21 22 23 24 25 26 27 28 29 30; do
					kill -0 "$CHILD" 2>/dev/null || exit 0
					sleep 1
				done
				kill -KILL "$CHILD" 2>/dev/null
				exit 0
			fi
			sleep 2
		done
	) &
	WATCHDOG=$!
}

main() {
	local dotnet app_dir dll code
	mkdir -p "$DATA" "$STATE" "$BACKUPS" "$VERSIONS"
	trap on_signal INT TERM HUP

	while :; do
		apply_pending
		check_trial
		recover_current

		if ! dotnet="$(find_dotnet)" || [ -z "$dotnet" ]; then
			log "The .NET runtime wasn't found. Re-run install.sh."
			exit 1
		fi
		app_dir="$(cd "$ROOT/current" && pwd -P)"
		if ! dll="$(entry_dll "$app_dir")"; then
			if [ -f "$TRIAL" ]; then
				rollback "its app files are missing"
				continue
			fi
			log "No app found in $app_dir. Re-run install.sh."
			exit 1
		fi

		rm -f "$STATE/.trial-timed-out"
		log "Starting ${app_dir##*/} (launcher $LAUNCHER_VERSION)."
		cd "$DATA" || exit 1
		Updates__InstallRoot="$ROOT" "$dotnet" "$dll" --contentRoot "$app_dir" &
		CHILD=$!
		start_watchdog

		# wait returns early when a trapped signal arrives; keep waiting until the app has exited.
		while :; do
			wait "$CHILD"
			code=$?
			kill -0 "$CHILD" 2>/dev/null || break
		done
		CHILD=""
		if [ -n "$WATCHDOG" ]; then
			kill "$WATCHDOG" 2>/dev/null
			wait "$WATCHDOG" 2>/dev/null
			WATCHDOG=""
		fi

		if [ "$STOPPING" -eq 1 ]; then
			# Stopped from outside. An unconfirmed trial gets another start next time.
			exit "$code"
		fi
		if [ -f "$PENDING" ]; then
			continue
		fi
		if [ -f "$TRIAL" ]; then
			if [ -f "$STATE/.trial-timed-out" ]; then
				rollback "it didn't finish starting within ${CONFIRM_TIMEOUT}s"
			else
				rollback "it exited with code $code while starting"
			fi
			continue
		fi
		if [ "$code" -eq "$RESTART_EXIT_CODE" ]; then
			continue
		fi
		exit "$code"
	done
}

main "$@"
