using System.Buffers.Binary;
using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Models;
using VibeSwarm.Shared.Providers;
using VibeSwarm.Shared.Services;
using VibeSwarm.Web.Hubs;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class PushNotificationTests : IDisposable
{
	// RFC 8291 section 5 and Appendix A.
	private const string RfcSenderPrivateKey = "yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw";
	private const string RfcSenderPublicKey = "BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8";
	private const string RfcReceiverPrivateKey = "q1dXpw3UpT5VOmu_cf_v6ih07Aems3njxI-JWgLcM94";
	private const string RfcReceiverPublicKey = "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4";
	private const string RfcAuthSecret = "BTBZMqHH6r4Tts7J_aSIgg";
	private const string RfcSalt = "DGv6ra1nlYgDCS1FRnbzlw";
	private const string RfcBody =
		"DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27ml" +
		"mlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPT" +
		"pK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN";

	private const string FirstEndpoint = "https://fcm.googleapis.com/fcm/send/device-one";
	private const string SecondEndpoint = "https://web.push.apple.com/device-two";

	private readonly SqliteConnection _connection;
	private readonly ServiceProvider _provider;
	private readonly FakePushService _pushService = new();

	public PushNotificationTests()
	{
		_connection = new SqliteConnection("Data Source=:memory:");
		_connection.Open();

		var services = new ServiceCollection();
		services.AddLogging();
		services.AddDbContext<VibeSwarmDbContext>(options => options.UseSqlite(_connection));
		services.AddSingleton<IHttpClientFactory>(_pushService);
		services.AddScoped<PushNotificationService>();
		_provider = services.BuildServiceProvider();

		using var scope = _provider.CreateScope();
		scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>().Database.EnsureCreated();
	}

	public void Dispose()
	{
		_provider.Dispose();
		_connection.Dispose();
	}

	[Fact]
	public void Encrypt_MatchesTheRfc8291WorkedExample()
	{
		using var senderKey = ImportKey(RfcSenderPrivateKey, RfcSenderPublicKey);

		var body = WebPushProtocol.Encrypt(
			Encoding.ASCII.GetBytes("When I grow up, I want to be a watermelon"),
			RfcReceiverPublicKey, RfcAuthSecret, senderKey, Base64Url.DecodeFromChars(RfcSalt));

		Assert.Equal(RfcBody, Base64Url.EncodeToString(body));
	}

	[Fact]
	public void Encrypt_WithAFreshKeyAndSalt_DecryptsForTheReceiver()
	{
		var plaintext = Encoding.UTF8.GetBytes("{\"title\":\"VibeSwarm: job finished\"}");

		var first = WebPushProtocol.Encrypt(plaintext, RfcReceiverPublicKey, RfcAuthSecret);
		var second = WebPushProtocol.Encrypt(plaintext, RfcReceiverPublicKey, RfcAuthSecret);

		Assert.Equal(plaintext, Decrypt(first, RfcReceiverPrivateKey, RfcReceiverPublicKey, RfcAuthSecret));
		Assert.Equal(plaintext, Decrypt(second, RfcReceiverPrivateKey, RfcReceiverPublicKey, RfcAuthSecret));
		Assert.NotEqual(first, second);
	}

	[Fact]
	public void CreateVapidAuthorization_IsAnEs256TokenForTheEndpointOrigin()
	{
		var (publicKey, privateKey) = WebPushProtocol.CreateVapidKeys();
		var expires = DateTimeOffset.UtcNow.AddHours(12);

		var header = WebPushProtocol.CreateVapidAuthorization(
			"https://web.push.apple.com/QGuQyavXutnMH8/abc", "https://example.com", publicKey, privateKey, expires);

		var match = System.Text.RegularExpressions.Regex.Match(header, "^vapid t=([^,]+), k=(.+)$");
		Assert.True(match.Success, header);
		Assert.Equal(publicKey, match.Groups[2].Value);

		var parts = match.Groups[1].Value.Split('.');
		Assert.Equal(3, parts.Length);
		using var claims = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
		Assert.Equal("https://web.push.apple.com", claims.RootElement.GetProperty("aud").GetString());
		Assert.Equal("https://example.com", claims.RootElement.GetProperty("sub").GetString());
		Assert.Equal(expires.ToUnixTimeSeconds(), claims.RootElement.GetProperty("exp").GetInt64());

		var point = Base64Url.DecodeFromChars(publicKey);
		using var verifier = ECDsa.Create(new ECParameters
		{
			Curve = ECCurve.NamedCurves.nistP256,
			Q = new ECPoint { X = point[1..33], Y = point[33..] }
		});
		Assert.True(verifier.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), Base64Url.DecodeFromChars(parts[2]),
			HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
	}

	[Fact]
	public void AppSettings_SentToTheBrowser_LeaveOutThePushPrivateKey()
	{
		var json = JsonSerializer.Serialize(new AppSettings { WebPushPublicKey = "public-part", WebPushPrivateKey = "secret-part" });

		Assert.Contains("public-part", json);
		Assert.DoesNotContain("secret-part", json);
	}

	[Fact]
	public async Task GetPublicKey_MakesTheKeyPairOnceAndKeepsIt()
	{
		var first = await WithServiceAsync(service => service.GetPublicKeyAsync());
		var second = await WithServiceAsync(service => service.GetPublicKeyAsync());

		Assert.Equal(first, second);
		Assert.Equal(65, Base64Url.DecodeFromChars(first).Length);

		// Saving the Settings page (which never carries the private key) leaves the pair alone.
		using (var scope = _provider.CreateScope())
		{
			var db = scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>();
			var settingsService = new SettingsService(db);
			await settingsService.UpdateSettingsAsync(new AppSettings { TimeZoneId = "UTC", EnableHttps = true });
		}
		var settings = await WithDbAsync(db => db.AppSettings.AsNoTracking().SingleAsync());
		Assert.Equal(first, settings.WebPushPublicKey);
		Assert.False(string.IsNullOrEmpty(settings.WebPushPrivateKey));
	}

	[Fact]
	public async Task SaveDevice_KeepsChoicesNotSentAndMovesThemToARenewedEndpoint()
	{
		await WithServiceAsync(service => service.SaveDeviceAsync(Subscribe(FirstEndpoint)));
		await WithServiceAsync(service => service.SaveDeviceAsync(Subscribe(FirstEndpoint, completed: false)));
		var kept = await WithServiceAsync(service => service.SaveDeviceAsync(Subscribe(FirstEndpoint)));
		Assert.False(kept.NotifyOnCompleted);
		Assert.True(kept.NotifyOnFailed);

		var renewed = Subscribe(SecondEndpoint);
		renewed.PreviousEndpoint = FirstEndpoint;
		var moved = await WithServiceAsync(service => service.SaveDeviceAsync(renewed));

		Assert.Equal(SecondEndpoint, moved.Endpoint);
		Assert.False(moved.NotifyOnCompleted);
		var endpoints = await WithDbAsync(db => db.PushSubscriptions.Select(s => s.Endpoint).ToListAsync());
		Assert.Equal([SecondEndpoint], endpoints);
	}

	[Theory]
	[InlineData("http://fcm.googleapis.com/fcm/send/plain-http", RfcReceiverPublicKey, RfcAuthSecret)]
	[InlineData(FirstEndpoint, "not-a-key", RfcAuthSecret)]
	[InlineData(FirstEndpoint, RfcReceiverPublicKey, "c2hvcnQ")]
	public async Task SaveDevice_RejectsWhatNoBrowserWouldSend(string endpoint, string p256dh, string auth)
	{
		var request = new PushSubscriptionRequest { Endpoint = endpoint, P256dh = p256dh, Auth = auth };

		await Assert.ThrowsAsync<ArgumentException>(() => WithServiceAsync(service => service.SaveDeviceAsync(request)));
	}

	[Fact]
	public async Task Send_GoesOnlyToDevicesThatWantTheEventAndForgetsExpiredOnes()
	{
		await WithServiceAsync(service => service.SaveDeviceAsync(Subscribe(FirstEndpoint)));
		await WithServiceAsync(service => service.SaveDeviceAsync(Subscribe(SecondEndpoint, failed: false)));
		await WithServiceAsync(service => service.SaveDeviceAsync(Subscribe("https://updates.push.services.mozilla.com/wpush/v2/gone")));
		_pushService.StatusFor["updates.push.services.mozilla.com"] = HttpStatusCode.Gone;

		var delivered = await WithServiceAsync(service => service.SendAsync(JobPushEvent.Failed,
			new PushMessage("Shop: job failed", "Fix login", "/jobs/view/1", "job-1", Urgent: true)));

		Assert.Equal(1, delivered);
		Assert.Equal(["fcm.googleapis.com", "updates.push.services.mozilla.com"], _pushService.Requests.Select(r => r.Host).Order());
		var sent = _pushService.Requests.Single(r => r.Host == "fcm.googleapis.com");
		Assert.Equal("aes128gcm", sent.ContentEncoding);
		Assert.Equal("high", sent.Urgency);
		Assert.Equal("86400", sent.Ttl);
		Assert.StartsWith("vapid t=", sent.Authorization);

		using var payload = JsonDocument.Parse(Decrypt(sent.Body, RfcReceiverPrivateKey, RfcReceiverPublicKey, RfcAuthSecret));
		Assert.Equal("Shop: job failed", payload.RootElement.GetProperty("title").GetString());
		Assert.Equal("/jobs/view/1", payload.RootElement.GetProperty("url").GetString());

		var remaining = await WithDbAsync(db => db.PushSubscriptions.Select(s => s.Endpoint).OrderBy(e => e).ToListAsync());
		Assert.Equal([FirstEndpoint, SecondEndpoint], remaining);
	}

	[Fact]
	public async Task SendTest_ReportsWhatThePushServiceSaid()
	{
		await WithServiceAsync(service => service.SaveDeviceAsync(Subscribe(FirstEndpoint)));

		var sent = await WithServiceAsync(service => service.SendTestAsync(FirstEndpoint));
		_pushService.StatusFor["fcm.googleapis.com"] = HttpStatusCode.Forbidden;
		var refused = await WithServiceAsync(service => service.SendTestAsync(FirstEndpoint));
		var unknown = await WithServiceAsync(service => service.SendTestAsync(SecondEndpoint));

		Assert.True(sent.Success);
		Assert.False(refused.Success);
		Assert.Contains("403", refused.Error);
		Assert.False(unknown.Success);
	}

	[Theory]
	[InlineData(JobStatus.Completed, "Shop: job finished")]
	[InlineData(JobStatus.Failed, "Shop: job failed")]
	[InlineData(JobStatus.Stalled, "Shop: job stalled")]
	public async Task JobCompleted_SendsOnceForHowTheJobEnded(JobStatus status, string expectedTitle)
	{
		await WithServiceAsync(service => service.SaveDeviceAsync(Subscribe(FirstEndpoint)));
		var jobId = await AddJobAsync(status, error: "Build failed\nmore detail");
		var notifier = CreateNotifier();

		await notifier.NotifyJobCompletedAsync(jobId);
		await notifier.NotifyJobCompletedAsync(jobId);

		var request = Assert.Single(_pushService.Requests);
		using var payload = JsonDocument.Parse(Decrypt(request.Body, RfcReceiverPrivateKey, RfcReceiverPublicKey, RfcAuthSecret));
		Assert.Equal(expectedTitle, payload.RootElement.GetProperty("title").GetString());
		Assert.Equal($"/jobs/view/{jobId}", payload.RootElement.GetProperty("url").GetString());
		var body = payload.RootElement.GetProperty("body").GetString();
		Assert.StartsWith("Fix the login page", body);
		Assert.Equal(status != JobStatus.Completed, body!.Contains("Build failed"));
		Assert.DoesNotContain("more detail", body);
	}

	[Theory]
	[InlineData(JobStatus.Cancelled)]
	[InlineData(JobStatus.New)]
	public async Task JobCompleted_StaysQuietForCancelledOrRequeuedJobs(JobStatus status)
	{
		await WithServiceAsync(service => service.SaveDeviceAsync(Subscribe(FirstEndpoint)));
		var jobId = await AddJobAsync(status);

		await CreateNotifier().NotifyJobCompletedAsync(jobId);

		Assert.Empty(_pushService.Requests);
	}

	[Fact]
	public async Task JobCompleted_SkipsDevicesThatTurnedTheEventOff()
	{
		await WithServiceAsync(service => service.SaveDeviceAsync(Subscribe(FirstEndpoint, completed: false)));
		var jobId = await AddJobAsync(JobStatus.Completed);

		await CreateNotifier().NotifyJobCompletedAsync(jobId);

		Assert.Empty(_pushService.Requests);
	}

	[Fact]
	public async Task InputRequired_WithoutInferenceAnswering_SendsStraightAway()
	{
		await AddSettingsAsync(inferenceAnswersQuestions: false);
		await WithServiceAsync(service => service.SaveDeviceAsync(Subscribe(FirstEndpoint)));
		var jobId = await AddJobAsync(JobStatus.Paused, question: "Which database should I use?");

		await CreateNotifier().NotifyInteractionRequiredAsync(jobId);

		var request = Assert.Single(_pushService.Requests);
		using var payload = JsonDocument.Parse(Decrypt(request.Body, RfcReceiverPrivateKey, RfcReceiverPublicKey, RfcAuthSecret));
		Assert.Equal("Shop: needs your input", payload.RootElement.GetProperty("title").GetString());
		Assert.Contains("Which database should I use?", payload.RootElement.GetProperty("body").GetString());
	}

	[Fact]
	public async Task InputRequired_WhileInferenceAnswers_WaitsAndSkipsAnsweredQuestions()
	{
		await AddSettingsAsync(inferenceAnswersQuestions: true);
		await WithServiceAsync(service => service.SaveDeviceAsync(Subscribe(FirstEndpoint)));
		var answeredJob = await AddJobAsync(JobStatus.Paused, question: "Add tests too?");
		var waitingJob = await AddJobAsync(JobStatus.Paused, question: "Which API key should I use?");
		var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
		var notifier = CreateNotifier(time);

		var answered = notifier.NotifyInteractionRequiredAsync(answeredJob);
		var waiting = notifier.NotifyInteractionRequiredAsync(waitingJob);
		await WithDbAsync(db => db.Jobs.Where(j => j.Id == answeredJob)
			.ExecuteUpdateAsync(set => set.SetProperty(j => j.Status, JobStatus.Processing)));
		Assert.Empty(_pushService.Requests);

		await AdvanceUntilDoneAsync(time, answered, waiting);

		var request = Assert.Single(_pushService.Requests);
		using var payload = JsonDocument.Parse(Decrypt(request.Body, RfcReceiverPrivateKey, RfcReceiverPublicKey, RfcAuthSecret));
		Assert.Contains("Which API key should I use?", payload.RootElement.GetProperty("body").GetString());
	}

	[Fact]
	public async Task SignalRJobCompleted_AlsoSendsThePushNotification()
	{
		await WithServiceAsync(service => service.SaveDeviceAsync(Subscribe(FirstEndpoint)));
		var jobId = await AddJobAsync(JobStatus.Completed);
		var updates = new SignalRJobUpdateService(new NullHubContext(), NullLogger<SignalRJobUpdateService>.Instance, CreateNotifier());

		await updates.NotifyJobCompleted(jobId, true);

		var deadline = DateTime.UtcNow.AddSeconds(10);
		while (_pushService.Requests.Count == 0 && DateTime.UtcNow < deadline)
		{
			await Task.Delay(20);
		}
		Assert.Single(_pushService.Requests);
	}

	[Fact]
	public async Task SignalRStatusChangedToStalled_SendsThePushNotification()
	{
		await WithServiceAsync(service => service.SaveDeviceAsync(Subscribe(FirstEndpoint)));
		var stalledJob = await AddJobAsync(JobStatus.Stalled);
		var runningJob = await AddJobAsync(JobStatus.Processing);
		var updates = new SignalRJobUpdateService(new NullHubContext(), NullLogger<SignalRJobUpdateService>.Instance, CreateNotifier());

		await updates.NotifyJobStatusChanged(runningJob, nameof(JobStatus.Processing));
		await updates.NotifyJobStatusChanged(stalledJob, nameof(JobStatus.Stalled));

		var deadline = DateTime.UtcNow.AddSeconds(10);
		while (_pushService.Requests.Count == 0 && DateTime.UtcNow < deadline)
		{
			await Task.Delay(20);
		}
		var request = Assert.Single(_pushService.Requests);
		using var payload = JsonDocument.Parse(Decrypt(request.Body, RfcReceiverPrivateKey, RfcReceiverPublicKey, RfcAuthSecret));
		Assert.Equal($"/jobs/view/{stalledJob}", payload.RootElement.GetProperty("url").GetString());
	}

	private JobPushNotifier CreateNotifier(TimeProvider? time = null)
		=> new(_provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<JobPushNotifier>.Instance, time);

	private static async Task AdvanceUntilDoneAsync(FakeTimeProvider time, params Task[] tasks)
	{
		for (var i = 0; i < 500 && !tasks.All(t => t.IsCompleted); i++)
		{
			time.Advance(TimeSpan.FromSeconds(30));
			await Task.Delay(10);
		}
		await Task.WhenAll(tasks);
	}

	private static PushSubscriptionRequest Subscribe(string endpoint, bool? completed = null, bool? failed = null) => new()
	{
		Endpoint = endpoint,
		P256dh = RfcReceiverPublicKey,
		Auth = RfcAuthSecret,
		DeviceName = "iPhone",
		NotifyOnCompleted = completed,
		NotifyOnFailed = failed
	};

	private async Task AddSettingsAsync(bool inferenceAnswersQuestions)
	{
		await WithDbAsync(async db =>
		{
			db.AppSettings.Add(new AppSettings { Id = Guid.NewGuid(), InferenceAnswersAgentQuestions = inferenceAnswersQuestions });
			return await db.SaveChangesAsync();
		});
	}

	private async Task<Guid> AddJobAsync(JobStatus status, string? error = null, string? question = null)
	{
		return await WithDbAsync(async db =>
		{
			var project = await db.Projects.FirstOrDefaultAsync();
			if (project == null)
			{
				project = new Project { Id = Guid.NewGuid(), Name = "Shop", WorkingPath = "/tmp/shop" };
				db.Projects.Add(project);
			}
			var provider = await db.Providers.FirstOrDefaultAsync();
			if (provider == null)
			{
				provider = new Provider { Id = Guid.NewGuid(), Name = "Claude", Type = ProviderType.Claude };
				db.Providers.Add(provider);
			}

			var job = new Job
			{
				Id = Guid.NewGuid(),
				ProjectId = project.Id,
				ProviderId = provider.Id,
				GoalPrompt = "Fix the login page\nwith more detail",
				Status = status,
				ErrorMessage = error,
				CompletedAt = status is JobStatus.Completed or JobStatus.Failed or JobStatus.Stalled ? DateTime.UtcNow : null,
				PendingInteractionPrompt = question,
				InteractionRequestedAt = question == null ? null : DateTime.UtcNow
			};
			db.Jobs.Add(job);
			await db.SaveChangesAsync();
			return job.Id;
		});
	}

	private async Task<T> WithServiceAsync<T>(Func<PushNotificationService, Task<T>> action)
	{
		using var scope = _provider.CreateScope();
		return await action(scope.ServiceProvider.GetRequiredService<PushNotificationService>());
	}

	private async Task<T> WithDbAsync<T>(Func<VibeSwarmDbContext, Task<T>> action)
	{
		using var scope = _provider.CreateScope();
		return await action(scope.ServiceProvider.GetRequiredService<VibeSwarmDbContext>());
	}

	private static ECDiffieHellman ImportKey(string privateKey, string publicKey)
	{
		var point = Base64Url.DecodeFromChars(publicKey);
		return ECDiffieHellman.Create(new ECParameters
		{
			Curve = ECCurve.NamedCurves.nistP256,
			D = Base64Url.DecodeFromChars(privateKey),
			Q = new ECPoint { X = point[1..33], Y = point[33..] }
		});
	}

	/// <summary>The receiving browser's side of RFC 8291, for a single-record message.</summary>
	private static byte[] Decrypt(byte[] body, string receiverPrivateKey, string receiverPublicKey, string authSecret)
	{
		var salt = body[..16];
		Assert.Equal(4096u, BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(16, 4)));
		Assert.Equal(65, body[20]);
		var senderPublic = body[21..86];

		using var receiver = ImportKey(receiverPrivateKey, receiverPublicKey);
		using var sender = ECDiffieHellman.Create(new ECParameters
		{
			Curve = ECCurve.NamedCurves.nistP256,
			Q = new ECPoint { X = senderPublic[1..33], Y = senderPublic[33..] }
		});
		var secret = receiver.DeriveRawSecretAgreement(sender.PublicKey);
		var keyInfo = Encoding.ASCII.GetBytes("WebPush: info\0").Concat(Base64Url.DecodeFromChars(receiverPublicKey)).Concat(senderPublic).ToArray();
		var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, Base64Url.DecodeFromChars(authSecret), keyInfo);
		var cek = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 16, salt, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
		var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 12, salt, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));

		var record = body[86..];
		var plaintext = new byte[record.Length - 16];
		using var aes = new AesGcm(cek, 16);
		aes.Decrypt(nonce, record[..^16], record[^16..], plaintext);
		Assert.Equal(0x02, plaintext[^1]);
		return plaintext[..^1];
	}

	private sealed record SentPush(string Host, byte[] Body, string? ContentEncoding, string? Urgency, string? Ttl, string? Authorization);

	/// <summary>Stands in for every push service: records requests and answers 201 unless told otherwise.</summary>
	private sealed class FakePushService : HttpMessageHandler, IHttpClientFactory
	{
		public List<SentPush> Requests { get; } = [];
		public Dictionary<string, HttpStatusCode> StatusFor { get; } = [];

		public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			var host = request.RequestUri!.Host;
			lock (Requests)
			{
				Requests.Add(new SentPush(
					host,
					request.Content!.ReadAsByteArrayAsync(cancellationToken).Result,
					request.Content.Headers.ContentEncoding.SingleOrDefault(),
					request.Headers.TryGetValues("Urgency", out var urgency) ? urgency.Single() : null,
					request.Headers.TryGetValues("TTL", out var ttl) ? ttl.Single() : null,
					request.Headers.TryGetValues("Authorization", out var auth) ? auth.Single() : null));
			}
			await Task.Yield();
			return new HttpResponseMessage(StatusFor.GetValueOrDefault(host, HttpStatusCode.Created));
		}
	}

	private sealed class NullHubContext : IHubContext<JobHub>
	{
		public IHubClients Clients { get; } = new NullHubClients();
		public IGroupManager Groups => throw new NotSupportedException();

		private sealed class NullHubClients : IHubClients
		{
			private static readonly IClientProxy Proxy = new NullClientProxy();
			public IClientProxy All => Proxy;
			public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy;
			public IClientProxy Client(string connectionId) => Proxy;
			public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy;
			public IClientProxy Group(string groupName) => Proxy;
			public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy;
			public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy;
			public IClientProxy User(string userId) => Proxy;
			public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy;
		}

		private sealed class NullClientProxy : IClientProxy
		{
			public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) => Task.CompletedTask;
		}
	}
}
