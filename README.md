# VibeSwarm

A vibe-coded vibe coding orchestrator. Imagine CI/CD but with AI. Bring your own provider. Own your own infrastructure.

## Support

This application is free for anyone to use. If you like it and want to donate, we have a donation link set up.

[Donate to North River Design with Stripe](https://donate.stripe.com/3cI00i3SM2X88w55uGaZi00).

Thank you!

## Quick Start

### Prerequisites

- .NET 10 SDK
- Windows, Linux, or macOS
- Git set up
- Your own CLI coding agent

### Supported Agents

- OpenCode
- Claude Code
- GitHub Copilot

## Development

### 1. Clone and Navigate

```bash
git clone <repository-url>
cd VibeSwarm
```

### 2. Configure Authentication

Create a `.env` file in the root directory:

```bash
DEFAULT_ADMIN_USER=admin
DEFAULT_ADMIN_PASS=YourSecurePassword123!
```

**Password Requirements:**

- Minimum 8 characters
- At least one uppercase letter
- At least one lowercase letter
- At least one digit

**Note**: The `.env` file is gitignored to prevent accidentally committing credentials.

### 3. Build and Run

```bash
cd src/VibeSwarm.Web
dotnet run
```

The application will:

- Generate a self-signed HTTPS certificate (first run only)
- Run database migrations
- Create the admin user
- Start on:
  - HTTP: `http://localhost:5000`
  - HTTPS: `https://localhost:5001` (recommended)

### 4. Access the Application

Navigate to `https://localhost:5001`

Your browser will show a certificate warning because we're using a self-signed certificate. This is expected and safe for local/private deployments.

- **Chrome/Edge**: Click "Advanced" → "Proceed to localhost"
- **Firefox**: Click "Advanced" → "Accept the Risk and Continue"

Login with the credentials you set in the `.env` file.

## Running in Production

### Option 1: Direct Execution

```bash
cd src/VibeSwarm.Web
dotnet run --environment Production
```

### Option 2: Published Application

```bash
# Build for production
cd src/VibeSwarm.Web
dotnet publish -c Release -o ../../publish

# Run the published app
cd ../../publish
./VibeSwarm.Web
```

### Environment Variables

Instead of using a `.env` file, you can set environment variables directly:

**Windows (PowerShell):**

```powershell
$env:DEFAULT_ADMIN_USER="admin"
$env:DEFAULT_ADMIN_PASS="YourSecurePassword123!"
$env:ASPNETCORE_ENVIRONMENT="Production"
dotnet run
```

**Linux/macOS:**

```bash
export DEFAULT_ADMIN_USER=admin
export DEFAULT_ADMIN_PASS=YourSecurePassword123!
export ASPNETCORE_ENVIRONMENT=Production
dotnet run
```

## Security

This application is designed to utilize the host system's terminal and command line utilities. You set up your own coding agents. VibeSwarm just calls CLI commands on the host system.

## Database

VibeSwarm uses SQLite for data storage. The database file `vibeswarm.db` is created automatically in the application directory.

**Backup your database:**

```bash
cp vibeswarm.db vibeswarm.db.backup
```

## Troubleshooting

### No Admin User Created

Check the logs on startup. You should see:

```
info: Admin user 'admin' created successfully
info: Database initialized with 1 user(s)
```

If you see a warning about missing credentials:

1. Create a `.env` file with `DEFAULT_ADMIN_USER` and `DEFAULT_ADMIN_PASS`
2. Restart the application

### Certificate Issues

The self-signed certificate is stored as `vibeswarm.pfx` in the application directory.

To regenerate it:

1. Stop the application
2. Delete `vibeswarm.pfx`
3. Restart the application

### Port Already in Use

The application listens on ports 5000 (HTTP) and 5001 (HTTPS). If these are in use, the application will fail to start.

Check what's using the ports:

```bash
# Windows
netstat -ano | findstr :5000

# Linux/macOS
lsof -i :5000
```

## Advanced Configuration

### Custom Database Location

Set the connection string in environment variables:

```bash
export ConnectionStrings__Default="Data Source=/path/to/your/database.db"
```

Or in `appsettings.json`:

```json
{
	"ConnectionStrings": {
		"Default": "Data Source=/custom/path/vibeswarm.db"
	}
}
```

### Accessing from Other Devices

To access the application from other devices on your network, you need to bind to all interfaces:

Set the `ASPNETCORE_URLS` environment variable:

```bash
# Windows
$env:ASPNETCORE_URLS="http://0.0.0.0:5000;https://0.0.0.0:5001"

# Linux/macOS
export ASPNETCORE_URLS="http://0.0.0.0:5000;https://0.0.0.0:5001"
```

Then access via:

- `https://<your-machine-ip>:5001`

**Note**: Your self-signed certificate won't be trusted on other devices. You'll need to accept the certificate warning.

## Running as a Service

### Windows Service

Use NSSM (Non-Sucking Service Manager):

```powershell
# Download NSSM from nssm.cc
nssm install VibeSwarm "C:\path\to\publish\VibeSwarm.Web.exe"
nssm set VibeSwarm AppDirectory "C:\path\to\publish"
nssm set VibeSwarm AppEnvironmentExtra DEFAULT_ADMIN_USER=admin DEFAULT_ADMIN_PASS=YourPass123!
nssm start VibeSwarm
```

### Linux systemd Service

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
Environment=DEFAULT_ADMIN_USER=admin
Environment=DEFAULT_ADMIN_PASS=YourSecurePassword123!

[Install]
WantedBy=multi-user.target
```

Then:

```bash
sudo systemctl daemon-reload
sudo systemctl enable vibeswarm
sudo systemctl start vibeswarm
```

## Development

### Running in Development Mode

```bash
cd src/VibeSwarm.Web
dotnet run
```

Development mode includes:

- Detailed error pages
- Hot reload
- Verbose logging

### Building

```bash
cd src/VibeSwarm.Web
dotnet build
```

### Running Tests

```bash
dotnet test
```

## Project Structure

```
VibeSwarm/
├── build/
|   └── .env               		# Your build configuration (create this)
├── src/
│   ├── VibeSwarm.Shared/ 		# Data models and shared code
│   ├── VibeSwarm.Web/ 			# Blazor Server web application
|   |	├── .env               	# Your local configuration (create this)
|	|	└── .env.example 		# Template for .env file
│   └── VibeSwarm.Worker/       # Background worker services
└── README.md  					# This file
```

## Updates

We are working on an update notification system and providing pre-built solutions. Things change fast. For now, we recommend checking out the `main` branch and running `dotnet publish -c Release -o ./build/`. If you have VibeSwarm installed as a service, restart the process.

## Technologies

- **.NET 10.0** - Web framework
- **Blazor Server** - UI framework
- **SignalR** - Real-time communication
- **Entity Framework Core** - ORM
- **SQLite** - Database
- **ASP.NET Core Identity** - Authentication

## License

VibeSwarm is open sourced under the MIT license and developed by the company, North River Design LLC. The company retains all rights to claim intellectual property of VibeSwarm.

Because this whole application is vibe coded, feel free to fork it. We just need to absolutely specify we are NOT responsible for ANY claims of damage.

## Contributions

If you would like to contribute to VibeSwarm, open a GitHub issue with any problems you are facing. We are an extremely small team (1 person) and rapidly developing this application in a changing landscape.

Any attempt at submitting contributions with nefarious intent will be denied. Be a good person.

This application is being developed to serve our needs and is distributed for free. Any negativitiy will not be tolerated or obliged. This project is open source and you are not entitled to any support or reimbursement for any damages.

## Warranty

VibeSwarm is provided with absolutely no warranty. Use at your own risk. The maintainers are not responsible for any damages or losses resulting from the use of VibeSwarm. Users are encouraged to thoroughly test the application in their own environments before deploying it in production settings.

VibeSwarm is for expert developers who know the limitations and risks of using AI coding agents. It is the user's responsibility to ensure that the generated code meets their quality, security, and compliance standards.

Any and all use of VibeSwarm must comply with the terms of service and usage policies of the AI providers whose agents are utilized. Users are responsible for understanding and adhering to these policies to avoid violations that could lead to account suspension or other penalties.

By using VibeSwarm, users agree to indemnify and hold harmless the maintainers from any claims, damages, or liabilities arising from their use of the application. In short, we are NOT responsible if the AI agent decides to delete important information on your system - it's just a wrapper application.
