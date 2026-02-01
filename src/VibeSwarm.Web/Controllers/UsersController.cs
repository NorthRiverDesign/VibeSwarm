using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Controllers;

[ApiController]
[Route("api/users")]
[Authorize(Roles = "Admin")]
public class UsersController : ControllerBase
{
    private readonly IUserService _userService;

    public UsersController(IUserService userService) => _userService = userService;

    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await _userService.GetAllUsersAsync());

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var user = await _userService.GetUserByIdAsync(id);
        return user == null ? NotFound() : Ok(user);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateUserModel model)
    {
        var (success, error, user) = await _userService.CreateUserAsync(model);
        return success ? Ok(user) : BadRequest(error);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateUserModel model)
    {
        var (success, error) = await _userService.UpdateUserAsync(id, model);
        return success ? Ok() : BadRequest(error);
    }

    [HttpPost("{id:guid}/reset-password")]
    public async Task<IActionResult> ResetPassword(Guid id, [FromBody] ResetPasswordRequest req)
    {
        var (success, error) = await _userService.ResetPasswordAsync(id, req.NewPassword);
        return success ? Ok() : BadRequest(error);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var (success, error) = await _userService.DeleteUserAsync(id);
        return success ? Ok() : BadRequest(error);
    }

    [HttpPost("{id:guid}/toggle-active")]
    public async Task<IActionResult> ToggleActive(Guid id, [FromBody] ToggleActiveRequest req)
    {
        var (success, error) = await _userService.ToggleUserActiveAsync(id, req.CurrentUserId);
        return success ? Ok() : BadRequest(error);
    }

    [HttpGet("{id:guid}/roles")]
    public async Task<IActionResult> GetRoles(Guid id) => Ok(await _userService.GetUserRolesAsync(id));

    public record ResetPasswordRequest(string NewPassword);
    public record ToggleActiveRequest(Guid CurrentUserId);
}
