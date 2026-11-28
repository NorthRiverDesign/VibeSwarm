[<img src="./src/VibeSwarm.Client/wwwroot/img/logo_color.png" alt="VibeSwarm - AI Agent Orchestrator" width="200" />](https://github.com/NorthRiverDesign/VibeSwarm)

# VibeSwarm

A web dashboard that orchestrates CLI-based AI coding agents. You bring your own tools and infrastructure — VibeSwarm turns ideas into code.

---

## What is VibeSwarm?

VibeSwarm is an agentic CI/CD system for turning ideas into application code. It provides a responsive web UI for managing projects and orchestrating AI coding agents running on your host machine. Think of it as a self-hosted control panel for your AI workforce.

- **Self-hosted** — runs on a Raspberry Pi, VPS, laptop, or cloud instance.
- **Bring your own agents** — install the CLI tools you already use and VibeSwarm detects them automatically.
- **No API keys stored** — authentication lives in your host environment; VibeSwarm just calls CLI commands.

## Supported Agents

| Agent          | CLI Command | Verified against | Minimum | Install                                           |
| -------------- | ----------- | ---------------- | ------- | ------------------------------------------------- |
| Claude Code    | `claude`    | 2.1.326          | 2.0.0   | [claude.ai/code](https://claude.ai/code)          |
| OpenCode       | `opencode`  | 2.3.0            | 1.0.0   | [opencode.ai](https://opencode.ai)                |
| GitHub Copilot | `copilot`   | 1.0.112          | 1.0.0   | [copilot CLI](https://docs.github.com/en/copilot) |

Agents are **auto-detected** at startup. If a supported CLI tool is on your PATH, VibeSwarm registers it as a provider automatically.

### CLI version reference

These CLIs change fast. `ProviderVersionReference` (in `src/VibeSwarm.Shared/Providers/`) is the
single source of truth for the release each integration was built and verified against — last
reviewed **2026-11-17**. Newer CLI features are version-gated, so an older CLI keeps working
with those features switched off rather than failing.

At startup VibeSwarm compares each detected CLI against this reference and logs the result:
a warning when the CLI is below the supported minimum, and an informational line when it is
older or newer than the verified release. When re-verifying against a new CLI, update that
file so the gap between "what we tested" and "what is installed" stays visible.

### Usage limits

Claude Code and GitHub Copilot meter usage against a subscription; VibeSwarm tracks both and
pauses work when a window is exhausted.

- **Claude Code** reports limits as structured `rate_limit_event` messages during a normal
  headless run. VibeSwarm reads the rolling **5-hour** (shown as Session) and **7-day**
  (shown as Weekly) windows, plus whichever limit is currently binding. Warnings printed to
  stderr are parsed as a fallback.
- **GitHub Copilot** meters premium requests and, increasingly, **GitHub AI Credits**.
  VibeSwarm reads the structured usage report written by `--usage-output-file` where the CLI
  supports it, and otherwise falls back to parsing the session summary on stderr.
- **OpenCode** has no quota of its own — metering belongs to whichever model provider you
  configure. Self-hosted open-source models (Ollama, LM Studio, llama.cpp, vLLM and similar)
  are reported as **Unmetered**: no meter is drawn, and they are never paused for exhaustion,
  since there is no window to wait for.

### Claude Code models and advisor

Claude Code providers offer the CLI's aliases (`sonnet`, `opus`, `haiku`, `fable`), which always
run the newest model of each line, followed by pinned model IDs. The list ships with VibeSwarm and
is refreshed shortly after each start, so new models appear without a manual refresh.

A Claude Code provider can also name an **Advisor** (`--advisor`): a stronger model the agent
consults at hard moments, billed at that model's rate. It applies to every run of the provider
(jobs, planning, review and ideas). Running them on Sonnet with Opus or Fable as advisor keeps
most of the work on the cheaper model. Claude Code skips an advisor weaker than the run's model,
and skips Fable until it has been enabled once in an interactive session. Leave it unset to use
Claude Code's own `advisorModel` setting, or pick Off to turn it off for that provider.

### Project memory and session files

Each agent keeps its own memory in its own place, so VibeSwarm keeps one **project memory**
that every agent shares. Edit it under **Instructions and memory** in a project's settings
(up to 20,000 characters). Before each job VibeSwarm writes it to
`.vibeswarm/project-memory.md` in the working directory, tells the agent to read it and add
lasting lessons (not progress, plans or todo lists), and saves the agent's edits back to the
project when the job ends.

Session files never reach your commits. At every job start VibeSwarm lists the agents' own
folders and files (`.vibeswarm/`, `.claude/`, `.opencode/`, `.copilot/`, `CLAUDE.local.md`,
`tasks/todo.md`), Playwright and test output, logs and temp files in the repository's
`.git/info/exclude`. That file is local to your clone, so your `.gitignore` is never changed,
and a run that only leaves such files behind doesn't count as a code change. Agents are also
told to keep plans, todo lists and scratch work out of the repository.

### Browser for agents

Agents can open, click through and screenshot the web apps they build. Every job gets the
[Playwright MCP](https://github.com/microsoft/playwright-mcp) server, driving a headless
Chromium on the host, whether Claude Code, Copilot or OpenCode runs it. Set it up under
**Settings > General > Browser for agents**: it shows whether Node.js and Chromium are
installed, and **Install Playwright and Chromium** installs whatever is missing. Chromium goes
into Playwright's shared cache (`~/.cache/ms-playwright` on Linux), with its system libraries
when VibeSwarm can use `sudo` without a password. A system Chromium (such as Debian's
`chromium` package on a Raspberry Pi) works too. Turn off **Give every job a browser** to keep
it to projects with a web environment.

### Development and release branches

To batch changes and ship them when you choose, set a **Development branch** and a
**Release branch** under **When a job finishes** in a project's settings. Jobs then work on the
development branch, and the project gets a **Release** tab listing the changes that are
waiting. **Deploy** merges the development branch, as it is on origin, into the release
branch and pushes it, so your CI/CD can ship it from there.

If the release branch has changes of its own that conflict, such as a hotfix, nothing is
pushed. VibeSwarm queues a job instead: it merges the release branch into the development
branch and leaves the conflicts for a provider to resolve, and the result is committed as a
merge only once no conflict markers remain. When that job finishes, press **Deploy** again.
Deploys are always manual.

### Profiles and Workflow

A **profile** names one provider, one model and one reasoning level, such as *Deep thinker* or
*Quick fixer*. Wherever VibeSwarm asks who should run something (a job, a retry, a template, a
schedule, auto-pilot, Start All, a role in a project) it asks for a profile rather than for a
provider and a model. Every provider has a
default profile that follows its own default model and reasoning. Add more on the **Profiles**
page, or pick **New profile…** from any profile list. Editing a profile updates everything that
uses it; deleting one moves what used it to its provider's default profile.

Each project's settings show a **Workflow**: the roles that work on it, each Off or on a profile.

- **Build** runs each job. Add fallbacks on other providers for when the first can't run.
- **Plan first** writes a plan before a job builds.
- **Review** checks each job before its work is delivered.
- **Research** and **Decisions** answer the builder's questions while a job runs.
- **Lead** suggests what to build next.

So a project can build on a large model at high reasoning while cheaper profiles review and
advise. Plan first and Lead need Claude Code or Copilot. Review, Research and Decisions also take
an inference profile, such as Grok, which sees only what it is sent rather than the repository.

Two more project settings take an inference profile, or leave the work to the coding provider:
who suggests ideas, and who writes commit messages. A schedule, auto-pilot and Start All run on
the project's Build profile unless you pick another for them.

A job runs on its project's Build profile unless you pick another when you create it. A job
remembers the profile it was asked to run on, and its page shows that name ahead of the provider
and model that ran, which differ once a job has moved to a fallback.

### Review and Lead

Two optional helpers sit above single jobs. Both are roles in the project's Workflow, and both
run read-only: Claude Code or Copilot with their write tools blocked.

- **Review** (off by default) checks each job before its work is delivered. When the agent
  finishes, the project's build and tests run. Then a second model reads the change against the
  goal and the plan's acceptance criteria. The reviewer is a Claude or Copilot CLI that can open
  files, or an inference provider that sees only the diff. Problems go back to the agent in the
  same session, at most twice. Review never holds work back: if the reviewer is off, near its
  usage limit, fails or gives no verdict, the work is delivered and the job says why it wasn't
  reviewed.
- **Lead** suggests what to build next. A Lead review copies the branch jobs deliver to, as it is
  on origin, and reads it alongside the project memory, recent job outcomes, unresolved review
  findings and an optional **Direction** you write. It returns a short report and up to eight
  ideas, each with a goal and acceptance criteria. They wait under **Suggested by Lead** on the
  project page until you approve them. Dismissed ones are never suggested again.

On the Auto-Pilot tab, **Ideas → Only ones I approve** makes auto-pilot run only ideas you typed
or approved. When none are left, it asks the Lead for more, tells you once, and waits.

---

## Quick Start

### Install on Linux or macOS

```bash
curl -fsSL https://raw.githubusercontent.com/NorthRiverDesign/VibeSwarm/main/install.sh | bash
```

Run it as the user whose agent CLIs (`claude`, `copilot`, `opencode`) VibeSwarm should drive, not as
root. The installer:

- downloads the newest release and checks its SHA-256 against the release's `SHA256SUMS`;
- adds the ASP.NET Core runtime the release needs if this machine doesn't have it, inside the
  install folder (nothing system-wide);
- on Linux with systemd, sets up a `vibeswarm` service that starts with the machine and runs as you
  (sudo asks for your password once);
- prints the address to open. **Create your admin account straight away**: until you do, anyone who
  can reach that address can create it.

Everything lives in `~/.vibeswarm`:

```
~/.vibeswarm/
├── launcher.sh      # what the service runs; applies updates and rolls back failed ones
├── current -> versions/1.4.0
├── versions/        # one folder per release, never edited once installed
├── data/            # .env, vibeswarm.db and the HTTPS certificate: your settings and data
├── backups/         # the SQLite database as it was before each update
└── dotnet/          # the .NET runtime, only when the installer had to add it
```

Options go after `bash -s --` when piped, for example `curl -fsSL …/install.sh | bash -s -- --port 8080`:

| Option | |
| --- | --- |
| `--port 8080` | Port for a new install (default 5000) |
| `--dir PATH` | Install somewhere other than `~/.vibeswarm` |
| `--version v1.2.0` | A specific release instead of the newest |
| `--no-service` | Skip systemd; start it with `bash ~/.vibeswarm/launcher.sh` |
| `--import PATH` | Bring `.env`, `vibeswarm.db` and the certificate over from an older install |
| `--uninstall` | Remove the service and app, keeping `data/` (add `--purge` to delete it too) |

Running the installer again is safe: it repairs the launcher and service and installs a newer
release if there is one, keeping `data/` as it is. Logs: `journalctl -u vibeswarm -f`.

On Windows, download the `.zip` from [Releases](https://github.com/NorthRiverDesign/VibeSwarm/releases),
install the [ASP.NET Core 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0), and run
`dotnet VibeSwarm.Web.dll` in the unzipped folder.

### Run from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
git clone https://github.com/NorthRiverDesign/VibeSwarm.git
cd VibeSwarm
dotnet run --project src/VibeSwarm.Web
```

The default setup uses SQLite and the browser-based setup wizard, so you can start the app without
creating a config file first. Copy `src/VibeSwarm.Web/.env.example` to `.env` only when you want to
change ports, pre-create the first admin account, or switch databases.

On start the app runs database migrations, detects installed CLI agents and listens on
**http://localhost:5000**. On first launch you are sent to a setup wizard to create your admin
account. For automated deployments, set `DEFAULT_ADMIN_PASS` and optionally `DEFAULT_ADMIN_USER`
in `.env` before starting.

A build from source reports itself as "Built from source" and is never offered a release update:
update it with `git pull` and publish again.

### HTTPS

To also serve HTTPS, turn on **Settings → Connection → Serve over HTTPS** and restart VibeSwarm. It then generates a self-signed certificate and listens on **https://localhost:5001** as well. Your browser will show a certificate warning until you trust it (devices can install it from `/cert`):

- **Chrome/Edge**: Click "Advanced" → "Proceed to localhost"
- **Firefox**: Click "Advanced" → "Accept the Risk and Continue"

Browsers only allow installing VibeSwarm as an app and keeping the screen awake over HTTPS (or on `localhost`).

---

## Configuration

The `.env` file is the **only** configuration you need. An installed copy reads `~/.vibeswarm/data/.env`; from source, place it in the repo root or in `src/VibeSwarm.Web/`.

| Variable                     | Default                                        | Description                                              |
| ---------------------------- | ---------------------------------------------- | -------------------------------------------------------- |
| `ASPNETCORE_URLS`            | `http://localhost:5000`                        | Bind addresses. Use `0.0.0.0` for remote access. `https://` ones need HTTPS on in Settings. |
| `DEFAULT_ADMIN_USER`         | `admin` when only a password is provided       | Optional admin username for automated setup.             |
| `DEFAULT_ADMIN_PASS`         | _(empty — setup wizard)_                       | Admin password. Min 8 chars, upper + lower + digit.      |
| `DATABASE_PROVIDER`          | `sqlite`                                       | Database engine: `sqlite` or `mysql`.                    |
| `ConnectionStrings__Default` | `Data Source=vibeswarm.db`                     | Connection string for the chosen provider.               |
| `Updates__AutoCheck`         | `true`                                         | Look for a new release twice a day.                      |
| `Updates__IncludePrereleases`| `false`                                        | Offer pre-releases (`v1.3.0-beta.1`) too.                |
| `Updates__Repository`        | `NorthRiverDesign/VibeSwarm`                   | The GitHub repository releases come from (for forks).    |

You can also set these as system environment variables instead of using `.env`.

---

## Database Options

**SQLite (default)** — zero configuration. The file `vibeswarm.db` is created next to the app.

Migrations run automatically on startup and build the schema from empty, so a new install
works on either provider with no manual setup.

**MySQL:**

```bash
DATABASE_PROVIDER=mysql
ConnectionStrings__Default=Server=localhost;Database=vibeswarm;User=vibeswarm;Password=secret
```

Provider aliases are supported: `mariadb` / `mysql`.

You can also switch databases from **Settings → Database**. That flow copies your current VibeSwarm
data into an empty target database, writes the new provider and connection string to a runtime
config file outside the repo, and takes effect after restart.

### Backup (SQLite)

```bash
cp vibeswarm.db vibeswarm.db.backup
```

### Migrations

EF Core bakes provider-specific SQL into a migration when it is generated — SQLite emits
`TEXT` columns where MySQL needs `char(36)` — so one migration set cannot serve both
providers. VibeSwarm therefore keeps a set per provider:

```
src/VibeSwarm.Web/Data/Migrations/
├── Sqlite/     # owned by SqliteVibeSwarmDbContext
└── MySql/      # owned by MySqlVibeSwarmDbContext
```

`VibeSwarmDbContext` itself owns no migrations. Everything that applies them goes through
`DataServiceExtensions.CreateMigrationContext`, which picks the set matching the configured
provider.

**Every schema change must be generated for both providers:**

```bash
dotnet ef migrations add <Name> --project src/VibeSwarm.Web \
  --context SqliteVibeSwarmDbContext --output-dir Data/Migrations/Sqlite
dotnet ef migrations add <Name> --project src/VibeSwarm.Web \
  --context MySqlVibeSwarmDbContext  --output-dir Data/Migrations/MySql
```

`MigrationSetsStayInStepTests` fails the build if one set drifts from the other, so a
forgotten provider is caught by `dotnet test` rather than by a failed deployment.

On MySQL, string columns with a `MaxLength` of 1000 or more are mapped to `LONGTEXT`.
InnoDB caps a row at 65,535 bytes and utf8mb4 costs 4 bytes per character, so without this
the `AppSettings`, `Jobs` and `CriticalErrorLogs` tables exceed the limit and cannot be
created. `MaxLength` is still enforced by EF validation.

---

## Running as a Service

The installer sets up the service for you. To run a copy you published yourself under systemd,
create `/etc/systemd/system/vibeswarm.service`. Run it as the user whose agent CLIs are signed in,
and give it that user's `HOME` and `PATH`, or the CLIs won't be found:

```ini
[Unit]
Description=VibeSwarm
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=you
WorkingDirectory=/home/you/vibeswarm
ExecStart=/usr/bin/dotnet /home/you/vibeswarm/VibeSwarm.Web.dll
Restart=always
RestartSec=10
TimeoutStopSec=90
Environment=HOME=/home/you
Environment=PATH=/home/you/.local/bin:/usr/local/bin:/usr/bin:/bin
Environment=ASPNETCORE_ENVIRONMENT=Production

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now vibeswarm
```

The `.env` and the SQLite database sit in the working directory, so publishing a new build over
that folder keeps them. One-click updates need the installer's layout.

### Windows (NSSM)

```powershell
# Download NSSM from nssm.cc
nssm install VibeSwarm "C:\Program Files\dotnet\dotnet.exe" "C:\path\to\VibeSwarm\VibeSwarm.Web.dll"
nssm set VibeSwarm AppDirectory "C:\path\to\VibeSwarm"
nssm start VibeSwarm
```

---

## Security

- VibeSwarm serves plain HTTP by default. Turning on Settings → Connection → Serve over HTTPS adds HTTPS with a self-signed certificate. For production, place it behind a reverse proxy with a real certificate.
- The application calls CLI tools on the host system. It does **not** store API keys — your agents authenticate through their own configurations.
- All user passwords are hashed with ASP.NET Core Identity defaults.

---

## Troubleshooting

### Certificate Warning

Expected with self-signed certificates, which VibeSwarm only uses while Settings → Connection → Serve over HTTPS is on. Turn it off and restart to serve HTTP only, accept the warning in your browser, or put a real certificate in front with a reverse proxy.

### Port Already in Use

```bash
# Windows
netstat -ano | findstr :5000

# Linux/macOS
lsof -i :5000
```

Change the port in `.env` via `ASPNETCORE_URLS`.

### Agent Not Detected

- Verify the CLI tool is on your PATH: `claude --version`, `opencode --version`, `copilot --version`
- Check the application logs for detection results, including how each CLI's version compares
  to the release VibeSwarm was verified against.
- You can always add agents manually through the web UI under Providers.

---

## Development

### Prerequisites

- .NET 10 SDK
- Windows, Linux, or macOS
- Git

### Build

```bash
dotnet build VibeSwarm.sln
```

### Run (Development)

```bash
cd src/VibeSwarm.Web
dotnet run
```

### Run Tests

```bash
dotnet test
```

### Publish

```bash
dotnet publish src/VibeSwarm.Web/VibeSwarm.Web.csproj -c Release -o ./build/vibeswarm /p:UseAppHost=false
```

### Project Structure

```
VibeSwarm/
├── src/
│   ├── VibeSwarm.Client/     # Blazor WebAssembly front-end
│   ├── VibeSwarm.Shared/     # Shared models, services, utilities
│   └── VibeSwarm.Web/        # Server — API, SignalR, Identity, CLI orchestration
│       ├── Deploy/launcher.sh # Runs installed copies; applies and rolls back updates
│       └── .env.example      # Configuration template
├── install.sh                # The installer
├── build/                    # Published output
└── README.md
```

### Technologies

- **.NET 10.0** - Web framework
- **Blazor WebAssembly** - UI
- **SignalR** - Real-time communication
- **Entity Framework Core** - ORM (SQLite, MySQL)
- **Provider CLIs** - Claude Code 2.1.276, OpenCode 1.18.31, GitHub Copilot 1.0.86
- **ASP.NET Core Identity** - Authentication

---

## Updates

**Settings → Updates** shows the installed version and the newest release on GitHub. VibeSwarm
checks twice a day (turn it off with `Updates__AutoCheck=false`); **Check now** asks straight away.

On an install made by the installer, **Update** installs the new release:

1. It downloads the release's `.tar.gz`, checks its SHA-256 against the digest GitHub publishes
   (or the release's `SHA256SUMS`), unpacks it into a new `versions/<version>` folder, and checks it
   is the version the release says and that this machine has the .NET runtime it needs. Nothing
   in use is touched; a failed check removes the folder and changes nothing.
2. It pauses the job queue and waits for running jobs, and jobs waiting for your answer, to finish.
   You can cancel until then, and the queue resumes.
3. It restarts. The launcher backs up the SQLite database to `backups/`, points `current` at the
   new version and starts it. The new version confirms itself once it is serving, then resumes the
   queue and removes releases older than the previous one.
4. If the new version exits or hasn't finished starting within five minutes, the launcher restores
   the database backup and the previous version and starts that instead. Settings shows what
   happened.

Your `.env`, database and certificate are in `data/` and are never replaced. With MySQL, back the
database up yourself first: a rollback restores the app, not a MySQL database.

The installer works the same way: running it again stages the newest release and restarts the
service through the launcher, so it rolls back too (but doesn't wait for running jobs).

Copies that weren't installed by the installer (a build from source, a hand-copied release, or
Windows) see when a release is out but update by hand. Re-run the installer with
`--import <old folder>` to move one onto the installer's layout.

### Publishing a release

Push a tag such as `v1.2.0` (or `v1.3.0-beta.1` for a pre-release). The Release workflow stamps that
version into the build, runs the tests, and publishes `vibeswarm-v1.2.0.tar.gz` (Linux and macOS),
`vibeswarm-v1.2.0.zip` (Windows) and `SHA256SUMS`. Builds without a tag report `0.0.0-dev`.

The names an install uses on disk and on GitHub (the archive prefix, the `~/.vibeswarm` folder, the
`vibeswarm` service and the repository) come from `AppBranding.PackageId` and
`AppBranding.ReleaseRepository` in `src/VibeSwarm.Shared/Branding/AppBranding.cs`, with copies in
`install.sh` and `.github/workflows/release.yml`. `InstallerBrandingTests` fails when they drift.
Settle them before the first public release: changing them later strands existing installs on the
old folder and service names.

## License

VibeSwarm is open sourced under the MIT license and developed by the company, North River Design LLC. The company retains all rights to claim intellectual property of VibeSwarm.

Because this whole application is vibe coded, feel free to fork it. We just need to absolutely specify we are NOT responsible for ANY claims of damage.

## Contributions

If you would like to contribute to VibeSwarm, open a GitHub issue with any problems you are facing. We are an extremely small team (1 person) and rapidly developing this application in a changing landscape.

Any attempt at submitting contributions with nefarious intent will be denied. Be a good person.

This application is being developed to serve our needs and is distributed for free. Any negativity will not be tolerated or obliged. This project is open source and you are not entitled to any support or reimbursement for any damages.

## Warranty

VibeSwarm is provided with absolutely no warranty. Use at your own risk. The maintainers are not responsible for any damages or losses resulting from the use of VibeSwarm. Users are encouraged to thoroughly test the application in their own environments before deploying it in production settings.

VibeSwarm is for expert developers who know the limitations and risks of using AI coding agents. It is the user's responsibility to ensure that the generated code meets their quality, security, and compliance standards.

Any and all use of VibeSwarm must comply with the terms of service and usage policies of the AI providers whose agents are utilized. Users are responsible for understanding and adhering to these policies to avoid violations that could lead to account suspension or other penalties.

By using VibeSwarm, users agree to indemnify and hold harmless the maintainers from any claims, damages, or liabilities arising from their use of the application. In short, we are NOT responsible if the AI agent decides to delete important information on your system — it's just a wrapper application.

## Support

This application is free for anyone to use. If you like it and want to donate, we have a donation link set up.

[Donate to North River Design with Stripe](https://donate.stripe.com/3cI00i3SM2X88w55uGaZi00).

Thank you!
