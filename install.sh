#!/usr/bin/env bash
# Installs VibeSwarm from its GitHub releases, or updates and repairs an existing install.
#
#   curl -fsSL https://raw.githubusercontent.com/NorthRiverDesign/VibeSwarm/main/install.sh | bash
#
# Options (after "bash -s --" when piped):
#   --dir DIR         where to install (default ~/.vibeswarm)
#   --version TAG     install this release tag instead of the newest, e.g. v1.2.0
#   --port PORT       port for a new install (default 5000); an existing data/.env is kept
#   --no-service      don't set up a systemd service; print how to start it instead
#   --import DIR      copy .env, the SQLite database and certificate from an older install
#   --package FILE    install from a downloaded .tar.gz instead of GitHub (checked against
#                     SHA256SUMS when that file sits beside it)
#   --uninstall       remove the service and app files, keeping data/ and backups/
#   --purge           with --uninstall, also delete data/ and backups/
#
# Running it again is safe: it never overwrites data/.env or the database, and an update goes
# through the launcher, which backs up the SQLite database and rolls back if the new version
# fails to start.

set -euo pipefail

# ── Names: keep in step with AppBranding.cs and .github/workflows/release.yml ─────────────────
APP_NAME="VibeSwarm"
APP_ID="vibeswarm"
REPO="${INSTALL_REPO:-NorthRiverDesign/VibeSwarm}"
# ─────────────────────────────────────────────────────────────────────────────────────────────

INSTALL_DIR="${INSTALL_DIR:-$HOME/.$APP_ID}"
SERVICE_NAME="$APP_ID"
VERSION_TAG=""
PORT=""
WITH_SERVICE=1
IMPORT_DIR=""
PACKAGE_FILE=""
UNINSTALL=0
PURGE=0

say() { printf '\033[1m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[33mwarning:\033[0m %s\n' "$*" >&2; }
die() {
	printf '\033[31merror:\033[0m %s\n' "$*" >&2
	exit 1
}

while [ $# -gt 0 ]; do
	case "$1" in
	--dir) INSTALL_DIR="${2:?--dir needs a folder}"; shift 2 ;;
	--version) VERSION_TAG="${2:?--version needs a tag}"; shift 2 ;;
	--port) PORT="${2:?--port needs a number}"; shift 2 ;;
	--no-service) WITH_SERVICE=0; shift ;;
	--import) IMPORT_DIR="${2:?--import needs a folder}"; shift 2 ;;
	--package) PACKAGE_FILE="${2:?--package needs a file}"; shift 2 ;;
	--uninstall) UNINSTALL=1; shift ;;
	--purge) PURGE=1; shift ;;
	-h | --help) sed -n '2,22p' "$0" 2>/dev/null || true; exit 0 ;;
	*) die "Unknown option: $1 (see --help)" ;;
	esac
done

[ "$(id -u)" -ne 0 ] || [ -n "${INSTALL_ALLOW_ROOT:-}" ] ||
	die "Run this as the user whose agent CLIs (claude, copilot, opencode) $APP_NAME should use, not as root."
[ -z "$PORT" ] || [[ "$PORT" =~ ^[0-9]+$ ]] || die "--port must be a number."

case "$(uname -s)" in
Linux) OS=linux ;;
Darwin) OS=macos ;;
*) die "$APP_NAME installs on Linux and macOS. On Windows, download the .zip from https://github.com/$REPO/releases." ;;
esac

mkdir -p "$INSTALL_DIR"
INSTALL_DIR="$(cd "$INSTALL_DIR" && pwd -P)"
VERSIONS="$INSTALL_DIR/versions"
DATA="$INSTALL_DIR/data"
STATE="$INSTALL_DIR/state"
UNIT_FILE="/etc/systemd/system/$SERVICE_NAME.service"
UNIT_MARKER="# Managed by $APP_ID install.sh"

SUDO=""
if command -v sudo >/dev/null 2>&1; then SUDO="sudo"; fi

