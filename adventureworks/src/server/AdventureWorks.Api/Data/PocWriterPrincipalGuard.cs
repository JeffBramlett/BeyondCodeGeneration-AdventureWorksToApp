namespace AdventureWorks.Api.Data;

public sealed record PocWriterPrincipalSnapshot(
	string DataSource,
	string DatabaseName,
	string OriginalLogin,
	string ServerLogin,
	string DatabaseUser,
	bool HasOriginalDatabaseAccess,
	bool IsSysAdmin,
	bool IsDbOwner,
	IReadOnlyCollection<string> EffectivePermissions);

public static class PocWriterPrincipalGuard
{
	public static void EnsureReadyForOperation(
		PocWriterPrincipalSnapshot principal,
		IEnumerable<string> reviewedEffectivePermissions)
	{
		ArgumentNullException.ThrowIfNull(principal);
		ArgumentNullException.ThrowIfNull(reviewedEffectivePermissions);
		ArgumentNullException.ThrowIfNull(principal.EffectivePermissions);

		if (!string.Equals(principal.DataSource, PocWriterBoundaryConfiguration.TargetServer, StringComparison.OrdinalIgnoreCase)
			|| !string.Equals(principal.DatabaseName, PocWriterBoundaryConfiguration.TargetDatabase, StringComparison.OrdinalIgnoreCase)
			|| !MatchesRuntimeLogin(principal.OriginalLogin)
			|| !MatchesRuntimeLogin(principal.ServerLogin)
			|| !MatchesRuntimeLogin(principal.DatabaseUser))
		{
			throw new PocWriterPrincipalGuardException("identity", "The POC writer principal is not connected to its pinned local clone identity.");
		}

		if (principal.HasOriginalDatabaseAccess)
		{
			throw new PocWriterPrincipalGuardException("original database", "The POC writer principal must not access the original AdventureWorks database.");
		}

		if (principal.IsSysAdmin || principal.IsDbOwner)
		{
			throw new PocWriterPrincipalGuardException("elevated role", "Elevated SQL Server principals are not permitted for the POC writer.");
		}

		var reviewed = reviewedEffectivePermissions
			.Select(NormalizePermission)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		if (reviewed.Count == 0 || !reviewed.Any(IsMutationOrAdministrationPermission))
		{
			throw new PocWriterPrincipalGuardException("permission policy", "A reviewed effective-permission set including required write permissions is required.");
		}

		var actual = principal.EffectivePermissions
			.Select(NormalizePermission)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var missing = reviewed.Except(actual, StringComparer.OrdinalIgnoreCase).ToArray();
		var unexpected = actual.Except(reviewed, StringComparer.OrdinalIgnoreCase).ToArray();
		if (missing.Length > 0 || unexpected.Length > 0)
		{
			throw new PocWriterPrincipalGuardException("effective permissions", "The effective SQL Server permissions do not match the reviewed POC writer permission set.");
		}
	}

	private static bool MatchesRuntimeLogin(string login) =>
		string.Equals(login, PocWriterBoundaryConfiguration.RuntimeLogin, StringComparison.OrdinalIgnoreCase);

	private static string NormalizePermission(string permission) =>
		string.Join(':', permission.Split(':', 2).Select(part => part.Trim()));

	private static bool IsMutationOrAdministrationPermission(string permission)
	{
		var permissionName = permission.Split(':', 2)[0];
		return permissionName.Equals("INSERT", StringComparison.OrdinalIgnoreCase)
			|| permissionName.Equals("UPDATE", StringComparison.OrdinalIgnoreCase)
			|| permissionName.Equals("DELETE", StringComparison.OrdinalIgnoreCase)
			|| permissionName.Equals("ALTER", StringComparison.OrdinalIgnoreCase)
			|| permissionName.Equals("CONTROL", StringComparison.OrdinalIgnoreCase)
			|| permissionName.Equals("TAKE OWNERSHIP", StringComparison.OrdinalIgnoreCase)
			|| permissionName.StartsWith("CREATE ", StringComparison.OrdinalIgnoreCase)
			|| permissionName.StartsWith("ALTER ANY ", StringComparison.OrdinalIgnoreCase)
			|| permissionName.StartsWith("IMPERSONATE", StringComparison.OrdinalIgnoreCase)
			|| permissionName.StartsWith("BACKUP ", StringComparison.OrdinalIgnoreCase)
			|| permissionName.StartsWith("RESTORE ", StringComparison.OrdinalIgnoreCase);
	}
}

public sealed class PocWriterPrincipalGuardException(string failureCategory, string message) : InvalidOperationException(message)
{
	public string FailureCategory { get; } = failureCategory;
}