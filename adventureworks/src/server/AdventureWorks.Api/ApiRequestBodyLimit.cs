using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace AdventureWorks.Api;

public static class ApiRequestBodyLimit
{
	public const long MaximumRequestBodyBytes = 32 * 1024;

	public static void ConfigureKestrel(KestrelServerOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);
		options.Limits.MaxRequestBodySize = MaximumRequestBodyBytes;
	}
}