has_systemd() { [ "$OS" = linux ] && command -v systemctl >/dev/null 2>&1 && [ -d /run/systemd/system ]; }
unit_is_ours() { [ -f "$UNIT_FILE" ] && grep -qF "$UNIT_MARKER" "$UNIT_FILE"; }
service_active() { has_systemd && unit_is_ours && systemctl is-active --quiet "$SERVICE_NAME"; }

# ── Uninstall ────────────────────────────────────────────────────────────────────────────────
if [ "$UNINSTALL" -eq 1 ]; then
	if has_systemd && unit_is_ours; then
		say "Removing the $SERVICE_NAME service"
		$SUDO systemctl disable --now "$SERVICE_NAME" || true
		$SUDO rm -f "$UNIT_FILE"
		$SUDO systemctl daemon-reload
	fi
	rm -rf "$VERSIONS" "$STATE" "$INSTALL_DIR/dotnet" "$INSTALL_DIR/current" "$INSTALL_DIR/launcher.sh" "$INSTALL_DIR/install.conf"
	if [ "$PURGE" -eq 1 ]; then
		rm -rf "$DATA" "$INSTALL_DIR/backups"
		rmdir "$INSTALL_DIR" 2>/dev/null || true
		say "Removed $APP_NAME and its data."
	else
		say "Removed $APP_NAME. Your settings and database are still in $DATA (delete them with --uninstall --purge)."
	fi
	exit 0
fi

# ── Prerequisites ────────────────────────────────────────────────────────────────────────────
command -v tar >/dev/null 2>&1 || die "tar is required."
if command -v curl >/dev/null 2>&1; then
	fetch() { curl -fsSL --retry 3 -o "$2" "$1"; }
	fetch_text() { curl -fsSL --retry 3 -H "Accept: application/vnd.github+json" "$1"; }
elif command -v wget >/dev/null 2>&1; then
	fetch() { wget -q -O "$2" "$1"; }
	fetch_text() { wget -q -O - --header="Accept: application/vnd.github+json" "$1"; }
else
	die "curl or wget is required."
fi
if command -v sha256sum >/dev/null 2>&1; then
	sha256() { sha256sum "$1" | cut -d' ' -f1; }
elif command -v shasum >/dev/null 2>&1; then
	sha256() { shasum -a 256 "$1" | cut -d' ' -f1; }
else
	die "sha256sum or shasum is required to verify the download."
fi
command -v git >/dev/null 2>&1 || warn "git isn't installed. $APP_NAME needs it to run jobs; install it before adding a project."

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# ── Download and verify ──────────────────────────────────────────────────────────────────────
if [ -n "$PACKAGE_FILE" ]; then
	[ -f "$PACKAGE_FILE" ] || die "No such package: $PACKAGE_FILE"
	ARCHIVE="$PACKAGE_FILE"
	SUMS="$(dirname "$PACKAGE_FILE")/SHA256SUMS"
	if [ -f "$SUMS" ]; then
		expected="$(awk -v n="$(basename "$ARCHIVE")" '{f=$2; sub(/^\*/, "", f)} f == n {print $1}' "$SUMS")"
		[ -n "$expected" ] || die "SHA256SUMS beside the package doesn't list $(basename "$ARCHIVE")."
		[ "$(sha256 "$ARCHIVE")" = "$expected" ] || die "$(basename "$ARCHIVE") doesn't match SHA256SUMS."
		say "Verified the SHA-256 checksum"
	else
		warn "No SHA256SUMS beside $PACKAGE_FILE; installing it unverified."
	fi
