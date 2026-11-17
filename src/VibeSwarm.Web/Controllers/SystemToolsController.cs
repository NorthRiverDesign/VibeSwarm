using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VibeSwarm.Shared.Utilities;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Web.Controllers;

[ApiController]
[Route("api/system-tools")]
[Authorize]
public class SystemToolsController : ControllerBase
{
    // One install at a time: two apt-get runs fight over the dpkg lock, and a second tap from a
    // phone that lost track of the first install shouldn't start another.
    private static readonly SemaphoreSlim InstallGate = new(1, 1);
    private static volatile string? _installingTool;

    [HttpGet("detected")]
    public async Task<IActionResult> GetDetectedTools(CancellationToken ct)
    {
        var nodeResult = await RunShellCommandAsync("node --version", TimeSpan.FromSeconds(10), ct);
        var nodeVersion = nodeResult.Success ? nodeResult.Output?.Trim() : null;

        var npxResult = await RunShellCommandAsync("npx --version", TimeSpan.FromSeconds(10), ct);
        var npxAvailable = npxResult.Success;

        // The same lookup jobs use, so "installed" here means agents get this browser
        var chromiumPath = BrowserToolsLocator.FindChromiumExecutable();
        var chromiumVersion = chromiumPath is null ? null : await GetChromiumVersionAsync(chromiumPath, ct);

        return Ok(new
        {
            NodeAvailable = nodeResult.Success,
            NodeVersion = nodeVersion,
            NpxAvailable = npxAvailable,
            PlaywrightBrowsersInstalled = chromiumPath is not null,
            PlaywrightStatus = chromiumPath is not null
                ? $"{chromiumVersion ?? "Chromium"} at {chromiumPath}"
                : "Chromium browser not installed",
            ChromiumPath = chromiumPath,
            ChromiumVersion = chromiumVersion,
            InstallingTool = _installingTool
        });
    }

    [HttpPost("install/{tool}")]
    public async Task<IActionResult> InstallTool(string tool)
    {
        var command = GetInstallCommand(tool);
        if (command is null)
            return BadRequest(new { Success = false, Error = $"Unknown tool: {tool}" });

        if (!await InstallGate.WaitAsync(0))
            return Conflict(new { Success = false, Error = $"Another install ({_installingTool}) is still running. Check again once it finishes." });

        try
        {
            _installingTool = tool.ToLowerInvariant();
            var timeout = _installingTool == "playwright" ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(5);
            // Not tied to the request: a phone that sleeps mid-install drops the connection, and the
            // installer should still finish while holding the gate.
            var result = await RunShellCommandAsync(command, timeout, CancellationToken.None);
            return Ok(new { result.Success, result.Output, result.Error });
        }
        finally
        {
            _installingTool = null;
            InstallGate.Release();
        }
    }

    [HttpGet("install-info/{tool}")]
    public IActionResult GetInstallInfo(string tool)
    {
        var command = GetInstallCommand(tool);
        if (command is null)
            return BadRequest(new { Error = $"Unknown tool: {tool}" });

        return Ok(new { Command = command });
    }

    private static string? GetInstallCommand(string tool) =>
        tool.ToLowerInvariant() switch
        {
            "git" => GetGitInstallCommand(),
            "gh" => GetGhInstallCommand(),
            "nodejs" => GetNodeJsInstallCommand(),
            "playwright" => GetPlaywrightInstallCommand(),
            _ => null
        };

    private static string GetGitInstallCommand()
    {
        if (OperatingSystem.IsWindows())
            return "winget install --id Git.Git -e --source winget";
        if (OperatingSystem.IsMacOS())
            return "brew install git";
        return "sudo DEBIAN_FRONTEND=noninteractive apt-get install -y git";
    }

    private static string GetGhInstallCommand()
    {
        if (OperatingSystem.IsWindows())
            return "winget install --id GitHub.cli -e --source winget";
        if (OperatingSystem.IsMacOS())
            return "brew install gh";
        // Official GitHub CLI apt repository method for Debian/Ubuntu/Raspberry Pi OS
        return "(type -p wget >/dev/null || (sudo apt update && sudo apt-get install wget -y)) " +
               "&& sudo mkdir -p -m 755 /etc/apt/keyrings " +
               "&& wget -nv -O- https://cli.github.com/packages/githubcli-archive-keyring.gpg | sudo tee /etc/apt/keyrings/githubcli-archive-keyring.gpg > /dev/null " +
               "&& sudo chmod go+r /etc/apt/keyrings/githubcli-archive-keyring.gpg " +
               "&& echo \"deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/githubcli-archive-keyring.gpg] https://cli.github.com/packages stable main\" | sudo tee /etc/apt/sources.list.d/github-cli.list > /dev/null " +
               "&& sudo apt update " +
               "&& sudo apt install gh -y";
    }

