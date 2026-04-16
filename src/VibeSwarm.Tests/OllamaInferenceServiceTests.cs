using System.Net;
using System.Text;
using VibeSwarm.Shared.Data;
using VibeSwarm.Shared.Inference;
using VibeSwarm.Shared.Services;
using VibeSwarm.Web.Services;

namespace VibeSwarm.Tests;

public sealed class OllamaInferenceServiceTests
{
	[Fact]
	public async Task GenerateAsync_AggregatesStreamedChunksIntoSingleResponse()
	{
		var service = CreateService("""
{"model":"qwen2.5-coder:7b","response":"Hello","done":false}
{"model":"qwen2.5-coder:7b","response":" world","done":false}
{"model":"qwen2.5-coder:7b","done":true,"total_duration":2500000000,"prompt_eval_count":11,"eval_count":7}
""");

		var response = await service.GenerateAsync(new InferenceRequest
		{
			Model = "qwen2.5-coder:7b",
			Prompt = "Say hello."
		});

		Assert.True(response.Success);
		Assert.Equal("Hello world", response.Response);
		Assert.Equal("qwen2.5-coder:7b", response.ModelUsed);
		Assert.Equal(2500, response.DurationMs);
		Assert.Equal(11, response.PromptTokens);
		Assert.Equal(7, response.CompletionTokens);
	}

