using System.Security.Cryptography;
using System.Text;
using AdventureWorks.Api.Data;
using Xunit;

namespace AdventureWorks.Api.Tests;

public sealed class PocWriterBoundaryTests
{
	private static readonly string[] ReviewedPermissions =
	[
		"CONNECT SQL",
		"CONNECT",
		"INSERT:Sales.SalesOrderHeader",
		"INSERT:Sales.SalesOrderDetail"
	];

	[Fact]
	public void Configuration_IsUnconfiguredWhenCredentialEnvironmentAndFileAreMissing()
	{
		var configuration = PocWriterBoundaryConfiguration.FromEnvironment(_ => null);

		Assert.False(configuration.IsConfigured);
		Assert.Equal("localhost", PocWriterBoundaryConfiguration.TargetServer);
		Assert.Equal("AdventureWorks2022_Test", PocWriterBoundaryConfiguration.TargetDatabase);
		Assert.Equal("AdventureWorksPocApiWriter", PocWriterBoundaryConfiguration.RuntimeLogin);
		Assert.Null(typeof(PocWriterBoundaryConfiguration).GetProperty("ConnectionString"));
	}

	[Fact]
	public void Configuration_IsUnconfiguredWhenCredentialFileIsInvalid()
	{
		var credentialPath = Path.Combine(Path.GetTempPath(), $"aw-poc-invalid-{Guid.NewGuid():N}.txt");
		try
		{
			File.WriteAllText(credentialPath, "not-protected-hex");
			var configuration = PocWriterBoundaryConfiguration.FromEnvironment(name =>
				name == PocWriterBoundaryConfiguration.CredentialFileEnvironmentVariable ? credentialPath : null);

			Assert.False(configuration.IsConfigured);
		}
		finally
		{
			if (File.Exists(credentialPath))
			{
				File.Delete(credentialPath);
			}
		}
	}

	[Fact]
	public void Configuration_ValidatesProtectedCredentialWithoutRetainingOrExposingSecret()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		const string secret = "not-a-real-poc-writer-secret";
		var credentialPath = Path.Combine(Path.GetTempPath(), $"aw-poc-credential-{Guid.NewGuid():N}.txt");
		var clearBytes = Encoding.Unicode.GetBytes(secret);
		var protectedBytes = ProtectedData.Protect(clearBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
		try
		{
			File.WriteAllText(credentialPath, Convert.ToHexString(protectedBytes));
			var configuration = PocWriterBoundaryConfiguration.FromEnvironment(name =>
				name == PocWriterBoundaryConfiguration.CredentialFileEnvironmentVariable ? credentialPath : null);

			Assert.True(configuration.IsConfigured);
			Assert.DoesNotContain(secret, configuration.ToString(), StringComparison.Ordinal);
			Assert.Null(typeof(PocWriterBoundaryConfiguration).GetProperty("ConnectionString"));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(clearBytes);
			CryptographicOperations.ZeroMemory(protectedBytes);
			if (File.Exists(credentialPath))
			{
				File.Delete(credentialPath);
			}
		}
	}

	[Fact]
	public void PrincipalGuard_AllowsOnlyPinnedIdentityAndReviewedEffectivePermissions()
	{
		PocWriterPrincipalGuard.EnsureReadyForOperation(ValidPrincipal(ReviewedPermissions), ReviewedPermissions);
	}

	[Theory]
	[InlineData("AdventureWorks2022")]
	[InlineData("master")]
	public void PrincipalGuard_RejectsWrongDatabase(string databaseName)
	{
		var exception = Assert.Throws<PocWriterPrincipalGuardException>(() =>
			PocWriterPrincipalGuard.EnsureReadyForOperation(
				ValidPrincipal(ReviewedPermissions) with { DatabaseName = databaseName },
				ReviewedPermissions));

		Assert.Equal("identity", exception.FailureCategory);
	}

	[Fact]
	public void PrincipalGuard_RejectsReadOnlyReaderIdentity()
	{
		var exception = Assert.Throws<PocWriterPrincipalGuardException>(() =>
			PocWriterPrincipalGuard.EnsureReadyForOperation(
				ValidPrincipal(["CONNECT SQL", "CONNECT"]) with
				{
					OriginalLogin = "AdventureWorksTestReader",
					ServerLogin = "AdventureWorksTestReader",
					DatabaseUser = "AdventureWorksTestReader"
				},
				ReviewedPermissions));

		Assert.Equal("identity", exception.FailureCategory);
	}

	[Fact]
	public void PrincipalGuard_RejectsMissingWritePermission()
	{
		var exception = Assert.Throws<PocWriterPrincipalGuardException>(() =>
			PocWriterPrincipalGuard.EnsureReadyForOperation(
				ValidPrincipal(["CONNECT SQL", "CONNECT"]),
				ReviewedPermissions));

		Assert.Equal("effective permissions", exception.FailureCategory);
	}

	[Fact]
	public void PrincipalGuard_RejectsOriginalDatabaseAccess()
	{
		var exception = Assert.Throws<PocWriterPrincipalGuardException>(() =>
			PocWriterPrincipalGuard.EnsureReadyForOperation(
				ValidPrincipal(ReviewedPermissions) with { HasOriginalDatabaseAccess = true },
				ReviewedPermissions));

		Assert.Equal("original database", exception.FailureCategory);
	}

	[Theory]
	[InlineData("remote-sql-host")]
	public void PrincipalGuard_RejectsWrongDataSource(string dataSource)
	{
		var exception = Assert.Throws<PocWriterPrincipalGuardException>(() =>
			PocWriterPrincipalGuard.EnsureReadyForOperation(
				ValidPrincipal(ReviewedPermissions) with { DataSource = dataSource },
				ReviewedPermissions));

		Assert.Equal("identity", exception.FailureCategory);
	}

	private static PocWriterPrincipalSnapshot ValidPrincipal(IReadOnlyCollection<string> permissions) => new(
		DataSource: "localhost",
		DatabaseName: "AdventureWorks2022_Test",
		OriginalLogin: "AdventureWorksPocApiWriter",
		ServerLogin: "AdventureWorksPocApiWriter",
		DatabaseUser: "AdventureWorksPocApiWriter",
		HasOriginalDatabaseAccess: false,
		IsSysAdmin: false,
		IsDbOwner: false,
		EffectivePermissions: permissions);
}