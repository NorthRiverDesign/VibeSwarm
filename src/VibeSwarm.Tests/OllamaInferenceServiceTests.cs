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
				StreamInactivityTimeout = TimeSpan.FromSeconds(1)
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
		OllamaInferenceService.RuntimeOptions? runtimeOptions = null)
	{
		var httpClient = new HttpClient(handler)
		{
			BaseAddress = new Uri("http://localhost:11434")
		};

		return new OllamaInferenceService(
			new FakeHttpClientFactory(httpClient),
			new FakeInferenceProviderService(),
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

	private sealed class FakeInferenceProviderService : IInferenceProviderService
	{
		public Task<IEnumerable<InferenceProvider>> GetAllAsync(CancellationToken ct = default)
			=> Task.FromResult<IEnumerable<InferenceProvider>>([]);

		public Task<InferenceProvider?> GetByIdAsync(Guid id, CancellationToken ct = default)
			=> Task.FromResult<InferenceProvider?>(null);

		public Task<IEnumerable<InferenceProvider>> GetEnabledAsync(CancellationToken ct = default)
			=> Task.FromResult<IEnumerable<InferenceProvider>>([]);

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
			=> Task.FromResult<InferenceModel?>(null);
	}
}
