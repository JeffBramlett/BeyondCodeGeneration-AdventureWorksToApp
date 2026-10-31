using System.Net;
using System.Net.Sockets;
using AdventureWorks.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AdventureWorks.Api.Tests;

public sealed class KestrelRequestBodyLimitTests
{
	[Fact]
	public async Task HostAcceptsLimitAndReturns413ForOversizedLengthAndChunkedBodies()
	{
		await using var host = await StartBodyConsumerAsync();
		using var client = new HttpClient { BaseAddress = host.Address };

		using var exactRequest = new HttpRequestMessage(HttpMethod.Post, "/body")
		{
			Content = new ByteArrayContent(new byte[checked((int)ApiRequestBodyLimit.MaximumRequestBodyBytes)])
		};
		using var exactResponse = await client.SendAsync(exactRequest);
		Assert.Equal(HttpStatusCode.NoContent, exactResponse.StatusCode);

		using var oversizedRequest = new HttpRequestMessage(HttpMethod.Post, "/body")
		{
			Content = new ByteArrayContent(new byte[checked((int)ApiRequestBodyLimit.MaximumRequestBodyBytes + 1)])
		};
		using var oversizedResponse = await client.SendAsync(oversizedRequest);
		Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedResponse.StatusCode);

		using var chunkedRequest = new HttpRequestMessage(HttpMethod.Post, "/body")
		{
			Version = HttpVersion.Version11,
			VersionPolicy = HttpVersionPolicy.RequestVersionExact,
			Content = new UnknownLengthContent(new byte[checked((int)ApiRequestBodyLimit.MaximumRequestBodyBytes + 1)])
		};
		chunkedRequest.Headers.TransferEncodingChunked = true;
		using var chunkedResponse = await client.SendAsync(chunkedRequest);
		Assert.Equal(HttpStatusCode.RequestEntityTooLarge, chunkedResponse.StatusCode);
	}

	private static async Task<TestBodyHost> StartBodyConsumerAsync()
	{
		var builder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			EnvironmentName = Environments.Production
		});
		builder.WebHost.ConfigureKestrel(ApiRequestBodyLimit.ConfigureKestrel);
		builder.WebHost.UseUrls("http://127.0.0.1:0");

		var app = builder.Build();
		app.MapPost("/body", async (HttpRequest request, CancellationToken cancellationToken) =>
		{
			try
			{
				await request.Body.CopyToAsync(Stream.Null, cancellationToken);
				return Results.NoContent();
			}
			catch (Microsoft.AspNetCore.Http.BadHttpRequestException exception)
				when (exception.StatusCode == StatusCodes.Status413RequestEntityTooLarge)
			{
				return Results.StatusCode(StatusCodes.Status413RequestEntityTooLarge);
			}
		});

		await app.StartAsync();
		var server = app.Services.GetRequiredService<IServer>();
		var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses
			?? throw new InvalidOperationException("Kestrel did not publish its bound address.");
		var address = addresses.Single();
		return new TestBodyHost(app, new Uri(address));
	}

	private sealed class TestBodyHost(WebApplication application, Uri address) : IAsyncDisposable
	{
		public Uri Address { get; } = address;

		public async ValueTask DisposeAsync() => await application.DisposeAsync();
	}

	private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
	{
		protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
			stream.WriteAsync(bytes).AsTask();

		protected override bool TryComputeLength(out long length)
		{
			length = 0;
			return false;
		}
	}
}