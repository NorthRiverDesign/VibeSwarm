using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Services;

namespace VibeSwarm.Web.Services;

/// <summary>The job events a device can be told about.</summary>
public enum JobPushEvent
{
	Completed,
	Failed,
	InputRequired
}

/// <summary>One notification as the service worker shows it.</summary>
public sealed record PushMessage(string Title, string Body, string Url, string Tag, bool Urgent = false);

/// <summary>
/// Keeps the devices that get push notifications and sends to them through their push services.
/// Signs with a VAPID key pair made once and kept in <see cref="AppSettings"/>.
/// </summary>
public class PushNotificationService : IPushNotificationService
{
	public const string HttpClientName = "WebPush";

	// RFC 8292 asks for a contact here; Apple refuses anything but a mailto: or https: URL.
	internal const string VapidSubject = "https://github.com/NorthRiverDesign/VibeSwarm";

	private const string ExpiredMessage = "This device's subscription has expired. Turn notifications off and on again.";
	private static readonly TimeSpan TimeToLive = TimeSpan.FromDays(1);
	private static readonly SemaphoreSlim KeyCreationLock = new(1, 1);

	private readonly VibeSwarmDbContext _dbContext;
	private readonly IHttpClientFactory _httpClientFactory;
	private readonly ILogger<PushNotificationService> _logger;

	public PushNotificationService(VibeSwarmDbContext dbContext, IHttpClientFactory httpClientFactory, ILogger<PushNotificationService> logger)
	{
		_dbContext = dbContext;
		_httpClientFactory = httpClientFactory;
		_logger = logger;
	}

	public async Task<string> GetPublicKeyAsync(CancellationToken cancellationToken = default)
		=> (await GetOrCreateKeysAsync(cancellationToken)).PublicKey;

	public async Task<PushDeviceSettings?> GetDeviceAsync(string endpoint, CancellationToken cancellationToken = default)
	{
		var subscription = await _dbContext.PushSubscriptions.AsNoTracking()
			.FirstOrDefaultAsync(s => s.Endpoint == endpoint, cancellationToken);
		return subscription == null ? null : ToSettings(subscription);
	}

	public Task<PushDeviceSettings> SaveDeviceAsync(PushSubscriptionRequest request, CancellationToken cancellationToken = default)
		=> SaveDeviceAsync(request, null, cancellationToken);

	public async Task<PushDeviceSettings> SaveDeviceAsync(PushSubscriptionRequest request, Guid? userId, CancellationToken cancellationToken = default)
	{
		var endpoint = request.Endpoint?.Trim() ?? string.Empty;
		if (endpoint.Length > PushSubscription.EndpointMaxLength
			|| !Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri)
			|| endpointUri.Scheme != Uri.UriSchemeHttps)
		{
			throw new ArgumentException("The browser gave an invalid push endpoint.");
		}
		if (!WebPushProtocol.AreValidSubscriptionKeys(request.P256dh, request.Auth))
		{
			throw new ArgumentException("The browser gave invalid push subscription keys.");
		}

