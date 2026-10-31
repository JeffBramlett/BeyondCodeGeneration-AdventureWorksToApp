$ErrorActionPreference = 'Stop'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
	throw 'This workflow requires Windows current-user DPAPI.'
}

Add-Type -AssemblyName System.Security

$serverName = 'localhost'
$expectedServer = 'jeffs_dev_pc'
$testDatabase = 'AdventureWorks2022_Test'
$originalDatabase = 'AdventureWorks2022'
$loginName = 'AdventureWorksTestWriter'
$credentialPath = Join-Path $env:LOCALAPPDATA 'AdventureWorks\test-writer.credential'
$allowedSelect = @(
	'Sales.Customer'
	'Person.BusinessEntityAddress'
	'Purchasing.ShipMethod'
	'Sales.SpecialOfferProduct'
	'Sales.SalesOrderHeader'
	'Sales.SalesOrderDetail'
	'Production.TransactionHistory'
)
$allowedInsert = @('Sales.SalesOrderHeader', 'Sales.SalesOrderDetail')
$allowedDiagramExecutables = @(
	'dbo.fn_diagramobjects'
	'dbo.sp_alterdiagram'
	'dbo.sp_creatediagram'
	'dbo.sp_dropdiagram'
	'dbo.sp_helpdiagramdefinition'
	'dbo.sp_helpdiagrams'
	'dbo.sp_renamediagram'
	'dbo.sp_upgraddiagrams'
)
$expectedDatabasePermissions = @(
	'CONNECT'
	'VIEW ANY COLUMN ENCRYPTION KEY DEFINITION'
	'VIEW ANY COLUMN MASTER KEY DEFINITION'
)
$expectedServerPermissions = @('CONNECT SQL', 'VIEW ANY DATABASE')
$forbiddenObjectPermissions = @('ALTER', 'CONTROL', 'DELETE', 'TAKE OWNERSHIP', 'UPDATE')

function New-IntegratedConnectionString([string]$database) {
	return "Data Source=$serverName;Initial Catalog=$database;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Application Name=AdventureWorks.AW014.TestWriter"
}

function New-WriterConnectionString([string]$database, [string]$password) {
	return "Data Source=$serverName;Initial Catalog=$database;User ID=$loginName;Password=$password;Integrated Security=False;Encrypt=True;TrustServerCertificate=True;Persist Security Info=False;Application Name=AdventureWorks.AW014.TestWriter"
}

function Invoke-Table(
	[System.Data.SqlClient.SqlConnection]$connection,
	[string]$commandText,
	[System.Data.SqlClient.SqlTransaction]$transaction = $null
) {
	$command = $connection.CreateCommand()
	$command.CommandText = $commandText
	$command.Transaction = $transaction
	$adapter = [System.Data.SqlClient.SqlDataAdapter]::new($command)
	$table = [System.Data.DataTable]::new()
	try {
		[void]$adapter.Fill($table)
		return ,$table
	} finally {
		$adapter.Dispose()
		$command.Dispose()
	}
}

function Invoke-Scalar(
	[System.Data.SqlClient.SqlConnection]$connection,
	[string]$commandText,
	[System.Data.SqlClient.SqlTransaction]$transaction = $null
) {
	$command = $connection.CreateCommand()
	$command.CommandText = $commandText
	$command.Transaction = $transaction
	try {
		return $command.ExecuteScalar()
	} finally {
		$command.Dispose()
	}
}

function Assert-Equal($actual, $expected, [string]$label) {
	if ($actual -ne $expected) {
		throw "$label did not match the expected value."
	}
}

function Get-CloneCounts([string]$connectionString, [bool]$includeErrorLog = $false) {
	$connection = [System.Data.SqlClient.SqlConnection]::new($connectionString)
	$connection.Open()
	try {
		$sql = @'
SELECT
    (SELECT COUNT_BIG(*) FROM Sales.SalesOrderHeader) AS HeaderCount,
    (SELECT COUNT_BIG(*) FROM Sales.SalesOrderDetail) AS DetailCount,
    (SELECT COUNT_BIG(*) FROM Production.TransactionHistory) AS HistoryCount
'@
		if ($includeErrorLog) {
			$sql += ', (SELECT COUNT_BIG(*) FROM dbo.ErrorLog) AS ErrorLogCount'
		}
		$table = Invoke-Table $connection $sql
		return ,$table.Rows[0]
	} finally {
		$connection.Dispose()
	}
}