	[Fact]
	public async Task GenerateAsync_ReturnsHelpfulError_WhenStreamEndsBeforeDone()
	{
		var service = CreateService("""
{"model":"phi4-mini","response":"Partial answer","done":false}
""");

		var response = await service.GenerateAsync(new InferenceRequest
		{
			Model = "phi4-mini",
			Prompt = "Explain the fix."
		});

		Assert.False(response.Success);
		Assert.Equal("phi4-mini", response.ModelUsed);
		Assert.Contains("stopped before finishing", response.Error, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task GenerateAsync_ReturnsTimeoutError_WhenResponseTakesTooLong()
	{
		var handler = new StubHttpMessageHandler(async (_, cancellationToken) =>
		{
			await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
			return new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent("""
{"model":"tinyllama","done":true,"response":"Too late"}
""", Encoding.UTF8, "application/json")
			};
		});

		var service = CreateService(
			handler,
			new OllamaInferenceService.RuntimeOptions
			{
				GenerationTimeout = TimeSpan.FromMilliseconds(50),
				InitialResponseTimeout = TimeSpan.FromSeconds(1),
				StreamInactivityTimeout = TimeSpan.FromSeconds(1),
				StallDetectionWindow = TimeSpan.FromSeconds(1)
			});

		var response = await service.GenerateAsync(new InferenceRequest
		{
			Model = "tinyllama",
			Prompt = "Respond eventually."
		});

		Assert.False(response.Success);
		Assert.Equal("tinyllama", response.ModelUsed);
		Assert.Contains("Timed out waiting for inference", response.Error, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task GenerateAsync_ReturnsStallError_WhenGenerationStopsAfterInitialTokens()
	{
		// Stream sends enough tokens to pass the stall threshold, then hangs.
		var streamBody = string.Join("\n", Enumerable.Range(1, 6).Select(i =>
			$$"""{"model":"phi4-mini","response":"word{{i}} ","done":false}""")) + "\n";

		var handler = new StubHttpMessageHandler(async (_, ct) =>
		{
			return new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new HangingStreamContent(streamBody, ct)
			};
		});

		var service = CreateService(
			handler,
			new OllamaInferenceService.RuntimeOptions
			{
				GenerationTimeout = TimeSpan.FromSeconds(30),
				InitialResponseTimeout = TimeSpan.FromSeconds(5),
				StreamInactivityTimeout = TimeSpan.FromSeconds(5),
				StallDetectionWindow = TimeSpan.FromMilliseconds(200),
				StallDetectionTokenThreshold = 5
			});

		var response = await service.GenerateAsync(new InferenceRequest
		{
			Model = "phi4-mini",
			Prompt = "Tell me something."
		});

		Assert.False(response.Success);
		Assert.Equal("phi4-mini", response.ModelUsed);
		Assert.Contains("stopped generating", response.Error, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("out of memory", response.Error, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task GenerateAsync_UsesSelectedProviderDefaultModel_WhenProviderIdSpecified()
	{
		var selectedProvider = new InferenceProvider
		{
			Id = Guid.NewGuid(),
			Name = "Selected Ollama",
			ProviderType = InferenceProviderType.Ollama,
			Endpoint = "http://selected-ollama:11434",
			IsEnabled = true,
			Models =
			[
				new InferenceModel
				{
					InferenceProviderId = Guid.NewGuid(),
					ModelId = "selected-model",
					IsAvailable = true,
					IsDefault = true,
					TaskType = "default"
				}
			]
		};
		var otherProvider = new InferenceProvider
		{
			Id = Guid.NewGuid(),
			Name = "Other Ollama",
			ProviderType = InferenceProviderType.Ollama,
			Endpoint = "http://other-ollama:11434",
			IsEnabled = true
		};
		var fallbackModel = new InferenceModel
		{
			InferenceProviderId = otherProvider.Id,
			ModelId = "fallback-model",
			IsAvailable = true,
			IsDefault = true,
			TaskType = "default"
		};
		Uri? requestUri = null;
		string? requestBody = null;
		var handler = new StubHttpMessageHandler(async (request, _) =>
		{
			requestUri = request.RequestUri;
			requestBody = await request.Content!.ReadAsStringAsync();
			return new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent("""
{"model":"selected-model","response":"Hello","done":false}
{"model":"selected-model","done":true}
""", Encoding.UTF8, "application/x-ndjson")
			};
		});
		var service = CreateService(
			handler,
			providerService: new FakeInferenceProviderService([otherProvider, selectedProvider], fallbackModel));

		var response = await service.GenerateAsync(new InferenceRequest
		{
			ProviderId = selectedProvider.Id,
			Endpoint = selectedProvider.Endpoint,
			Prompt = "Say hello."
		});

		Assert.True(response.Success);
		Assert.Equal("selected-model", response.ModelUsed);
		Assert.Equal("http://selected-ollama:11434/api/generate", requestUri?.ToString());
		Assert.Contains("\"model\":\"selected-model\"", requestBody, StringComparison.Ordinal);
	}

	private static OllamaInferenceService CreateService(
		string responseBody,
		OllamaInferenceService.RuntimeOptions? runtimeOptions = null)
	{
		var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent(responseBody, Encoding.UTF8, "application/x-ndjson")
		}));

		return CreateService(handler, runtimeOptions);
	}

	private static OllamaInferenceService CreateService(
		HttpMessageHandler handler,
		OllamaInferenceService.RuntimeOptions? runtimeOptions = null,
		IInferenceProviderService? providerService = null)
	{
		var httpClient = new HttpClient(handler)
		{
			BaseAddress = new Uri("http://localhost:11434")
		};

		return new OllamaInferenceService(
			new FakeHttpClientFactory(httpClient),
			providerService ?? new FakeInferenceProviderService(),
			runtimeOptions);
	}

	private sealed class FakeHttpClientFactory : IHttpClientFactory
	{
		private readonly HttpClient _httpClient;

		public FakeHttpClientFactory(HttpClient httpClient)
		{
			_httpClient = httpClient;
		}

		public HttpClient CreateClient(string name) => _httpClient;
	}

	private sealed class StubHttpMessageHandler : HttpMessageHandler
	{
		private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

		public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
		{
			_handler = handler;
		}

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
			=> _handler(request, cancellationToken);
	}

	private sealed class FakeInferenceProviderService(
		IReadOnlyList<InferenceProvider>? providers = null,
		InferenceModel? fallbackModel = null) : IInferenceProviderService
	{
		public Task<IEnumerable<InferenceProvider>> GetAllAsync(CancellationToken ct = default)
			=> Task.FromResult<IEnumerable<InferenceProvider>>(providers ?? []);

		public Task<InferenceProvider?> GetByIdAsync(Guid id, CancellationToken ct = default)
			=> Task.FromResult((providers ?? []).FirstOrDefault(provider => provider.Id == id));

		public Task<IEnumerable<InferenceProvider>> GetEnabledAsync(CancellationToken ct = default)
			=> Task.FromResult<IEnumerable<InferenceProvider>>((providers ?? []).Where(provider => provider.IsEnabled).ToList());

		public Task<InferenceProvider> CreateAsync(InferenceProvider provider, CancellationToken ct = default)
			=> throw new NotSupportedException();

		public Task<InferenceProvider> UpdateAsync(InferenceProvider provider, CancellationToken ct = default)
			=> throw new NotSupportedException();

		public Task DeleteAsync(Guid id, CancellationToken ct = default)
			=> throw new NotSupportedException();

		public Task<IEnumerable<InferenceModel>> GetModelsAsync(Guid providerId, CancellationToken ct = default)
			=> Task.FromResult<IEnumerable<InferenceModel>>([]);

		public Task<IEnumerable<InferenceModel>> RefreshModelsAsync(Guid providerId, CancellationToken ct = default)
			=> Task.FromResult<IEnumerable<InferenceModel>>([]);

		public Task SetModelForTaskAsync(Guid providerId, string modelId, string taskType, CancellationToken ct = default)
			=> throw new NotSupportedException();

		public Task<InferenceModel?> GetModelForTaskAsync(string taskType, CancellationToken ct = default)
			=> Task.FromResult(fallbackModel);
	}

	/// <summary>
	/// HttpContent that delivers <paramref name="initialBody"/> immediately then blocks
	/// indefinitely until the request cancellation token fires. Simulates a model that
	/// starts generating tokens then stalls mid-stream (e.g. OOM on low-RAM hardware).
	/// </summary>
	private sealed class HangingStreamContent : HttpContent
	{
		private readonly byte[] _initialBytes;
		private readonly CancellationToken _requestCt;

		public HangingStreamContent(string initialBody, CancellationToken requestCt)
		{
			_initialBytes = Encoding.UTF8.GetBytes(initialBody);
			_requestCt = requestCt;
			Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-ndjson");
		}

		protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
			=> throw new NotSupportedException("Use ReadAsStreamAsync instead.");

		protected override bool TryComputeLength(out long length)
		{
			length = 0;
			return false;
		}

		protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken ct)
			=> Task.FromResult<Stream>(new HangingStream(_initialBytes, _requestCt));
	}

	/// <summary>
	/// Stream that returns pre-buffered bytes synchronously, then blocks indefinitely
	/// until any cancellation token fires. Simulates a mid-stream model stall.
	/// </summary>
	private sealed class HangingStream : Stream
	{
		private readonly byte[] _buffer;
		private int _position;
		private readonly CancellationToken _externalCt;

		public HangingStream(byte[] buffer, CancellationToken externalCt)
		{
			_buffer = buffer;
			_externalCt = externalCt;
		}

		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

		public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			if (_position < _buffer.Length)
			{
				var toRead = Math.Min(buffer.Length, _buffer.Length - _position);
				_buffer.AsSpan(_position, toRead).CopyTo(buffer.Span);
				_position += toRead;
				return ValueTask.FromResult(toRead);
			}
			return BlockUntilCancelledAsync(cancellationToken);
		}

		private async ValueTask<int> BlockUntilCancelledAsync(CancellationToken cancellationToken)
		{
			using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _externalCt);
			try { await Task.Delay(Timeout.Infinite, linked.Token).ConfigureAwait(false); }
			catch (OperationCanceledException) { }
			return 0;
		}

		public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
			=> ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

		public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		public override void Flush() { }
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	}
}