else
	if [ -z "$VERSION_TAG" ]; then
		say "Finding the newest $APP_NAME release"
		release_json="$(fetch_text "https://api.github.com/repos/$REPO/releases/latest")" ||
			die "Couldn't find a release of $REPO. Has one been published yet?"
		VERSION_TAG="$(printf '%s' "$release_json" | sed -n 's/.*"tag_name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -n 1)"
		[ -n "$VERSION_TAG" ] || die "Couldn't read the newest release's tag."
	fi
	asset="$APP_ID-$VERSION_TAG.tar.gz"
	base="https://github.com/$REPO/releases/download/$VERSION_TAG"
	say "Downloading $APP_NAME $VERSION_TAG"
	ARCHIVE="$WORK/$asset"
	fetch "$base/$asset" "$ARCHIVE" || die "Couldn't download $base/$asset"
	fetch "$base/SHA256SUMS" "$WORK/SHA256SUMS" || die "Couldn't download the release's SHA256SUMS, so the package can't be verified."
	expected="$(awk -v n="$asset" '{f=$2; sub(/^\*/, "", f)} f == n {print $1}' "$WORK/SHA256SUMS")"
	[ -n "$expected" ] || die "SHA256SUMS doesn't list $asset."
	[ "$(sha256 "$ARCHIVE")" = "$expected" ] || die "The download doesn't match its published SHA-256. Nothing was changed; try again."
	say "Verified the SHA-256 checksum"
fi

# ── Unpack into versions/<version> ───────────────────────────────────────────────────────────
mkdir -p "$WORK/extract"
tar -xzf "$ARCHIVE" -C "$WORK/extract" || die "The package couldn't be unpacked."

