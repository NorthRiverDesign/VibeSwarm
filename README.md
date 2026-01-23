# VibeSwarm

A Blazor Server application for managing background jobs and tasks, designed to run on Raspberry Pi and accessible over your local network.

## Quick Start

### Development (VSCode)

Press **F5** to start debugging. The application will:
- Build automatically
- Start on `http://localhost:5000` and `https://localhost:5001`
- Open in your default browser

**To access from other devices on your network during development:**

Set the environment variable before running:
```bash
# Windows (PowerShell)
$env:ASPNETCORE_URLS="http://0.0.0.0:5000;https://0.0.0.0:5001"

# Windows (CMD)
set ASPNETCORE_URLS=http://0.0.0.0:5000;https://0.0.0.0:5001

# Linux/Mac
export ASPNETCORE_URLS=http://0.0.0.0:5000;https://0.0.0.0:5001
```

Or temporarily edit [appsettings.Development.json](src/VibeSwarm.Web/appsettings.Development.json) to use `0.0.0.0` instead of `localhost`.

### Raspberry Pi Deployment

#### Initial Setup

1. **Clone the repository** on your Raspberry Pi:
   ```bash
   cd ~
   git clone <your-repo-url> VibeSwarm
   cd VibeSwarm
   ```

2. **Make scripts executable**:
   ```bash
   chmod +x publish.sh start-vibeswarm.sh
   ```

3. **Install .NET Runtime** (if not already installed):
   ```bash
   curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 10.0 --runtime aspnetcore
   ```

#### Building and Running

**Option 1: Quick Start (Recommended)**

1. **Publish the application**:
   ```bash
   ./publish.sh
   ```

2. **Start the application**:
   ```bash
   ./start-vibeswarm.sh
   ```

The application will be accessible at:
- `http://localhost:5000` (from the Pi)
- `http://<pi-ip-address>:5000` (from other devices on your network)

**Option 2: Manual Commands**

```bash
# Publish
dotnet publish src/VibeSwarm.Web/VibeSwarm.Web.csproj -c Release -o build

# Run
cd build
ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS=http://0.0.0.0:5000 ./VibeSwarm.Web
```

#### Running as a System Service

To run VibeSwarm as a background service that starts automatically:

1. **Edit the service file** to match your installation path:
   ```bash
   nano vibeswarm.service
   ```

   Update these lines if your path is different:
   ```
   User=pi
   WorkingDirectory=/home/pi/VibeSwarm/build
   ExecStart=/home/pi/VibeSwarm/build/VibeSwarm.Web
   ```

2. **Install the service**:
   ```bash
   sudo cp vibeswarm.service /etc/systemd/system/
   sudo systemctl daemon-reload
   sudo systemctl enable vibeswarm
   sudo systemctl start vibeswarm
   ```

3. **Check service status**:
   ```bash
   sudo systemctl status vibeswarm
   ```

4. **View logs**:
   ```bash
   sudo journalctl -u vibeswarm -f
   ```

5. **Stop/restart service**:
   ```bash
   sudo systemctl stop vibeswarm
   sudo systemctl restart vibeswarm
   ```

## Project Structure

```
VibeSwarm/
├── src/
│   ├── VibeSwarm.Web/          # Main web application (Blazor Server)
│   ├── VibeSwarm.Shared/       # Shared data models and services
│   └── VibeSwarm.Worker/       # Background worker services
├── build/                      # Published output (created by publish script)
├── publish.sh                  # Linux/Mac publish script
├── publish.bat                 # Windows publish script
├── start-vibeswarm.sh          # Raspberry Pi startup script
└── vibeswarm.service           # systemd service configuration
```

## Configuration

### Network Binding

- **Development**: [appsettings.Development.json](src/VibeSwarm.Web/appsettings.Development.json)
  - HTTP: `http://localhost:5000`
  - HTTPS: `https://localhost:5001`
  - Use `ASPNETCORE_URLS` environment variable to override with `0.0.0.0` for network access

- **Production** (Raspberry Pi): [appsettings.Production.json](src/VibeSwarm.Web/appsettings.Production.json)
  - HTTP: `http://0.0.0.0:5000`
  - Accessible from any device on your local network

### Database

The application uses SQLite by default:
- Database file: `vibeswarm.db` (created automatically in the application directory)
- Migrations run automatically on startup

### Environment Variables

You can override settings using environment variables:

```bash
export ASPNETCORE_ENVIRONMENT=Production
export ASPNETCORE_URLS=http://0.0.0.0:5000
export ConnectionStrings__Default="Data Source=vibeswarm.db"
```

## Building from Windows

Use the provided batch script:

```cmd
publish.bat
```

Then copy the `build` folder to your Raspberry Pi and run `./start-vibeswarm.sh`.

## Updating on Raspberry Pi

```bash
cd ~/VibeSwarm
git pull
./publish.sh

# If running as a service:
sudo systemctl restart vibeswarm

# If running manually:
./start-vibeswarm.sh
```

## Troubleshooting

### Assets not loading

If CSS or JavaScript files aren't loading:

1. Ensure you published in Release mode: `dotnet publish -c Release`
2. Check that the `wwwroot` folder exists in the `build` directory
3. Verify the application is running in Production mode: `echo $ASPNETCORE_ENVIRONMENT`

### Cannot access from other devices

1. Check your firewall settings on the Raspberry Pi:
   ```bash
   sudo ufw allow 5000/tcp
   ```

2. Verify the application is listening on `0.0.0.0`:
   ```bash
   sudo netstat -tuln | grep 5000
   ```

   You should see: `0.0.0.0:5000`

3. Find your Pi's IP address:
   ```bash
   hostname -I
   ```

### Service won't start

1. Check service logs:
   ```bash
   sudo journalctl -u vibeswarm -n 50 --no-pager
   ```

2. Verify file permissions:
   ```bash
   ls -la ~/VibeSwarm/build/VibeSwarm.Web
   chmod +x ~/VibeSwarm/build/VibeSwarm.Web
   ```

3. Check that .NET runtime is installed:
   ```bash
   dotnet --info
   ```

## Technologies

- **.NET 10.0** - Web framework
- **Blazor Server** - UI framework
- **SignalR** - Real-time communication
- **Entity Framework Core** - ORM
- **SQLite** - Database

## Development

### Prerequisites

- .NET 10.0 SDK
- Visual Studio Code with C# extension

### Running Tests

```bash
dotnet test
```

### VSCode Tasks

Available tasks (Ctrl+Shift+P → "Tasks: Run Task"):
- **build** - Build the solution
- **publish** - Publish in Release mode to `build/` folder
- **watch** - Run with hot reload

## License

[Your License Here]