		var subscription = await _dbContext.PushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == endpoint, cancellationToken);
		if (subscription == null && !string.IsNullOrWhiteSpace(request.PreviousEndpoint))
		{
			subscription = await _dbContext.PushSubscriptions
				.FirstOrDefaultAsync(s => s.Endpoint == request.PreviousEndpoint, cancellationToken);
		}
		if (subscription == null)
		{
			subscription = new PushSubscription { Id = Guid.NewGuid(), CreatedAt = DateTime.UtcNow };
			_dbContext.PushSubscriptions.Add(subscription);
		}

		subscription.Endpoint = endpoint;
		subscription.P256dh = request.P256dh.Trim();
		subscription.Auth = request.Auth.Trim();
		subscription.UserId = userId ?? subscription.UserId;
		if (!string.IsNullOrWhiteSpace(request.DeviceName))
		{
			subscription.DeviceName = Truncate(request.DeviceName.Trim(), 100);
		}
		subscription.NotifyOnCompleted = request.NotifyOnCompleted ?? subscription.NotifyOnCompleted;
		subscription.NotifyOnFailed = request.NotifyOnFailed ?? subscription.NotifyOnFailed;
		subscription.NotifyOnInputRequired = request.NotifyOnInputRequired ?? subscription.NotifyOnInputRequired;

		await _dbContext.SaveChangesAsync(cancellationToken);
		return ToSettings(subscription);
	}

	public async Task DeleteDeviceAsync(string endpoint, CancellationToken cancellationToken = default)
	{
		await _dbContext.PushSubscriptions.Where(s => s.Endpoint == endpoint).ExecuteDeleteAsync(cancellationToken);
	}

	public async Task<PushTestResult> SendTestAsync(string endpoint, CancellationToken cancellationToken = default)
	{
		var subscription = await _dbContext.PushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == endpoint, cancellationToken);
		if (subscription == null)
		{
			return new PushTestResult { Error = "This device isn't signed up. Turn notifications off and on again." };
		}

		var keys = await GetOrCreateKeysAsync(cancellationToken);
		var result = await DeliverAsync(subscription, new PushMessage(
			"VibeSwarm", "Notifications are on for this device.", "/settings", "vibeswarm-test"), keys, cancellationToken);
		await _dbContext.SaveChangesAsync(cancellationToken);
		return result;
	}

	/// <summary>
	/// Sends a notification to every device that wants this kind of event, and forgets devices
	/// whose push service says the subscription is gone. Returns how many accepted it.
	/// </summary>
	public async Task<int> SendAsync(JobPushEvent pushEvent, PushMessage message, CancellationToken cancellationToken = default)
	{
		var query = _dbContext.PushSubscriptions.AsQueryable();
		query = pushEvent switch
		{
			JobPushEvent.Completed => query.Where(s => s.NotifyOnCompleted),
			JobPushEvent.Failed => query.Where(s => s.NotifyOnFailed),
			_ => query.Where(s => s.NotifyOnInputRequired)
		};
		var subscriptions = await query.ToListAsync(cancellationToken);
		if (subscriptions.Count == 0)
		{
			return 0;
		}

		var keys = await GetOrCreateKeysAsync(cancellationToken);
		var delivered = 0;
		foreach (var subscription in subscriptions)
		{
			if ((await DeliverAsync(subscription, message, keys, cancellationToken)).Success)
			{
				delivered++;
			}
		}

		await _dbContext.SaveChangesAsync(cancellationToken);
		return delivered;
	}

	private async Task<PushTestResult> DeliverAsync(PushSubscription subscription, PushMessage message,
		(string PublicKey, string PrivateKey) keys, CancellationToken cancellationToken)
	{
		var host = new Uri(subscription.Endpoint).Host;
		try
		{
			var payload = JsonSerializer.SerializeToUtf8Bytes(new
			{
				title = message.Title,
				body = message.Body,
				url = message.Url,
				tag = message.Tag
			});

			using var request = new HttpRequestMessage(HttpMethod.Post, subscription.Endpoint)
			{
				Content = new ByteArrayContent(WebPushProtocol.Encrypt(payload, subscription.P256dh, subscription.Auth))
			};
			request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
			request.Content.Headers.ContentEncoding.Add("aes128gcm");
			request.Headers.TryAddWithoutValidation("TTL", ((int)TimeToLive.TotalSeconds).ToString(CultureInfo.InvariantCulture));
			request.Headers.TryAddWithoutValidation("Urgency", message.Urgent ? "high" : "normal");
			request.Headers.TryAddWithoutValidation("Authorization", WebPushProtocol.CreateVapidAuthorization(
				subscription.Endpoint, VapidSubject, keys.PublicKey, keys.PrivateKey, DateTimeOffset.UtcNow.AddHours(12)));

			using var response = await _httpClientFactory.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
			if (response.IsSuccessStatusCode)
			{
				return new PushTestResult { Success = true };
			}

			if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
			{
				_logger.LogInformation("Push subscription on {Host} has expired; removing it", host);
				_dbContext.PushSubscriptions.Remove(subscription);
				return new PushTestResult { Error = ExpiredMessage };
			}

			var detail = await response.Content.ReadAsStringAsync(cancellationToken);
			_logger.LogWarning("Push service {Host} refused a notification: {StatusCode} {Detail}",
				host, (int)response.StatusCode, Truncate(detail, 300));
			return new PushTestResult { Error = $"The push service answered {(int)response.StatusCode} {response.ReasonPhrase}." };
		}
		catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
		{
			_logger.LogWarning(ex, "Could not reach push service {Host}", host);
			return new PushTestResult { Error = $"Couldn't reach the push service ({host})." };
		}
	}

	private async Task<(string PublicKey, string PrivateKey)> GetOrCreateKeysAsync(CancellationToken cancellationToken)
	{
		var keys = await ReadKeysAsync(cancellationToken);
		if (keys != null)
		{
			return keys.Value;
		}

		await KeyCreationLock.WaitAsync(cancellationToken);
		try
		{
			var settings = await _dbContext.AppSettings.OrderBy(s => s.Id).FirstOrDefaultAsync(cancellationToken);
			if (settings == null)
			{
				settings = new AppSettings { Id = Guid.NewGuid(), UpdatedAt = DateTime.UtcNow };
				_dbContext.AppSettings.Add(settings);
			}
			else
			{
				// Another request may have made the keys while this one waited.
				await _dbContext.Entry(settings).ReloadAsync(cancellationToken);
				if (!string.IsNullOrEmpty(settings.WebPushPublicKey) && !string.IsNullOrEmpty(settings.WebPushPrivateKey))
				{
					return (settings.WebPushPublicKey, settings.WebPushPrivateKey);
				}
			}

			var (publicKey, privateKey) = WebPushProtocol.CreateVapidKeys();
			settings.WebPushPublicKey = publicKey;
			settings.WebPushPrivateKey = privateKey;
			await _dbContext.SaveChangesAsync(cancellationToken);
			_logger.LogInformation("Created the VAPID key pair for push notifications");
			return (publicKey, privateKey);
		}
		finally
		{
			KeyCreationLock.Release();
		}
	}

	private async Task<(string PublicKey, string PrivateKey)?> ReadKeysAsync(CancellationToken cancellationToken)
	{
		var keys = await _dbContext.AppSettings.AsNoTracking().OrderBy(s => s.Id)
			.Select(s => new { s.WebPushPublicKey, s.WebPushPrivateKey })
			.FirstOrDefaultAsync(cancellationToken);
		return string.IsNullOrEmpty(keys?.WebPushPublicKey) || string.IsNullOrEmpty(keys.WebPushPrivateKey)
			? null
			: (keys.WebPushPublicKey, keys.WebPushPrivateKey);
	}

	private static PushDeviceSettings ToSettings(PushSubscription subscription) => new()
	{
		Endpoint = subscription.Endpoint,
		DeviceName = subscription.DeviceName,
		NotifyOnCompleted = subscription.NotifyOnCompleted,
		NotifyOnFailed = subscription.NotifyOnFailed,
		NotifyOnInputRequired = subscription.NotifyOnInputRequired
	};

	internal static string Truncate(string value, int maxLength)
		=> value.Length <= maxLength ? value : value[..(maxLength - 1)].TrimEnd() + "…";
}