find_app_dir() {
	local config
	for config in "$1"/*.runtimeconfig.json "$1"/*/*.runtimeconfig.json; do
		[ -f "$config" ] || continue
		if grep -q '"Microsoft.AspNetCore.App"' "$config" && [ -f "${config%.runtimeconfig.json}.dll" ]; then
			dirname "$config"
			return 0
		fi
	done
	return 1
}
APP_DIR_NEW="$(find_app_dir "$WORK/extract")" || die "The package doesn't contain $APP_NAME."
APP_DLL="$(basename "$(grep -l '"Microsoft.AspNetCore.App"' "$APP_DIR_NEW"/*.runtimeconfig.json | head -n 1)" .runtimeconfig.json).dll"

if [ -z "$VERSION_TAG" ]; then
	# A local package: take the version from the file name (vibeswarm-v1.2.3.tar.gz).
	VERSION_TAG="$(basename "$ARCHIVE")"
	VERSION_TAG="${VERSION_TAG#"$APP_ID"-}"
	VERSION_TAG="${VERSION_TAG%.tar.gz}"
fi
VERSION="${VERSION_TAG#v}"
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+(\.[0-9]+)?(-[0-9A-Za-z.-]+)?$ ]] || die "'$VERSION_TAG' isn't a release version."

# The ASP.NET Core version the release asks for, e.g. 10.0.0, from its runtimeconfig.json.
RUNTIME_VERSION="$(tr -d ' \n\r\t' <"$APP_DIR_NEW/${APP_DLL%.dll}.runtimeconfig.json" |
	sed -n 's/.*"name":"Microsoft.AspNetCore.App","version":"\([0-9.]*\)".*/\1/p')"
[ -n "$RUNTIME_VERSION" ] || RUNTIME_VERSION="10.0.0"
RUNTIME_CHANNEL="$(printf '%s' "$RUNTIME_VERSION" | cut -d. -f1-2)"

CURRENT_VERSION=""
if [ -L "$INSTALL_DIR/current" ]; then
	CURRENT_VERSION="$(basename "$(readlink "$INSTALL_DIR/current")")"
fi

mkdir -p "$VERSIONS" "$DATA" "$STATE" "$INSTALL_DIR/backups"
if [ "$CURRENT_VERSION" = "$VERSION" ] && [ -d "$VERSIONS/$VERSION" ]; then
	say "$APP_NAME $VERSION is already installed; repairing the launcher and service"
else
	staging="$VERSIONS/.staging-$VERSION-$$"
	rm -rf "$staging" "$VERSIONS/$VERSION"
	mv "$APP_DIR_NEW" "$staging"
	mv "$staging" "$VERSIONS/$VERSION"
fi

# ── .NET runtime ─────────────────────────────────────────────────────────────────────────────
has_runtime() { # DOTNET_PATH
	[ -x "$1" ] && "$1" --list-runtimes 2>/dev/null | grep -q "^Microsoft.AspNetCore.App $RUNTIME_CHANNEL\."
}
DOTNET=""
for candidate in "$INSTALL_DIR/dotnet/dotnet" "$(command -v dotnet 2>/dev/null || true)" "$HOME/.dotnet/dotnet" \
	/usr/share/dotnet/dotnet /usr/lib/dotnet/dotnet /usr/local/share/dotnet/dotnet /opt/homebrew/bin/dotnet; do
	if [ -n "$candidate" ] && has_runtime "$candidate"; then
		DOTNET="$candidate"
		break
	fi
done
if [ -z "$DOTNET" ]; then
	say "Installing the ASP.NET Core $RUNTIME_CHANNEL runtime into $INSTALL_DIR/dotnet"
	fetch "https://dot.net/v1/dotnet-install.sh" "$WORK/dotnet-install.sh" || die "Couldn't download dotnet-install.sh."
	bash "$WORK/dotnet-install.sh" --channel "$RUNTIME_CHANNEL" --runtime aspnetcore --install-dir "$INSTALL_DIR/dotnet" --no-path >/dev/null ||
		die "Installing the .NET runtime failed. Install the ASP.NET Core $RUNTIME_CHANNEL runtime yourself and re-run this."
	DOTNET="$INSTALL_DIR/dotnet/dotnet"
	has_runtime "$DOTNET" || die "The .NET runtime installed but doesn't run on this machine."
fi
printf 'DOTNET=%s\n' "$DOTNET" >"$INSTALL_DIR/install.conf.tmp" && mv -f "$INSTALL_DIR/install.conf.tmp" "$INSTALL_DIR/install.conf"

# ── Launcher ─────────────────────────────────────────────────────────────────────────────────
LAUNCHER_SOURCE="$VERSIONS/$VERSION/Deploy/launcher.sh"
[ -f "$LAUNCHER_SOURCE" ] || die "The package has no Deploy/launcher.sh."
bash -n "$LAUNCHER_SOURCE" || die "The package's launcher.sh has a syntax error."
cp "$LAUNCHER_SOURCE" "$INSTALL_DIR/.launcher.sh.new"
chmod 755 "$INSTALL_DIR/.launcher.sh.new"
mv -f "$INSTALL_DIR/.launcher.sh.new" "$INSTALL_DIR/launcher.sh"

# ── Data: settings and an older install's database ───────────────────────────────────────────
if [ -n "$IMPORT_DIR" ]; then
	[ -d "$IMPORT_DIR" ] || die "No such folder to import from: $IMPORT_DIR"
	for file in .env vibeswarm.db vibeswarm.db-wal vibeswarm.db-shm vibeswarm.pfx; do
		if [ -f "$IMPORT_DIR/$file" ] && [ ! -e "$DATA/$file" ]; then
			cp -p "$IMPORT_DIR/$file" "$DATA/$file"
			say "Imported $file"
		fi
	done
fi

if [ ! -f "$DATA/.env" ]; then
	PORT="${PORT:-5000}"
	if (exec 3<>"/dev/tcp/127.0.0.1/$PORT") 2>/dev/null; then
		die "Port $PORT is already in use. Re-run with --port and a free port."
	fi
	(umask 077 && cat >"$DATA/.env") <<EOF
# $APP_NAME settings. Every option is described in $INSTALL_DIR/current/.env.example.
# Restart the service after changing this file.

# Reachable from other devices on your network. Use http://localhost:$PORT for this machine only.
ASPNETCORE_URLS=http://0.0.0.0:$PORT
EOF
	say "Created $DATA/.env"
fi
PORT="$(sed -n 's/^ASPNETCORE_URLS=.*http:\/\/[^:;]*:\([0-9][0-9]*\).*/\1/p' "$DATA/.env" | head -n 1)"
PORT="${PORT:-5000}"

# SQLite database the launcher backs up before switching versions (none when MySQL is configured).
SQLITE_PATH=""
if ! grep -qiE '^DATABASE_PROVIDER=(mysql|mariadb)' "$DATA/.env"; then
	SQLITE_PATH="$DATA/vibeswarm.db"
fi

# ── Switch versions ──────────────────────────────────────────────────────────────────────────
NEEDS_START=1
if [ -z "$CURRENT_VERSION" ] || [ ! -d "$INSTALL_DIR/current" ]; then
	ln -sfn "versions/$VERSION" "$INSTALL_DIR/current"
elif [ "$CURRENT_VERSION" != "$VERSION" ]; then
	# The launcher applies it on its next start: database backup, trial start, rollback on failure.
	printf 'VERSION=%s\nFROM=%s\nSQLITE_PATH=%s\n' "$VERSION" "$CURRENT_VERSION" "$SQLITE_PATH" >"$STATE/pending.env.tmp"
	mv -f "$STATE/pending.env.tmp" "$STATE/pending.env"
	say "Staged the update from $CURRENT_VERSION to $VERSION"
fi

# ── Service ──────────────────────────────────────────────────────────────────────────────────
SERVICE_READY=0
if [ "$WITH_SERVICE" -eq 1 ] && has_systemd; then
	if [ -f "$UNIT_FILE" ] && ! unit_is_ours; then
		warn "$UNIT_FILE exists but wasn't created by this installer, so it was left alone."
		warn "Remove it (or rename that service) and re-run this to have $APP_NAME start with the machine."
	elif [ -z "$SUDO" ] && [ "$(id -u)" -ne 0 ]; then
		warn "sudo isn't available, so no service was set up."
	else
		say "Setting up the $SERVICE_NAME service (sudo may ask for your password)"
		service_path="$PATH"
		case ":$service_path:" in *":$HOME/.local/bin:"*) ;; *) service_path="$HOME/.local/bin:$service_path" ;; esac
		service_path="${service_path//%/%%}" # systemd reads % as a specifier
		cat >"$WORK/unit" <<EOF
