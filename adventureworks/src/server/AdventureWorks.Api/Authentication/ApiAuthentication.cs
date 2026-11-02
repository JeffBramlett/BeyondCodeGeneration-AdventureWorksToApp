using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

namespace AdventureWorks.Api.Authentication;

public static class ApiAuthentication
{
	public const string DevelopmentAuthenticationEnvironmentVariable = "ADVENTUREWORKS_DEV_AUTH_ENABLED";
	public const string DevelopmentEnabledConfigurationKey = "Authentication:Development:Enabled";
	public const string InternalStaffClaimType = "app_role";
	public const string InternalStaffClaimValue = "InternalStaff";
	public const string DevelopmentIdentityIssuer = "urn:adventureworks:local-development";
	public const string DevelopmentScheme = NegotiateDefaults.AuthenticationScheme;
	public const string UnauthenticatedScheme = "Unauthenticated";

	public static bool Configure(WebApplicationBuilder builder)
	{
		var configuredValue = Environment.GetEnvironmentVariable(DevelopmentAuthenticationEnvironmentVariable)
			?? builder.Configuration[DevelopmentEnabledConfigurationKey]
			?? builder.Configuration[DevelopmentAuthenticationEnvironmentVariable]
			?? builder.WebHost.GetSetting(DevelopmentAuthenticationEnvironmentVariable);
		var enabled = ParseEnabled(configuredValue);

		if (enabled && !builder.Environment.IsDevelopment())
		{
			throw new InvalidOperationException(
				$"{DevelopmentAuthenticationEnvironmentVariable} can only be enabled when ASPNETCORE_ENVIRONMENT is Development.");
		}

		if (enabled)
		{
			DevelopmentLoopbackAddressGuard.ValidateConfiguredListeners(builder.Configuration);
			builder.Services.AddHostedService<DevelopmentLoopbackAddressValidationService>();
		}

		var scheme = enabled ? DevelopmentScheme : UnauthenticatedScheme;
		var authentication = builder.Services.AddAuthentication(options =>
		{
			options.DefaultAuthenticateScheme = scheme;
			options.DefaultChallengeScheme = scheme;
			options.DefaultForbidScheme = scheme;
		});

		if (enabled)
		{
			authentication.AddNegotiate(DevelopmentScheme, options =>
			{
				options.Events = new NegotiateEvents
				{
					OnAuthenticated = context =>
					{
						if (context.Principal is not null)
						{
							DevelopmentStaffIdentity.AddInternalStaffClaim(context.Principal);
						}

						return Task.CompletedTask;
					}
				};
			});
		}
		else
		{
			authentication.AddScheme<AuthenticationSchemeOptions, UnauthenticatedAuthenticationHandler>(UnauthenticatedScheme, _ => { });
		}

		var internalStaffPolicy = new AuthorizationPolicyBuilder()
			.RequireAuthenticatedUser()
			.RequireClaim(InternalStaffClaimType, InternalStaffClaimValue)
			.Build();
		builder.Services.AddAuthorization(options =>
		{
			options.AddPolicy("InternalStaff", internalStaffPolicy);
			options.FallbackPolicy = internalStaffPolicy;
		});

		return enabled;
	}

	private static bool ParseEnabled(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return false;
		}

		if (bool.TryParse(value, out var enabled))
		{
			return enabled;
		}

		throw new InvalidOperationException($"{DevelopmentAuthenticationEnvironmentVariable} must be 'true' or 'false'.");
	}
}

public static class DevelopmentStaffIdentity
{
	public static bool AddInternalStaffClaim(ClaimsPrincipal principal)
	{
		ArgumentNullException.ThrowIfNull(principal);

		var identity = principal.Identities.FirstOrDefault(candidate => candidate.IsAuthenticated);
		if (identity is null)
		{
			return false;
		}

		if (!identity.HasClaim(ApiAuthentication.InternalStaffClaimType, ApiAuthentication.InternalStaffClaimValue))
		{
			identity.AddClaim(new Claim(
				ApiAuthentication.InternalStaffClaimType,
				ApiAuthentication.InternalStaffClaimValue,
				ClaimValueTypes.String,
				ApiAuthentication.DevelopmentIdentityIssuer));
		}

		return true;
	}
}

public sealed class UnauthenticatedAuthenticationHandler(
	IOptionsMonitor<AuthenticationSchemeOptions> options,
	ILoggerFactory logger,
	UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
	protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
		Task.FromResult(AuthenticateResult.NoResult());
}

public static class DevelopmentLoopbackAddressGuard
{
	public static void ValidateConfiguredListeners(IConfiguration configuration)
	{
		var urls = configuration["urls"];
		if (!string.IsNullOrWhiteSpace(urls))
		{
			foreach (var url in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				EnsureLoopbackUrl(url);
			}
		}

		foreach (var endpoint in configuration.GetSection("Kestrel:Endpoints").GetChildren())
		{
			var url = endpoint["Url"];
			if (!string.IsNullOrWhiteSpace(url))
			{
				EnsureLoopbackUrl(url);
			}
		}

		if (!string.IsNullOrWhiteSpace(configuration["http_ports"]) ||
			!string.IsNullOrWhiteSpace(configuration["https_ports"]))
		{
			throw new InvalidOperationException("Development authentication requires explicitly loopback-bound API listeners.");
		}
	}

	public static void ValidateListeningAddresses(IEnumerable<string> addresses)
	{
		var addressList = addresses.ToArray();
		if (addressList.Length == 0 || addressList.Any(address => !IsLoopbackUrl(address)))
		{
			throw new InvalidOperationException("Development authentication requires every API listener to be bound to loopback.");
		}
	}

	public static bool IsLoopbackUrl(string address)
	{
		if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
		{
			return false;
		}

		return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
			|| IPAddress.TryParse(uri.Host, out var ipAddress) && IPAddress.IsLoopback(ipAddress);
	}

	private static void EnsureLoopbackUrl(string address)
	{
		if (!IsLoopbackUrl(address))
		{
			throw new InvalidOperationException("Development authentication requires every configured API listener to use localhost or a loopback IP address.");
		}
	}
}

public sealed class DevelopmentLoopbackAddressValidationService(IServer server, IConfiguration configuration) : IHostedService
{
	public Task StartAsync(CancellationToken cancellationToken)
	{
		var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
		if (addresses.Count > 0)
		{
			DevelopmentLoopbackAddressGuard.ValidateListeningAddresses(addresses);
		}
		else
		{
			if (string.IsNullOrWhiteSpace(configuration["urls"]) &&
				!configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
			{
				throw new InvalidOperationException("Development authentication requires an explicit loopback API listener.");
			}

			DevelopmentLoopbackAddressGuard.ValidateConfiguredListeners(configuration);
		}

		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}