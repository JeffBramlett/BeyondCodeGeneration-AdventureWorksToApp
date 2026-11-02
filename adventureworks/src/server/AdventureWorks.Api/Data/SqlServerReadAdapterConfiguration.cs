using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;

namespace AdventureWorks.Api.Data;

public sealed class SqlServerReadAdapterConfiguration
{
	public const string CredentialFileEnvironmentVariable = "ADVENTUREWORKS_READONLY_SQL_CREDENTIAL_FILE";
	private const string CredentialRelativePath = "AdventureWorks\\test-reader.credential";

	private SqlServerReadAdapterConfiguration(string? connectionString)
	{
		ConnectionString = string.IsNullOrWhiteSpace(connectionString) ? null : connectionString;
	}

	public string? ConnectionString { get; }

	public bool IsConfigured => ConnectionString is not null;

	public static SqlServerReadAdapterConfiguration FromEnvironment(
		Func<string, string?>? getEnvironmentVariable = null)
	{
		getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
		var credentialPath = getEnvironmentVariable(CredentialFileEnvironmentVariable);
		if (string.IsNullOrWhiteSpace(credentialPath))
		{
			var localAppData = getEnvironmentVariable("LOCALAPPDATA");
			credentialPath = string.IsNullOrWhiteSpace(localAppData)
				? null
				: Path.Combine(localAppData, CredentialRelativePath);
		}

		return new SqlServerReadAdapterConfiguration(LoadProtectedConnectionString(credentialPath));
	}

	private static string? LoadProtectedConnectionString(string? credentialPath)
	{
		if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(credentialPath) || !File.Exists(credentialPath))
		{
			return null;
		}

		byte[]? protectedBytes = null;
		byte[]? connectionBytes = null;
		try
		{
			var encryptedText = File.ReadAllText(credentialPath).Trim();
			protectedBytes = Convert.FromHexString(encryptedText);
			connectionBytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
			var password = Encoding.Unicode.GetString(connectionBytes);
			if (string.IsNullOrWhiteSpace(password))
			{
				return null;
			}

			var builder = new SqlConnectionStringBuilder
			{
				DataSource = "localhost",
				InitialCatalog = "AdventureWorks2022_Test",
				UserID = "AdventureWorksTestReader",
				Password = password,
				IntegratedSecurity = false,
				Encrypt = true,
				TrustServerCertificate = true,
				PersistSecurityInfo = false,
				ConnectTimeout = 5,
				ApplicationName = "AdventureWorks.ReadOnly.Test"
			};
			return builder.ConnectionString;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or CryptographicException)
		{
			return null;
		}
		finally
		{
			if (protectedBytes is not null)
			{
				CryptographicOperations.ZeroMemory(protectedBytes);
			}

			if (connectionBytes is not null)
			{
				CryptographicOperations.ZeroMemory(connectionBytes);
			}
		}
	}
}