if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
	throw 'LOCALAPPDATA is unavailable; no database changes were made.'
}
$adminConnectionString = New-IntegratedConnectionString 'master'
$admin = [System.Data.SqlClient.SqlConnection]::new($adminConnectionString)
$admin.Open()
try {
	$preflight = Invoke-Table $admin @'
SELECT
    @@SERVERNAME AS ServerName,
    CONVERT(int, CASE WHEN EXISTS
        (SELECT 1 FROM sys.databases WHERE name=N'AdventureWorks2022_Test' AND state_desc=N'ONLINE' AND user_access_desc=N'MULTI_USER')
        THEN 1 ELSE 0 END) AS CloneReady,
    CONVERT(int, CASE WHEN EXISTS
        (SELECT 1 FROM sys.server_principals WHERE name=N'AdventureWorksTestWriter')
        THEN 1 ELSE 0 END) AS LoginExists,
    CONVERT(int, CASE WHEN EXISTS
        (SELECT 1 FROM AdventureWorks2022_Test.sys.database_principals WHERE name=N'AdventureWorksTestWriter')
        THEN 1 ELSE 0 END) AS CloneUserExists,
    CONVERT(int, CASE WHEN EXISTS
        (SELECT 1 FROM AdventureWorks2022.sys.database_principals WHERE name=N'AdventureWorksTestWriter')
        THEN 1 ELSE 0 END) AS OriginalUserExists;
'@
	$row = $preflight.Rows[0]
	Assert-Equal $row.ServerName $expectedServer 'SQL Server instance'
	Assert-Equal $row.CloneReady 1 'Clone availability'
	Assert-Equal $row.OriginalUserExists 0 'Original database mapping check'
} finally {
	$admin.Dispose()
}

$credentialExists = Test-Path -LiteralPath $credentialPath
$provisionNewWriter = $row.LoginExists -eq 0 -and $row.CloneUserExists -eq 0 -and -not $credentialExists
$reuseExistingWriter = $row.LoginExists -eq 1 -and $row.CloneUserExists -eq 1 -and $credentialExists
if (-not $provisionNewWriter -and -not $reuseExistingWriter) {
	throw 'Partial or colliding writer identity state detected; no changes were made.'
}

