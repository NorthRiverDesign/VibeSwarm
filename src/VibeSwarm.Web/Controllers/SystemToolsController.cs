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
    [HttpPost("install/{tool}")]
    public async Task<IActionResult> InstallTool(string tool, CancellationToken ct)
    {
        var command = GetInstallCommand(tool);
        if (command is null)
            return BadRequest(new { Success = false, Error = $"Unknown tool: {tool}" });

        var result = await RunShellCommandAsync(command, TimeSpan.FromMinutes(5), ct);
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
