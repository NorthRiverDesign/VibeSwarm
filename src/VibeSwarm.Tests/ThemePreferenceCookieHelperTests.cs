using Microsoft.AspNetCore.Http;
using VibeSwarm.Shared.Data;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class ThemePreferenceCookieHelperTests
{
	[Fact]
	public void Append_WritesThemePreferenceCookie()
	{
		var httpContext = new DefaultHttpContext();

		ThemePreferenceCookieHelper.Append(httpContext.Response, httpContext.Request, ThemePreference.Dark);

		var cookieHeader = httpContext.Response.Headers.SetCookie.ToString();
		Assert.Contains($"{ThemePreferenceCookieHelper.CookieName}=dark", cookieHeader, StringComparison.Ordinal);
		Assert.Contains("path=/", cookieHeader, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("samesite=lax", cookieHeader, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void Delete_ExpiresThemePreferenceCookie()
	{
		var httpContext = new DefaultHttpContext();

		ThemePreferenceCookieHelper.Delete(httpContext.Response, httpContext.Request);

		var cookieHeader = httpContext.Response.Headers.SetCookie.ToString();
		Assert.Contains($"{ThemePreferenceCookieHelper.CookieName}=", cookieHeader, StringComparison.Ordinal);
		Assert.Contains("expires=", cookieHeader, StringComparison.OrdinalIgnoreCase);
	}
}