$password = $null
if ($provisionNewWriter) {
$randomBytes = New-Object byte[] 32
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$plainBytes = $null
$protectedBytes = $null
$password = $null
$loginCreated = $false
$userCreated = $false
try {
	$rng.GetBytes($randomBytes)
	$password = 'AwTest-' + [BitConverter]::ToString($randomBytes).Replace('-', '') + 'aZ9!'
	$plainBytes = [System.Text.Encoding]::Unicode.GetBytes($password)
	$protectedBytes = [System.Security.Cryptography.ProtectedData]::Protect(
		$plainBytes,
		$null,
		[System.Security.Cryptography.DataProtectionScope]::CurrentUser)
	$credentialDirectory = Split-Path -Parent $credentialPath
	New-Item -ItemType Directory -Path $credentialDirectory -Force | Out-Null
	[System.IO.File]::WriteAllText($credentialPath, [Convert]::ToHexString($protectedBytes), [System.Text.Encoding]::ASCII)
	$currentSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
	$fileAcl = [System.Security.AccessControl.FileSecurity]::new()
	$fileAcl.SetAccessRuleProtection($true, $false)
	$fileAcl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new(
		$currentSid,
		[System.Security.AccessControl.FileSystemRights]::FullControl,
		[System.Security.AccessControl.AccessControlType]::Allow))
	Set-Acl -LiteralPath $credentialPath -AclObject $fileAcl

	$escapedPassword = $password.Replace("'", "''")
	$admin = [System.Data.SqlClient.SqlConnection]::new($adminConnectionString)
	$admin.Open()
	try {
		$createLogin = $admin.CreateCommand()
		$createLogin.CommandText = "CREATE LOGIN [AdventureWorksTestWriter] WITH PASSWORD=N'$escapedPassword', CHECK_POLICY=ON, CHECK_EXPIRATION=OFF, DEFAULT_DATABASE=[AdventureWorks2022_Test];"
		[void]$createLogin.ExecuteNonQuery()
		$loginCreated = $true
		$createLogin.Dispose()
	} finally {
		$admin.Dispose()
	}

	$cloneAdmin = [System.Data.SqlClient.SqlConnection]::new((New-IntegratedConnectionString $testDatabase))
	$cloneAdmin.Open()
	try {
		$grantCommand = $cloneAdmin.CreateCommand()
		$grantCommand.CommandText = @'
BEGIN TRANSACTION;
CREATE USER [AdventureWorksTestWriter] FOR LOGIN [AdventureWorksTestWriter];
GRANT CONNECT TO [AdventureWorksTestWriter];
GRANT INSERT ON OBJECT::[Sales].[SalesOrderHeader] TO [AdventureWorksTestWriter];
GRANT INSERT ON OBJECT::[Sales].[SalesOrderDetail] TO [AdventureWorksTestWriter];
GRANT SELECT ON OBJECT::[Sales].[Customer] TO [AdventureWorksTestWriter];
GRANT SELECT ON OBJECT::[Person].[BusinessEntityAddress] TO [AdventureWorksTestWriter];
GRANT SELECT ON OBJECT::[Purchasing].[ShipMethod] TO [AdventureWorksTestWriter];
GRANT SELECT ON OBJECT::[Sales].[SpecialOfferProduct] TO [AdventureWorksTestWriter];
GRANT SELECT ON OBJECT::[Sales].[SalesOrderHeader] TO [AdventureWorksTestWriter];
GRANT SELECT ON OBJECT::[Sales].[SalesOrderDetail] TO [AdventureWorksTestWriter];
GRANT SELECT ON OBJECT::[Production].[TransactionHistory] TO [AdventureWorksTestWriter];
COMMIT TRANSACTION;
'@
		[void]$grantCommand.ExecuteNonQuery()
		$userCreated = $true
		$grantCommand.Dispose()
	} finally {
		$cloneAdmin.Dispose()
	}
} catch {
	if ($userCreated -or $loginCreated) {
		try {
			$cleanup = [System.Data.SqlClient.SqlConnection]::new($adminConnectionString)
			$cleanup.Open()
			$cleanupCommand = $cleanup.CreateCommand()
			$cleanupCommand.CommandText = "IF EXISTS (SELECT 1 FROM AdventureWorks2022_Test.sys.database_principals WHERE name=N'AdventureWorksTestWriter') DROP USER [AdventureWorksTestWriter]; IF EXISTS (SELECT 1 FROM sys.server_principals WHERE name=N'AdventureWorksTestWriter') DROP LOGIN [AdventureWorksTestWriter];"
			[void]$cleanupCommand.ExecuteNonQuery()
			$cleanup.Dispose()
		} catch { }
	}
	Remove-Item -LiteralPath $credentialPath -Force -ErrorAction SilentlyContinue
	throw 'Writer provisioning failed; partial clone identity and credential state were cleaned up.'
} finally {
	$rng.Dispose()
	if ($plainBytes) { [System.Security.Cryptography.CryptographicOperations]::ZeroMemory($plainBytes) }
	if ($randomBytes) { [System.Security.Cryptography.CryptographicOperations]::ZeroMemory($randomBytes) }
	if ($protectedBytes) { [System.Security.Cryptography.CryptographicOperations]::ZeroMemory($protectedBytes) }
}
} else {
	$ciphertext = [Convert]::FromHexString([System.IO.File]::ReadAllText($credentialPath).Trim())
	$plainBytes = [System.Security.Cryptography.ProtectedData]::Unprotect(
		$ciphertext,
		$null,
		[System.Security.Cryptography.DataProtectionScope]::CurrentUser)
	try {
		$password = [System.Text.Encoding]::Unicode.GetString($plainBytes)
	} finally {
		[System.Security.Cryptography.CryptographicOperations]::ZeroMemory($plainBytes)
		[System.Security.Cryptography.CryptographicOperations]::ZeroMemory($ciphertext)
	}
	if ([string]::IsNullOrWhiteSpace($password)) { throw 'The existing DPAPI credential could not be loaded.' }
}

