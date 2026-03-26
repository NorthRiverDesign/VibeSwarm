using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VibeSwarm.Shared.Utilities;

namespace VibeSwarm.Web.Controllers;

[ApiController]
[Route("api/system-tools")]
[Authorize]
public class SystemToolsController : ControllerBase
{
    [HttpGet("detected")]
    public async Task<IActionResult> GetDetectedTools(CancellationToken ct)
    {
        var nodeResult = await RunShellCommandAsync("node --version", TimeSpan.FromSeconds(10), ct);
        var nodeVersion = nodeResult.Success ? nodeResult.Output?.Trim() : null;

        var npxResult = await RunShellCommandAsync("npx --version", TimeSpan.FromSeconds(10), ct);
        var npxAvailable = npxResult.Success;

        // Detect Playwright browsers by checking the chromium executable in common cache locations
        var playwrightResult = await DetectPlaywrightBrowsersAsync(ct);

        return Ok(new
        {
            NodeAvailable = nodeResult.Success,
            NodeVersion = nodeVersion,
            NpxAvailable = npxAvailable,
            PlaywrightBrowsersInstalled = playwrightResult.Installed,
            PlaywrightStatus = playwrightResult.Status
        });
    }

    [HttpPost("install/{tool}")]
    public async Task<IActionResult> InstallTool(string tool, CancellationToken ct)
    {
        var command = GetInstallCommand(tool);
        if (command is null)
            return BadRequest(new { Success = false, Error = $"Unknown tool: {tool}" });

        var timeout = tool.ToLowerInvariant() == "playwright" ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(5);
        var result = await RunShellCommandAsync(command, timeout, ct);
        return Ok(new { result.Success, result.Output, result.Error });
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

    private static string GetPlaywrightInstallCommand()
    {
        // Install Playwright chromium browser with system dependencies.
        // --with-deps installs OS-level libraries (libgbm, libasound, etc.) required by chromium.
        // ARM64 (Raspberry Pi) is supported via chromium-browser fallback on Debian/Ubuntu.
        return "npx -y playwright install chromium --with-deps";
    }

    private static async Task<(bool Installed, string Status)> DetectPlaywrightBrowsersAsync(CancellationToken ct)
    {
        // Try running npx playwright --version to see if it's available, then check for browsers
        var versionResult = await RunShellCommandAsync("npx -y playwright --version", TimeSpan.FromSeconds(30), ct);
        if (!versionResult.Success)
            return (false, "Playwright not available (Node.js/npx required)");

        // Check common Playwright browser cache locations
        var homeDir = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var browserPaths = new[]
        {
            Path.Combine(homeDir, ".cache", "ms-playwright"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ms-playwright")
        };

        foreach (var browserPath in browserPaths)
        {
            if (Directory.Exists(browserPath))
            {
                var chromiumDirs = Directory.GetDirectories(browserPath, "chromium*");
                if (chromiumDirs.Length > 0)
                    return (true, $"Chromium installed ({Path.GetFileName(chromiumDirs[^1])})");
            }
        }

        return (false, "Chromium browser not installed");
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
