using Microsoft.AspNetCore.Http;
using VibeSwarm.Shared.Data;

namespace VibeSwarm.Web.Services;

public static class ThemePreferenceCookieHelper
{
	public const string CookieName = "VibeSwarm.ThemePreference";
	private static readonly TimeSpan CookieLifetime = TimeSpan.FromDays(365);

	public static void Append(HttpResponse response, HttpRequest request, ThemePreference preference)
	{
		response.Cookies.Append(CookieName, preference.ToValue(), new CookieOptions
		{
			HttpOnly = false,
			IsEssential = true,
			Path = "/",
			SameSite = SameSiteMode.Lax,
			Secure = request.IsHttps,
			MaxAge = CookieLifetime,
			Expires = DateTimeOffset.UtcNow.Add(CookieLifetime)
		});
	}

	public static void Delete(HttpResponse response, HttpRequest request)
	{
		response.Cookies.Delete(CookieName, new CookieOptions
		{
			Path = "/",
			SameSite = SameSiteMode.Lax,
			Secure = request.IsHttps
		});
	}
}
