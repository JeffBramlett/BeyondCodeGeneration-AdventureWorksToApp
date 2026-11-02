using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;

namespace AdventureWorks.Api.Data;

/// <summary>
/// Local-demo writer configuration: clone-only SQL login AdventureWorksTestWriter on AdventureWorks2022_Test.
/// The password is a DPAPI (CurrentUser) protected, hex-encoded Unicode string, loaded exactly like the read adapter.
/// </summary>
public sealed class SqlServerOrderWriterConfiguration
{
	public const string CredentialFileEnvironmentVariable = "ADVENTUREWORKS_TEST_WRITER_CREDENTIAL_FILE";
	public const string ExpectedDatabase = "AdventureWorks2022_Test";
	public const string ExpectedLogin = "AdventureWorksTestWriter";
	public const string ApplicationName = "AdventureWorks.DemoOrderWriter";
	private const string CredentialRelativePath = "AdventureWorks\\test-writer.credential";

	private SqlServerOrderWriterConfiguration(string? connectionString)
	{
		ConnectionString = string.IsNullOrWhiteSpace(connectionString) ? null : connectionString;
	}

	public string? ConnectionString { get; }

	public bool IsConfigured => ConnectionString is not null;

	public static SqlServerOrderWriterConfiguration FromEnvironment(
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

		return new SqlServerOrderWriterConfiguration(LoadProtectedConnectionString(credentialPath));
	}

	private static string? LoadProtectedConnectionString(string? credentialPath)
	{
		if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(credentialPath) || !File.Exists(credentialPath))
		{
			return null;
		}

		byte[]? protectedBytes = null;
		byte[]? passwordBytes = null;
		try
		{
			var encryptedText = File.ReadAllText(credentialPath).Trim();
			protectedBytes = Convert.FromHexString(encryptedText);
			passwordBytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
			var password = Encoding.Unicode.GetString(passwordBytes);
			if (string.IsNullOrWhiteSpace(password))
			{
				return null;
			}

			var builder = new SqlConnectionStringBuilder
			{
				DataSource = "localhost",
				InitialCatalog = ExpectedDatabase,
				UserID = ExpectedLogin,
				Password = password,
				IntegratedSecurity = false,
				Encrypt = true,
				TrustServerCertificate = true,
				PersistSecurityInfo = false,
				ConnectTimeout = 5,
				ApplicationName = ApplicationName
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

			if (passwordBytes is not null)
			{
				CryptographicOperations.ZeroMemory(passwordBytes);
			}
		}
	}
}