$UNIT_MARKER. Re-run install.sh to change it; edits here are replaced.
[Unit]
Description=$APP_NAME
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=$(id -un)
Group=$(id -gn)
WorkingDirectory=$DATA
ExecStart=$(command -v bash) "$INSTALL_DIR/launcher.sh"
Restart=always
RestartSec=10
KillMode=mixed
TimeoutStopSec=90
SuccessExitStatus=143
Environment="HOME=$HOME"
Environment="PATH=$service_path"
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=DOTNET_NOLOGO=1

[Install]
WantedBy=multi-user.target
EOF
		$SUDO install -m 644 "$WORK/unit" "$UNIT_FILE"
		$SUDO systemctl daemon-reload
		$SUDO systemctl enable "$SERVICE_NAME" >/dev/null 2>&1
		if service_active; then
			$SUDO systemctl restart "$SERVICE_NAME"
		else
			$SUDO systemctl start "$SERVICE_NAME"
		fi
		SERVICE_READY=1
		NEEDS_START=0
	fi
fi

# ── Done ─────────────────────────────────────────────────────────────────────────────────────
addresses="http://localhost:$PORT"
if [ "$OS" = linux ] && command -v hostname >/dev/null 2>&1; then
	for ip in $(hostname -I 2>/dev/null || true); do
		case "$ip" in *:*) ;; *) addresses="$addresses  http://$ip:$PORT" ;; esac
	done
fi

if [ "$SERVICE_READY" -eq 1 ]; then
	say "Waiting for $APP_NAME to start"
	for _ in $(seq 1 60); do
		if (exec 3<>"/dev/tcp/127.0.0.1/$PORT") 2>/dev/null; then break; fi
		sleep 2
	done
fi

echo
say "$APP_NAME $VERSION is installed in $INSTALL_DIR"
echo "    Settings:  $DATA/.env"
if [ "$SERVICE_READY" -eq 1 ]; then
	echo "    Open:      $addresses"
	echo "    Logs:      journalctl -u $SERVICE_NAME -f"
	echo
	echo "    Create your admin account in the browser now: until you do, anyone who can"
	echo "    reach the address above can create it."
elif [ "$NEEDS_START" -eq 1 ]; then
	echo "    Start it:  bash \"$INSTALL_DIR/launcher.sh\""
	echo "    Then open: $addresses"
fi
echo "    Update:    Settings > Updates in $APP_NAME, or run this installer again"
echo "    Remove:    re-run with --uninstall"
