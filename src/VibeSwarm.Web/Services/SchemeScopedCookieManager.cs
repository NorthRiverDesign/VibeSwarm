using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace VibeSwarm.Web.Services;

/// <summary>
/// Gives the auth cookie a separate name on plain HTTP. A sign-in over HTTPS leaves a Secure
/// cookie that browsers never send over HTTP and won't let an HTTP response replace, so once
/// HTTPS is turned off, an HTTP sign-in using the same name would be dropped and loop back to
/// the login page. HTTPS requests keep the configured name, so existing sessions survive.
/// </summary>
public sealed class SchemeScopedCookieManager : ICookieManager
{
	public const string HttpSuffix = ".Http";

	private readonly ChunkingCookieManager _inner = new();

	public string? GetRequestCookie(HttpContext context, string key)
		=> _inner.GetRequestCookie(context, ScopeKey(context, key));

	public void AppendResponseCookie(HttpContext context, string key, string? value, CookieOptions options)
		=> _inner.AppendResponseCookie(context, ScopeKey(context, key), value, options);

	public void DeleteCookie(HttpContext context, string key, CookieOptions options)
		=> _inner.DeleteCookie(context, ScopeKey(context, key), options);

	public static string ScopeKey(HttpContext context, string key)
		=> context.Request.IsHttps ? key : key + HttpSuffix;
}
