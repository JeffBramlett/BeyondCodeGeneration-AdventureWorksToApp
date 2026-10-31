namespace AdventureWorks.Api.Data;

public sealed record SqlServerPrincipalSnapshot(
	bool IsSysAdmin,
	bool IsDbOwner,
	IReadOnlyCollection<string> EffectivePermissions);

public static class SqlServerPrincipalGuard
{
	private static readonly string[] RequiredPermissions =
	[
		"CONNECT SQL",
		"VIEW ANY DATABASE",
		"VIEW ANY COLUMN ENCRYPTION KEY DEFINITION",
		"VIEW ANY COLUMN MASTER KEY DEFINITION",
		"CONNECT",
		"SELECT:Production.Product",
		"SELECT:Production.ProductSubcategory",
		"SELECT:Production.ProductCategory",
		"SELECT:Sales.SpecialOfferProduct",
		"SELECT:Sales.SpecialOffer",
		"SELECT:Sales.Customer",
		"SELECT:Person.Person",
		"SELECT:Sales.Store"
	];

	// fn_my_permissions(<object>, 'OBJECT') reports column-level permissions as rows with a non-empty
	// subentity_name; a three-part column securable returns no rows, so the object form is required.
	public const string OrderColumnPermissionSnapshotSql = """
		SELECT permission.permission_name COLLATE DATABASE_DEFAULT + N':Sales.SalesOrderHeader.' +
		       permission.subentity_name COLLATE DATABASE_DEFAULT
		FROM sys.fn_my_permissions(N'[Sales].[SalesOrderHeader]', 'OBJECT') AS permission
		WHERE permission.subentity_name <> N''
		UNION ALL
		SELECT permission.permission_name COLLATE DATABASE_DEFAULT + N':Sales.SalesOrderDetail.' +
		       permission.subentity_name COLLATE DATABASE_DEFAULT
		FROM sys.fn_my_permissions(N'[Sales].[SalesOrderDetail]', 'OBJECT') AS permission
		WHERE permission.subentity_name <> N'';
		""";

	// Object-level snapshot used by VerifyPrincipalAsync. Column-level SELECT rows (non-empty subentity_name) are
	// excluded so a column grant is not misreported as a table-wide SELECT; non-SELECT column rows (e.g. a
	// column-level UPDATE) are kept so the mutation check still sees them.
	public const string ObjectPermissionSnapshotSql = """
		SELECT permission.permission_name COLLATE DATABASE_DEFAULT + N':' +
		       schemaInfo.name COLLATE DATABASE_DEFAULT + N'.' +
		       objectInfo.name COLLATE DATABASE_DEFAULT
		FROM sys.objects AS objectInfo
		INNER JOIN sys.schemas AS schemaInfo ON schemaInfo.schema_id = objectInfo.schema_id
		CROSS APPLY sys.fn_my_permissions(
			(QUOTENAME(schemaInfo.name) COLLATE DATABASE_DEFAULT + N'.' +
			 QUOTENAME(objectInfo.name) COLLATE DATABASE_DEFAULT) COLLATE DATABASE_DEFAULT,
			'OBJECT') AS permission
		WHERE objectInfo.is_ms_shipped = 0
		  AND (permission.subentity_name = N'' OR permission.permission_name <> N'SELECT');
		""";

	public static void EnsureLeastPrivilege(SqlServerPrincipalSnapshot principal)
		=> EnsureLeastPrivilege(principal, []);