$writerConnectionString = New-WriterConnectionString $testDatabase $password
$writer = [System.Data.SqlClient.SqlConnection]::new($writerConnectionString)
$writer.Open()
try {
	$identity = Invoke-Table $writer @'
SELECT DB_NAME() AS DatabaseName, ORIGINAL_LOGIN() AS OriginalLogin, SUSER_SNAME() AS ServerLogin,
       USER_NAME() AS DatabaseUser, CONVERT(int, IS_SRVROLEMEMBER('sysadmin')) AS IsSysadmin,
       CONVERT(int, IS_ROLEMEMBER('db_owner')) AS IsDbOwner,
       CONVERT(int, HAS_DBACCESS('AdventureWorks2022')) AS OriginalDatabaseAccess;
'@
	$identityRow = $identity.Rows[0]
	Assert-Equal $identityRow.DatabaseName $testDatabase 'Connected database'
	Assert-Equal $identityRow.OriginalLogin $loginName 'Original login'
	Assert-Equal $identityRow.ServerLogin $loginName 'Server login'
	Assert-Equal $identityRow.DatabaseUser $loginName 'Database user'
	Assert-Equal $identityRow.IsSysadmin 0 'sysadmin membership'
	Assert-Equal $identityRow.IsDbOwner 0 'db_owner membership'
	Assert-Equal $identityRow.OriginalDatabaseAccess 0 'Original database access'

	$serverPermissions = Invoke-Table $writer "SELECT permission_name FROM sys.fn_my_permissions(NULL, 'SERVER');"
	$effectiveServerPermissions = @($serverPermissions.Rows | ForEach-Object { [string]$_.permission_name } | Sort-Object -Unique)
	if (Compare-Object ($expectedServerPermissions | Sort-Object -Unique) $effectiveServerPermissions) {
		throw 'Effective server permissions differ from the reviewed non-mutating baseline.'
	}

	$databasePermissions = Invoke-Table $writer "SELECT permission_name FROM sys.fn_my_permissions(NULL, 'DATABASE');"
	$effectiveDatabasePermissions = @($databasePermissions.Rows | ForEach-Object { [string]$_.permission_name } | Sort-Object -Unique)
	if (Compare-Object ($expectedDatabasePermissions | Sort-Object -Unique) $effectiveDatabasePermissions) {
		throw 'Effective database permissions differ from the reviewed non-mutating baseline.'
	}
	$schemaPermissions = Invoke-Table $writer @'
SELECT permission.permission_name, schemaInfo.name
FROM sys.schemas AS schemaInfo
CROSS APPLY sys.fn_my_permissions(QUOTENAME(schemaInfo.name), 'SCHEMA') AS permission
WHERE schemaInfo.name NOT IN (N'sys', N'INFORMATION_SCHEMA');
'@
	if ($schemaPermissions.Rows.Count -ne 0) { throw 'Unexpected schema-scoped permissions detected.' }

	$roles = Invoke-Table $writer 'SELECT role.name FROM sys.database_role_members AS membership INNER JOIN sys.database_principals AS role ON role.principal_id=membership.role_principal_id WHERE membership.member_principal_id=DATABASE_PRINCIPAL_ID();'
	if ($roles.Rows.Count -ne 0) { throw 'Unexpected database role membership detected.' }

	$objectPermissions = Invoke-Table $writer @'
SELECT permission.permission_name,
			 schemaInfo.name COLLATE DATABASE_DEFAULT + N'.' + objectInfo.name COLLATE DATABASE_DEFAULT AS ObjectName
FROM sys.objects AS objectInfo
INNER JOIN sys.schemas AS schemaInfo ON schemaInfo.schema_id=objectInfo.schema_id
CROSS APPLY sys.fn_my_permissions(
		(QUOTENAME(schemaInfo.name) COLLATE DATABASE_DEFAULT + N'.' + QUOTENAME(objectInfo.name) COLLATE DATABASE_DEFAULT),
		'OBJECT') AS permission
WHERE objectInfo.is_ms_shipped=0
	AND objectInfo.type IN ('U', 'V', 'P', 'FN', 'IF', 'TF');
'@
	$observedSelect = @($objectPermissions.Rows | Where-Object { $_.permission_name -eq 'SELECT' } | ForEach-Object { [string]$_.ObjectName })
	$observedInsert = @($objectPermissions.Rows | Where-Object { $_.permission_name -eq 'INSERT' } | ForEach-Object { [string]$_.ObjectName })
	$observedExecute = @($objectPermissions.Rows | Where-Object { $_.permission_name -eq 'EXECUTE' })
	$unexpectedSelect = @($observedSelect | Where-Object { $allowedSelect -notcontains $_ })
	$unexpectedInsert = @($observedInsert | Where-Object { $allowedInsert -notcontains $_ })
	$unexpectedObjectMutations = @($objectPermissions.Rows | Where-Object { $forbiddenObjectPermissions -contains [string]$_.permission_name })
	$unexpectedExecute = @($observedExecute | Where-Object { $allowedDiagramExecutables -notcontains ([string]$_.ObjectName) })
	if ($unexpectedSelect.Count -gt 0 -or $unexpectedInsert.Count -gt 0 -or $unexpectedObjectMutations.Count -gt 0 -or $unexpectedExecute.Count -gt 0) {
		throw 'Effective object permissions exceed the reviewed clone-only permission matrix.'
	}
	foreach ($objectName in $allowedSelect) {
		if ($observedSelect -notcontains $objectName) { throw "Required SELECT permission is missing on $objectName." }
	}
	foreach ($objectName in $allowedInsert) {
		if ($observedInsert -notcontains $objectName) { throw "Required INSERT permission is missing on $objectName." }
	}

	$originalProbe = [System.Data.SqlClient.SqlConnection]::new((New-WriterConnectionString $originalDatabase $password))
	$originalDenied = $false
	try {
		$originalProbe.Open()
	} catch [System.Data.SqlClient.SqlException] {
		$originalDenied = $_.Exception.Number -in @(4060, 916, 18456)
	} finally {
		$originalProbe.Dispose()
	}
	if (-not $originalDenied) { throw 'Writer unexpectedly connected to the original database; no DML was attempted there.' }

	'Effective principal checks passed: no server/database roles, no db_owner/sysadmin, no disallowed mutation rights, only the seven reviewed SELECT objects and two order-table INSERT objects.'
	if ($observedExecute.Count -gt 0) {
		'Existing reviewed database-diagram EXECUTE permissions: ' + (($observedExecute | ForEach-Object { [string]$_.ObjectName } | Sort-Object -Unique) -join ', ')
	}
	'Writer connection to AdventureWorks2022 was denied; clone HAS_DBACCESS check also returned 0.'
} finally {
	$writer.Dispose()
}