    private static string GetNodeJsInstallCommand()
    {
        if (OperatingSystem.IsWindows())
            return "winget install --id OpenJS.NodeJS.LTS -e --source winget";
        if (OperatingSystem.IsMacOS())
            return "brew install node";
        // Installs Node.js LTS via NodeSource for Debian/Ubuntu/Raspberry Pi OS (ARM64 supported)
        return "sudo DEBIAN_FRONTEND=noninteractive apt-get update && sudo DEBIAN_FRONTEND=noninteractive apt-get install -y nodejs npm";
    }

    internal static string GetPlaywrightInstallCommand()
    {
        // Downloads Chromium into the shared Playwright cache that jobs read (BrowserToolsLocator), then
        // fetches the Playwright MCP package so the first job doesn't wait on npm.
        const string fetchMcpServer = "npx -y @playwright/mcp@latest --version";
        if (OperatingSystem.IsWindows())
            return $"npx -y playwright@latest install chromium; if ($?) {{ {fetchMcpServer} }}";

        var installNode = $"(command -v npx >/dev/null 2>&1 || ({GetNodeJsInstallCommand()}))";
        if (OperatingSystem.IsMacOS())
            return $"{installNode} && npx -y playwright@latest install chromium && {fetchMcpServer}";

        // --with-deps apt-installs the libraries Chromium needs (libgbm, libasound, ...), which takes root.
        // Without passwordless sudo, sudo would wait on a password nobody can type, so skip it.
        return $"{installNode} && " +
               "if [ \"$(id -u)\" -eq 0 ] || sudo -n true 2>/dev/null; " +
               "then npx -y playwright@latest install --with-deps chromium; " +
               "else npx -y playwright@latest install chromium; fi " +
               $"&& {fetchMcpServer}";
    }

    private static async Task<string?> GetChromiumVersionAsync(string chromiumPath, CancellationToken ct)
    {
        // chrome.exe is a GUI app that prints nothing for --version
        if (OperatingSystem.IsWindows())
            return null;

        var quotedPath = $"'{chromiumPath.Replace("'", "'\\''", StringComparison.Ordinal)}'";
        var result = await RunShellCommandAsync($"{quotedPath} --version", TimeSpan.FromSeconds(10), ct);
        var version = result.Success ? result.Output?.Trim().Split('\n')[0].Trim() : null;
        if (string.IsNullOrEmpty(version))
            return null;

        // "Chromium 154.0.8037.92 built on Debian GNU/Linux 13 (trixie)" reads fine without the build note
        var builtOn = version.IndexOf(" built on ", StringComparison.Ordinal);
        return builtOn > 0 ? version[..builtOn] : version;
    }

    private static async Task<ProcessResult> RunShellCommandAsync(string command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var (fileName, arguments) = OperatingSystem.IsWindows()
            ? ("powershell", new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", command })
            : ("/bin/bash", new[] { "-lc", command });

        try
        {
            var startInfo = new ProcessStartInfo { FileName = fileName };
            foreach (var arg in arguments)
                startInfo.ArgumentList.Add(arg);

            PlatformHelper.ConfigureForCrossPlatform(startInfo);

            using var process = new Process { StartInfo = startInfo };
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var errorTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

            try
            {
                await process.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new ProcessResult(false, null, "The installer timed out before it finished.");
            }

            var output = await outputTask;
            var error = await errorTask;
            return process.ExitCode == 0
                ? new ProcessResult(true, output, null)
                : new ProcessResult(false, output, string.IsNullOrWhiteSpace(error) ? output : error);
        }
        catch (Exception ex)
        {
            return new ProcessResult(false, null, ex.Message);
        }
    }

    private readonly record struct ProcessResult(bool Success, string? Output, string? Error);
}
