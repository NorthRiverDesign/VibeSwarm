using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Web.Controllers;

[ApiController]
[Route("api/auth")]
[Authorize]
public class AuthController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _userManager;

    public AuthController(UserManager<ApplicationUser> userManager)
    {
        _userManager = userManager;
    }

    [HttpGet("user")]
    public async Task<IActionResult> GetCurrentUser()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();
        var roles = await _userManager.GetRolesAsync(user);
        ThemePreferenceCookieHelper.Append(Response, Request, user.ThemePreference);
        return Ok(new
        {
            UserId = user.Id.ToString(),
            UserName = user.UserName,
            Email = user.Email,
            Roles = roles,
            ThemePreference = user.ThemePreference.ToValue()
        });
    }

    [HttpGet("status")]
    public IActionResult GetStatus() => Ok(new { IsAuthenticated = true });

    [HttpGet("theme-preference")]
    public async Task<IActionResult> GetThemePreference()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return Unauthorized();
        }

        ThemePreferenceCookieHelper.Append(Response, Request, user.ThemePreference);

        return Ok(new ThemePreferenceDto
        {
            Theme = user.ThemePreference.ToValue()
        });
    }

    [HttpPut("theme-preference")]
    public async Task<IActionResult> UpdateThemePreference([FromBody] UpdateThemePreferenceRequest request)
    {
        if (!ThemePreferenceExtensions.TryParse(request.Theme, out var themePreference))
        {
            return BadRequest(new { Message = "Theme must be one of: system, light, dark." });
        }

        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return Unauthorized();
        }

        user.ThemePreference = themePreference;
        var result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            var errors = string.Join(" ", result.Errors.Select(e => e.Description));
            return BadRequest(new { Message = errors });
        }

        ThemePreferenceCookieHelper.Append(Response, Request, user.ThemePreference);

        return Ok(new ThemePreferenceDto
        {
            Theme = user.ThemePreference.ToValue()
        });
    }

    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var result = await _userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);

        if (result.Succeeded)
        {
            return Ok(new { Success = true, Message = "Password changed successfully." });
        }

        var errors = string.Join(" ", result.Errors.Select(e => e.Description));
        return BadRequest(new { Success = false, Message = errors });
    }
}

public class ChangePasswordRequest
{
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}
