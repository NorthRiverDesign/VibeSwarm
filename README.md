[<img src="./src/VibeSwarm.Client/wwwroot/img/logo_color.png" alt="VibeSwarm - AI Agent Orchestrator" width="200" />](https://github.com/NorthRiverDesign/VibeSwarm)

# VibeSwarm

A web dashboard that orchestrates CLI-based AI coding agents. You bring your own tools and infrastructure — VibeSwarm turns ideas into code.

## Support

This application is free for anyone to use. If you like it and want to donate, we have a donation link set up.

[Donate to North River Design with Stripe](https://donate.stripe.com/3cI00i3SM2X88w55uGaZi00).

Thank you!

---

## What is VibeSwarm?

VibeSwarm is an agentic CI/CD system for turning ideas into application code. It provides a responsive web UI for managing projects and orchestrating AI coding agents running on your host machine. Think of it as a self-hosted control panel for your AI workforce.

- **Self-hosted** — runs on a Raspberry Pi, VPS, laptop, or cloud instance.
- **Bring your own agents** — install the CLI tools you already use and VibeSwarm detects them automatically.
- **No API keys stored** — authentication lives in your host environment; VibeSwarm just calls CLI commands.

## Supported Agents

| Agent          | CLI Command | Install                                           |
| -------------- | ----------- | ------------------------------------------------- |
| Claude Code    | `claude`    | [claude.ai/code](https://claude.ai/code)          |
| OpenCode       | `opencode`  | [opencode.ai](https://opencode.ai)                |
| GitHub Copilot | `copilot`   | [copilot CLI](https://docs.github.com/en/copilot) |

Agents are **auto-detected** at startup. If a supported CLI tool is on your PATH, VibeSwarm registers it as a provider automatically.

---

## Quick Start

### 1. Install .NET 9 Runtime

Download from [dot.net/download](https://dotnet.microsoft.com/download/dotnet/9.0). Install the **ASP.NET Core Runtime** for your platform.

### 2. Clone the Repository

```bash
git clone https://github.com/NorthRiverDesign/VibeSwarm.git
cd VibeSwarm
```

### 3. Copy the Example Configuration

```bash
cp src/VibeSwarm.Web/.env.example .env
```

Edit `.env` if needed — the defaults work for local development.

### 4. Run

```bash
cd src/VibeSwarm.Web
dotnet run
```

The application will:

- Generate a self-signed HTTPS certificate (first run only)
- Run database migrations (SQLite by default, zero configuration)
- Auto-detect installed CLI agents
- Start on **https://localhost:5001** and **http://localhost:5000**

### 5. Open Your Browser

Navigate to `https://localhost:5001`

Your browser will show a certificate warning (self-signed cert). This is expected:

- **Chrome/Edge**: Click "Advanced" → "Proceed to localhost"
- **Firefox**: Click "Advanced" → "Accept the Risk and Continue"

On first launch you will be redirected to a setup wizard to create your admin account. Alternatively, set `DEFAULT_ADMIN_USER` and `DEFAULT_ADMIN_PASS` in your `.env` file for automated deployments.

---

## Configuration

The `.env` file is the **only** configuration you need. Place it in the repo root or in `src/VibeSwarm.Web/`.

| Variable                     | Default                                        | Description                                              |
| ---------------------------- | ---------------------------------------------- | -------------------------------------------------------- |
| `ASPNETCORE_URLS`            | `https://localhost:5001;http://localhost:5000` | Bind addresses. Use `0.0.0.0` for remote access.         |
| `DEFAULT_ADMIN_USER`         | _(empty — setup wizard)_                       | Admin username for automated setup.                      |
| `DEFAULT_ADMIN_PASS`         | _(empty — setup wizard)_                       | Admin password. Min 8 chars, upper + lower + digit.      |
| `DATABASE_PROVIDER`          | `sqlite`                                       | Database engine: `sqlite`, `postgresql`, or `sqlserver`. |
| `ConnectionStrings__Default` | `Data Source=vibeswarm.db`                     | Connection string for the chosen provider.               |

You can also set these as system environment variables instead of using `.env`.

---

## Database Options

**SQLite (default)** — zero configuration. The file `vibeswarm.db` is created next to the app.

**PostgreSQL:**

```bash
DATABASE_PROVIDER=postgresql
ConnectionStrings__Default=Host=localhost;Database=vibeswarm;Username=vibeswarm;Password=secret
```

**SQL Server:**

```bash
DATABASE_PROVIDER=sqlserver
ConnectionStrings__Default=Server=localhost;Database=vibeswarm;Trusted_Connection=true;TrustServerCertificate=true
```

Provider aliases are supported: `postgres` / `postgresql`, `mssql` / `sqlserver`.

### Backup (SQLite)

```bash
cp vibeswarm.db vibeswarm.db.backup
```

---

## Running as a Service

### Linux (systemd)

Create `/etc/systemd/system/vibeswarm.service`:

```ini
[Unit]
Description=VibeSwarm
After=network.target

[Service]
Type=notify
WorkingDirectory=/opt/vibeswarm
ExecStart=/opt/vibeswarm/VibeSwarm.Web
Restart=always
RestartSec=10
KillSignal=SIGINT
Environment=ASPNETCORE_URLS=https://0.0.0.0:5001;http://0.0.0.0:5000
Environment=ASPNETCORE_ENVIRONMENT=Production

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable vibeswarm
sudo systemctl start vibeswarm
```

### Windows (NSSM)

```powershell
# Download NSSM from nssm.cc
nssm install VibeSwarm "C:\path\to\publish\VibeSwarm.Web.exe"
nssm set VibeSwarm AppDirectory "C:\path\to\publish"
nssm start VibeSwarm
```

---

## Security

- VibeSwarm generates a self-signed HTTPS certificate on first run. For production, place behind a reverse proxy with a real certificate.
- The application calls CLI tools on the host system. It does **not** store API keys — your agents authenticate through their own configurations.
- All user passwords are hashed with ASP.NET Core Identity defaults.

---

## Troubleshooting

### Certificate Warning

Expected with self-signed certificates. Accept the warning in your browser, or replace with a real cert behind a reverse proxy.

### Port Already in Use

```bash
# Windows
netstat -ano | findstr :5001

# Linux/macOS
lsof -i :5001
```

Change the port in `.env` via `ASPNETCORE_URLS`.

### Agent Not Detected

- Verify the CLI tool is on your PATH: `claude --version`, `opencode --version`, `copilot --version`
- Check the application logs for detection results.
- You can always add agents manually through the web UI under Providers.

---

## Development

### Prerequisites

- .NET 9 SDK
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
dotnet publish src/VibeSwarm.Web/VibeSwarm.Web.csproj -c Release -o ./build/
```

### Project Structure

```
VibeSwarm/
├── src/
│   ├── VibeSwarm.Client/     # Blazor WebAssembly front-end
│   ├── VibeSwarm.Shared/     # Shared models, services, utilities
│   └── VibeSwarm.Web/        # Server — API, SignalR, Identity, CLI orchestration
│       └── .env.example      # Configuration template
├── build/                    # Published output
└── README.md
```

### Technologies

- **.NET 9.0** - Web framework
- **Blazor WebAssembly** - UI
- **SignalR** - Real-time communication
- **Entity Framework Core** - ORM (SQLite, PostgreSQL, SQL Server)
- **ASP.NET Core Identity** - Authentication

---

## Updates

We are working on an update notification system and providing pre-built solutions. Things change fast. For now, we recommend checking out the `main` branch and running `dotnet publish -c Release -o ./build/`. If you have VibeSwarm installed as a service, restart the process.

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
