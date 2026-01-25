using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Web.Pages.Account;

public class LogoutModel : PageModel
{
	private readonly SignInManager<ApplicationUser> _signInManager;
	private readonly ILogger<LogoutModel> _logger;

	public LogoutModel(SignInManager<ApplicationUser> signInManager, ILogger<LogoutModel> logger)
	{
		_signInManager = signInManager;
		_logger = logger;
	}

	public async Task<IActionResult> OnGetAsync()
	{
		await _signInManager.SignOutAsync();
		_logger.LogInformation("User logged out");
		return Redirect("/login");
	}

	public async Task<IActionResult> OnPostAsync()
	{
		await _signInManager.SignOutAsync();
		_logger.LogInformation("User logged out");
		return Redirect("/login");
	}
}