$countsBefore = Get-CloneCounts $writerConnectionString
$adminCountsBefore = Get-CloneCounts (New-IntegratedConnectionString $testDatabase) $true
$writer = [System.Data.SqlClient.SqlConnection]::new($writerConnectionString)
$writer.Open()
$transaction = $null
$orderId = 0
$detailId = 0
$orderNumber = ''
try {
	$references = Invoke-Table $writer @'
SELECT TOP (1) customer.CustomerID, addressLink.AddressID
FROM Sales.Customer AS customer
INNER JOIN Person.BusinessEntityAddress AS addressLink ON addressLink.BusinessEntityID=customer.StoreID
WHERE customer.PersonID IS NULL AND customer.StoreID IS NOT NULL
ORDER BY customer.CustomerID, addressLink.AddressID;
'@
	if ($references.Rows.Count -ne 1) { throw 'No store-only customer with a linked address was available in the clone.' }
	$customerId = [int]$references.Rows[0].CustomerID
	$addressId = [int]$references.Rows[0].AddressID
	$shipMethod = Invoke-Scalar $writer 'SELECT TOP (1) ShipMethodID FROM Purchasing.ShipMethod ORDER BY ShipMethodID;'
	$validOffer = Invoke-Table $writer 'SELECT SpecialOfferID, ProductID FROM Sales.SpecialOfferProduct WHERE SpecialOfferID=1 AND ProductID=680;'
	if ($null -eq $shipMethod -or $validOffer.Rows.Count -ne 1) { throw 'The verified clone test ship-method or product/offer reference was unavailable.' }

	$transaction = $writer.BeginTransaction([System.Data.IsolationLevel]::ReadCommitted)
	$orderDate = [DateTime]::Now
	$header = $writer.CreateCommand()
	$header.Transaction = $transaction
	$header.CommandText = @'
INSERT INTO Sales.SalesOrderHeader
    (OrderDate, DueDate, CustomerID, BillToAddressID, ShipToAddressID, ShipMethodID, SalesPersonID, TerritoryID)
VALUES
    (@OrderDate, @DueDate, @CustomerID, @AddressID, @AddressID, @ShipMethodID, NULL, NULL);
SELECT CONVERT(int, SCOPE_IDENTITY());
'@
	[void]$header.Parameters.Add('@OrderDate', [System.Data.SqlDbType]::DateTime)
	$header.Parameters['@OrderDate'].Value = $orderDate
	[void]$header.Parameters.Add('@DueDate', [System.Data.SqlDbType]::DateTime)
	$header.Parameters['@DueDate'].Value = $orderDate.AddDays(7)
	[void]$header.Parameters.Add('@CustomerID', [System.Data.SqlDbType]::Int)
	$header.Parameters['@CustomerID'].Value = $customerId
	[void]$header.Parameters.Add('@AddressID', [System.Data.SqlDbType]::Int)
	$header.Parameters['@AddressID'].Value = $addressId
	[void]$header.Parameters.Add('@ShipMethodID', [System.Data.SqlDbType]::Int)
	$header.Parameters['@ShipMethodID'].Value = [int]$shipMethod
	$orderId = [int]$header.ExecuteScalar()
	$header.Dispose()

	$detail = $writer.CreateCommand()
	$detail.Transaction = $transaction
	$detail.CommandText = @'
INSERT INTO Sales.SalesOrderDetail
    (SalesOrderID, OrderQty, ProductID, SpecialOfferID, UnitPrice, UnitPriceDiscount)
VALUES
    (@SalesOrderID, 2, 680, 1, 1.25, 0);
SELECT CONVERT(int, SCOPE_IDENTITY());
'@
	[void]$detail.Parameters.Add('@SalesOrderID', [System.Data.SqlDbType]::Int)
	$detail.Parameters['@SalesOrderID'].Value = $orderId
	$detailId = [int]$detail.ExecuteScalar()
	$detail.Dispose()

	$transactionState = Invoke-Table $writer 'SELECT XACT_STATE() AS TransactionState, @@TRANCOUNT AS TransactionCount;' $transaction
	Assert-Equal $transactionState.Rows[0].TransactionState 1 'Active transaction state after triggers'
	Assert-Equal $transactionState.Rows[0].TransactionCount 1 'Transaction count after triggers'

	$persistedCommand = $writer.CreateCommand()
	$persistedCommand.Transaction = $transaction
	$persistedCommand.CommandText = @'
SELECT header.SalesOrderID, header.SalesOrderNumber, header.RevisionNumber,
       CONVERT(decimal(19,4), header.SubTotal) AS SubTotal,
       CONVERT(decimal(19,4), header.TotalDue) AS TotalDue,
       detail.SalesOrderDetailID, CONVERT(decimal(19,4), detail.LineTotal) AS LineTotal,
       history.TransactionType, history.Quantity, CONVERT(decimal(19,4), history.ActualCost) AS ActualCost
FROM Sales.SalesOrderHeader AS header
INNER JOIN Sales.SalesOrderDetail AS detail ON detail.SalesOrderID=header.SalesOrderID
INNER JOIN Production.TransactionHistory AS history
  ON history.ReferenceOrderID=detail.SalesOrderID
 AND history.ReferenceOrderLineID=detail.SalesOrderDetailID
WHERE header.SalesOrderID=@SalesOrderID AND detail.SalesOrderDetailID=@SalesOrderDetailID;
'@
	[void]$persistedCommand.Parameters.Add('@SalesOrderID', [System.Data.SqlDbType]::Int)
	$persistedCommand.Parameters['@SalesOrderID'].Value = $orderId
	[void]$persistedCommand.Parameters.Add('@SalesOrderDetailID', [System.Data.SqlDbType]::Int)
	$persistedCommand.Parameters['@SalesOrderDetailID'].Value = $detailId
	$persistedTable = [System.Data.DataTable]::new()
	$adapter = [System.Data.SqlClient.SqlDataAdapter]::new($persistedCommand)
	[void]$adapter.Fill($persistedTable)
	$adapter.Dispose()
	$persistedCommand.Dispose()
	if ($persistedTable.Rows.Count -ne 1) { throw 'Expected one post-trigger header/detail/history join row inside the transaction.' }
	$persistedRow = $persistedTable.Rows[0]
	Assert-Equal ([int]$persistedRow.SalesOrderID) $orderId 'Generated SalesOrderID'
	Assert-Equal ([int]$persistedRow.SalesOrderDetailID) $detailId 'Generated SalesOrderDetailID'
	Assert-Equal ([string]$persistedRow.TransactionType) 'S' 'TransactionHistory type'
	Assert-Equal ([int]$persistedRow.Quantity) 2 'TransactionHistory quantity'
	Assert-Equal ([decimal]$persistedRow.ActualCost) ([decimal]1.25) 'TransactionHistory amount'
	Assert-Equal ([decimal]$persistedRow.LineTotal) ([decimal]2.5) 'Computed detail LineTotal'
	Assert-Equal ([decimal]$persistedRow.SubTotal) ([decimal]2.5) 'Trigger-maintained header SubTotal'
	Assert-Equal ([decimal]$persistedRow.TotalDue) ([decimal]2.5) 'Computed header TotalDue'
	Assert-Equal ([int]$persistedRow.RevisionNumber) 1 'Trigger-maintained RevisionNumber'
	$orderNumber = [string]$persistedRow.SalesOrderNumber
} finally {
	if ($transaction) {
		try { $transaction.Rollback() } catch { }
		$transaction.Dispose()
	}
	$writer.Dispose()
}