	public static void EnsureLeastPrivilege(
		SqlServerPrincipalSnapshot principal,
		IEnumerable<string> additionalRequiredPermissions)
	{
		ArgumentNullException.ThrowIfNull(principal);
		ArgumentNullException.ThrowIfNull(additionalRequiredPermissions);

		if (principal.IsSysAdmin)
		{
			throw new SqlServerPrincipalGuardException("sysadmin", "SQL Server sysadmin principals are not permitted for application reads.");
		}
		if (principal.IsDbOwner)
		{
			throw new SqlServerPrincipalGuardException("db_owner", "SQL Server db_owner principals are not permitted for application reads.");
		}

		var actual = principal.EffectivePermissions
			.Select(NormalizePermission)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var required = RequiredPermissions
			.Concat(additionalRequiredPermissions)
			.Select(NormalizePermission)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var missing = required.Except(actual, StringComparer.OrdinalIgnoreCase)
			.Order(StringComparer.OrdinalIgnoreCase)
			.ToArray();
		var unexpectedMutations = actual
			.Where(IsMutationOrAdministrationPermission)
			.Order(StringComparer.OrdinalIgnoreCase)
			.ToArray();

		if (missing.Length > 0 || unexpectedMutations.Length > 0)
		{
			throw new SqlServerPrincipalGuardException(
				"effective permissions",
				"The SQL Server principal must have the approved read permissions and no write or administrative permissions.",
				unexpectedMutations,
				missing);
		}
	}

	public static void EnsureOrderReadPermissions(SqlServerPrincipalSnapshot principal)
	{
		ArgumentNullException.ThrowIfNull(principal);
		if (principal.IsSysAdmin)
		{
			throw new SqlServerPrincipalGuardException("sysadmin", "SQL Server sysadmin principals are not permitted for application reads.");
		}

		if (principal.IsDbOwner)
		{
			throw new SqlServerPrincipalGuardException("db_owner", "SQL Server db_owner principals are not permitted for application reads.");
		}

		var actual = principal.EffectivePermissions
			.Select(NormalizePermission)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var required = RequiredPermissions
			.Concat(OrderReadSql.RequiredPermissions)
			.Select(NormalizePermission)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var missing = required
			.Except(actual, StringComparer.OrdinalIgnoreCase)
			.Order(StringComparer.OrdinalIgnoreCase)
			.ToArray();
		var expectedOrderPermissions = OrderReadSql.RequiredPermissions
			.Select(NormalizePermission)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var unexpectedOrderReads = actual
			.Where(permission =>
				permission.Equals("SELECT", StringComparison.OrdinalIgnoreCase) ||
				permission.Equals("SELECT:SCHEMA:SALES", StringComparison.OrdinalIgnoreCase) ||
				permission.Equals("SELECT:SALES.SALESORDERHEADER", StringComparison.OrdinalIgnoreCase) ||
				permission.Equals("SELECT:SALES.SALESORDERDETAIL", StringComparison.OrdinalIgnoreCase) ||
				(permission.StartsWith("SELECT:SALES.SALESORDERHEADER.", StringComparison.OrdinalIgnoreCase) ||
				 permission.StartsWith("SELECT:SALES.SALESORDERDETAIL.", StringComparison.OrdinalIgnoreCase)) &&
				!expectedOrderPermissions.Contains(permission))
			.Concat(actual.Where(IsMutationOrAdministrationPermission))
			.Order(StringComparer.OrdinalIgnoreCase)
			.ToArray();

		if (missing.Length > 0 || unexpectedOrderReads.Length > 0)
		{
			throw new SqlServerPrincipalGuardException(
				"order table permissions",
				"The SQL Server principal must have only the approved order-column SELECT permissions.",
				unexpectedOrderReads,
				missing);
		}
	}

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

	private static string NormalizePermission(string permission) =>
		string.Join(':', permission.Split(':', 2).Select(part => part.Trim()));
}

public sealed class SqlServerPrincipalGuardException(
	string failureCategory,
	string message,
	IReadOnlyList<string>? unexpectedPermissions = null,
	IReadOnlyList<string>? missingPermissions = null) : InvalidOperationException(message)
{
	public string FailureCategory { get; } = failureCategory;
	public IReadOnlyList<string> UnexpectedPermissions { get; } = unexpectedPermissions ?? [];
	public IReadOnlyList<string> MissingPermissions { get; } = missingPermissions ?? [];
}