$countsAfter = Get-CloneCounts $writerConnectionString
$adminCountsAfter = Get-CloneCounts (New-IntegratedConnectionString $testDatabase) $true
$writer = [System.Data.SqlClient.SqlConnection]::new($writerConnectionString)
$writer.Open()
try {
	$residualCommand = $writer.CreateCommand()
	$residualCommand.CommandText = @'
SELECT
    (SELECT COUNT_BIG(*) FROM Sales.SalesOrderHeader WHERE SalesOrderID=@SalesOrderID) AS HeaderRows,
    (SELECT COUNT_BIG(*) FROM Sales.SalesOrderDetail WHERE SalesOrderID=@SalesOrderID AND SalesOrderDetailID=@SalesOrderDetailID) AS DetailRows,
    (SELECT COUNT_BIG(*) FROM Production.TransactionHistory WHERE ReferenceOrderID=@SalesOrderID AND ReferenceOrderLineID=@SalesOrderDetailID) AS HistoryRows;
'@
	[void]$residualCommand.Parameters.Add('@SalesOrderID', [System.Data.SqlDbType]::Int)
	$residualCommand.Parameters['@SalesOrderID'].Value = $orderId
	[void]$residualCommand.Parameters.Add('@SalesOrderDetailID', [System.Data.SqlDbType]::Int)
	$residualCommand.Parameters['@SalesOrderDetailID'].Value = $detailId
	$residualRows = [System.Data.DataTable]::new()
	$adapter = [System.Data.SqlClient.SqlDataAdapter]::new($residualCommand)
	[void]$adapter.Fill($residualRows)
	$adapter.Dispose()
	$residualCommand.Dispose()
	foreach ($column in @('HeaderRows', 'DetailRows', 'HistoryRows')) {
		Assert-Equal ([long]$residualRows.Rows[0][$column]) 0 "$column for generated IDs after rollback"
	}
} finally {
	$writer.Dispose()
}
foreach ($column in @('HeaderCount', 'DetailCount', 'HistoryCount')) {
	Assert-Equal $countsAfter[$column] $countsBefore[$column] "$column after rollback"
}
Assert-Equal $adminCountsAfter.ErrorLogCount $adminCountsBefore.ErrorLogCount 'ErrorLog count after rollback'
foreach ($column in @('HeaderCount', 'DetailCount', 'HistoryCount')) {
	Assert-Equal $adminCountsAfter[$column] $adminCountsBefore[$column] "$column administrator count after rollback"
}

[System.Security.Cryptography.CryptographicOperations]::ZeroMemory([System.Text.Encoding]::Unicode.GetBytes($password))
$password = $null
$writerConnectionString = $null
'AW-014 clone-only test passed.'
"Generated IDs: SalesOrderID=$orderId; SalesOrderDetailID=$detailId; SalesOrderNumber=$orderNumber"
'Inside transaction: LineTotal=2.5000; SubTotal=2.5000; TotalDue=2.5000; RevisionNumber=1; TransactionHistory type=S quantity=2 actualCost=1.2500.'
'After explicit rollback: no header/detail/history rows remained for the generated IDs; table counts, including ErrorLog, were unchanged.'
'Password remained DPAPI CurrentUser encrypted outside the repository; it was not emitted.'