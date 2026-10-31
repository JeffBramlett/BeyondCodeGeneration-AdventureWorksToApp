# AdventureWorks P0 Permission Recommendations

**Status:** The approved read grants remain limited to `AdventureWorks2022_Test` for `AdventureWorksTestReader`. On 2026-10-27, a separate SQL-auth `AdventureWorksTestWriter` was provisioned only in the isolated clone for the explicitly approved insert-and-rollback trigger test. It is a test-only identity, not an approved API/runtime or production writer. The original `AdventureWorks2022` database has no writer mapping or data/schema/permission changes.

**Evidence boundary:** AW-020 documents SQL Server 2022 `AdventureWorks2022` catalog metadata inspected on 2026-10-26. Effective read permissions and deployed order-trigger definitions/dependencies/ownership were inspected on the clone. The reader remains read-only; the test writer has only the reviewed clone grants recorded below. No migration identity or production write path is configured.

## Default security posture

Use default-deny: each database principal receives only the permissions required by its workflow, preferably through narrowly scoped database roles. Keep catalog/customer reads, order submission, and schema deployment in separate identities. Do not use `sysadmin` or `db_owner` identities for any application runtime, including read-only paths. A code-side elevated-principal check is only defense in depth; it does not replace database grants.

Do not grant permissions to `public` or add grants to the supplied AdventureWorks database. The grants recorded below are limited to the approved isolated clone and existing reader user.

## Catalog and customer read identity

For a service that needs both lookup workflows, use one non-elevated runtime principal assigned to separate catalog-reader and customer-reader roles. Keeping grants in separate roles preserves the option to split the identities later. Grant `SELECT` only on the objects directly referenced by the current API `SELECT` plans:

| Role | Directly queried objects | Plan purpose |
| --- | --- | --- |
| `aw_catalog_reader` | `Production.Product`, `Production.ProductSubcategory`, `Production.ProductCategory` | Product search and detail, including category labels; product detail now returns POC `Production.Product.ListPrice` |
| `aw_catalog_reader` | `Sales.SpecialOfferProduct`, `Sales.SpecialOffer` | Offers linked to a product; the plan returns only offer ID and description |
| `aw_customer_reader` | `Sales.Customer`, `Person.Person`, `Sales.Store` | Bounded customer search and unambiguous person/store identity joins |

For example, an authorized DBA could grant object-level `SELECT` on those exact tables to the corresponding database roles. Do not grant database-wide `SELECT`, schema-wide read access, or access to person names, addresses, contact details, prices, or inventory merely because those objects exist in the schema. POC `Production.Product.ListPrice` access is the limited, explicitly approved exception for the authenticated product-detail read; search remains price-free, and this needs no permission beyond the existing table-level SELECT on `Production.Product`.

The order-options query plan is now documented in [customer-order-options.md](../api/customer-order-options.md). Its direct dependencies are listed under the AW-016 review below. Do not infer additional tables from the schema or grant schema-wide access.

## AW-031 API query-plan review

**Decision:** Accept the table-level `SELECT` object list above as the minimum direct-object list for the current product catalog (including linked-offer reads) and customer-search plans. The union is eight tables: `Production.Product`, `Production.ProductSubcategory`, `Production.ProductCategory`, `Sales.SpecialOfferProduct`, `Sales.SpecialOffer`, `Sales.Customer`, `Person.Person`, and `Sales.Store`. Product search/detail joins the first three; the offer plan joins `Sales.SpecialOfferProduct` to `Sales.SpecialOffer`; customer search joins `Sales.Customer` to `Person.Person` and `Sales.Store`. Product detail selects `Production.Product.ListPrice` for the POC; no list-price history join or extra object permission is required. Search DTOs remain price-free. Keep the catalog/customer roles separate as shown; a principal serving both workflows needs their union.

**Guard correction:** `SqlServerPrincipalGuard` requires the approved eight-object `SELECT` baseline, including `Sales.SpecialOfferProduct` and `Sales.SpecialOffer`, and rejects mutation/administration permissions, `sysadmin`, and `db_owner`. It does not reject every additional non-mutating permission. Order-options calls explicitly require their four additional table-level `SELECT` permissions; catalog and customer-search calls do not. Focused offline tests cover the baseline and route-specific requirements. The guard is not authorization to change database permissions; verify the intended principal and inherited/broad permissions only in an approved isolated environment.

## AW-016 order-options permission extension (approved and applied)

The order-options address and ship-method plans directly reference exactly these four additional tables:

| Proposed permission | Query purpose |
| --- | --- |
| `SELECT` on `Person.BusinessEntityAddress` | Verify each address is linked to the selected customer's person and/or store business entity |
| `SELECT` on `Person.Address` | Return the minimal address-selection fields |
| `SELECT` on `Person.AddressType` | Return linked address-type labels without filtering by type |
| `SELECT` on `Purchasing.ShipMethod` | Return bounded ship-method IDs and names only |

The Product Designer confirmed the minimum address-option fields are address ID, address lines, city, postal code, and address-type labels; ship-method options need only ID and name. The API checks these four permissions only on order-options reads, in addition to the existing eight-object baseline. Catalog and customer-search calls do not require them.

**Applied change (2026-10-27):** In one transaction scoped to `AdventureWorks2022_Test`, granted `SELECT` to the existing database user `AdventureWorksTestReader` on exactly `Person.BusinessEntityAddress`, `Person.Address`, `Person.AddressType`, and `Purchasing.ShipMethod`. The target database, principal, and table objects were checked before the grants. No login, role, credential, schema, data, original-database permission, or write permission was changed.

**Validation:** Under the effective `AdventureWorksTestReader` context, all 13 existing baseline permissions (two server, three database, and eight table-level `SELECT`) and the four added table-level `SELECT` permissions are effective. There are no other readable user tables. `INSERT`, `UPDATE`, `DELETE`, `ALTER`, `CONTROL`, `sysadmin`, and `db_owner` are absent; `HAS_DBACCESS('AdventureWorks2022')` returns `0`. The existing non-mutating `EXECUTE` permissions on seven `dbo` database-diagram procedures were observed and left unchanged; no role or permission cleanup was authorized. Both opt-in SQL clone integration tests passed, including customer address and ship-method reads. No query reads `Person.BusinessEntity`, `SalesOrderHeader`, freight values, or other unapproved objects.

## AW-014 restricted test writer (2026-10-27)

After confirming no login/user collision, a separate SQL-auth login and database user named `AdventureWorksTestWriter` were created. The login default database is `AdventureWorks2022_Test`; its only database-user mapping is in that clone. A generated password is stored as current-user DPAPI ciphertext at `%LOCALAPPDATA%\AdventureWorks\test-writer.credential`, with a current-user-only file ACL. The password was not printed, logged, or committed. The reader identity remains unchanged and read-only.

The writer has explicit `CONNECT`, exactly two object-level `INSERT` grants, and exactly seven object-level `SELECT` grants:

| Permission | Objects | Test purpose |
| --- | --- | --- |
| `INSERT` | `Sales.SalesOrderHeader`, `Sales.SalesOrderDetail` | Insert one header and one detail; omit identity/computed columns |
| `SELECT` | `Sales.Customer`, `Person.BusinessEntityAddress`, `Purchasing.ShipMethod`, `Sales.SpecialOfferProduct` | Select valid clone-only references for the test |
| `SELECT` | `Sales.SalesOrderHeader`, `Sales.SalesOrderDetail`, `Production.TransactionHistory` | Verify generated/persisted values and the detail-trigger history row inside the transaction |

Effective-permission inspection confirmed no server/database role membership, `sysadmin`, `db_owner`, `UPDATE`, `DELETE`, `ALTER`, `CONTROL`, or other table mutation rights. The two order-table INSERT rights are the only effective INSERT rights on user tables. There is no user mapping in `AdventureWorks2022`; `HAS_DBACCESS('AdventureWorks2022')` returned 0 and a direct writer connection to that database was denied. The account is only a database test identity; it is not configured in the API and is not approved for production writes.

The clone already grants `EXECUTE` through `public` on seven database-diagram procedures and `dbo.fn_diagramobjects`. These grants were not created or changed for the writer. The scalar function was inspected; it only counts installed diagram objects. They do not confer table mutation rights.

**Transactional verification:** Under the restricted writer, a single transaction inserted one header and one detail for a store-only customer with a linked address, a valid ship method, and the verified `(SpecialOfferID, ProductID) = (1, 680)` pair. Salesperson and territory were null, so their trigger update branches were not entered; the person-customer demographics branch was avoided. Quantity was 2 and test unit price was 1.25. Inside the transaction, generated IDs/number were readable, `LineTotal`, trigger-maintained `SubTotal`, and computed `TotalDue` were each 2.5000, `RevisionNumber` was 1, and the matching `Production.TransactionHistory` row had type `S`, quantity 2, and actual cost 1.2500. The transaction was explicitly rolled back. Header, detail, history, and `dbo.ErrorLog` row counts matched their pre-test values afterward. SQL Server identity counters are not rolled back, so generated identity values may be skipped even though no rows persisted.

The trigger CATCH/error path was not deliberately induced: it can roll back and then write `dbo.ErrorLog` outside the order transaction. If a future narrow-grant test fails, do not broaden permissions automatically; review the exact error and trigger/module chain separately. The reusable clone-pinned procedure is `scripts/aw014-test-writer.ps1`; it reuses the protected credential only when the exact login/user/file state is present and changes no grants on that path.

## Authorized POC API Writer Boundary (2026-10-28)

The Project Owner authorized development of a **distinct local POC API writer** restricted to `AdventureWorks2022_Test`. This does not authorize access to or writes against the reference `AdventureWorks2022` database or any production environment.

- `AdventureWorksTestWriter` remains an AW-014 rollback-test identity only. Do not configure it as the API runtime principal or reuse its credential file.
- Code reserves the distinct runtime login name `AdventureWorksPocApiWriter`; no SQL login/user or POC credential has been provisioned. Provision a separate POC runtime identity and protected credential only after the Database Engineer verifies target identity/collisions and derives exact least-privilege grants from the final parameterized validation/insert/reload SQL. Do not copy AW-014's grants wholesale; do not grant database-wide/schema-wide rights, `db_datawriter`, or elevated roles.
- Pin and verify server/database/login on each connection. Fail closed unless the target is exactly the local `AdventureWorks2022_Test` clone and the principal matches the reviewed POC runtime identity. Verify there is no user mapping or access to `AdventureWorks2022`.
- The code-only `PocWriterBoundaryConfiguration` verifies a separate DPAPI credential file without retaining its secret or constructing a connection string. `PocWriterPrincipalGuard` requires the pinned local clone/login/user, denies original-database access and elevated roles, and compares the effective permissions to a reviewed set supplied by a future caller. Neither abstraction opens a connection or is registered in DI; there is no write method or route.
- The authorization permits implementing the isolated POC path. Durable idempotency storage and recovery, DBA/platform topology approval, trigger CATCH/error behavior, production authentication, and deployment approvals remain unresolved. The initial persisted status placeholder is resolved for the bounded clone only.
- Keep committed route/DML disabled until those prerequisites are resolved, the principal/grants are reviewed, and the intended transaction/rollback behavior is verified under that distinct POC runtime principal. AW-014 success-path evidence does not substitute for this validation.

No POC runtime writer has been provisioned and no grants have been changed by recording this authorization.

### AW-014 deployed trigger and ownership review (2026-10-27)

**Verified facts:** Read-only metadata inspection was run against `AdventureWorks2022_Test`. Both `Sales.iduSalesOrderDetail` and `Sales.uSalesOrderHeader` are enabled, AFTER triggers; their module `execute_as_principal_id` is `NULL` (caller context). The `SalesOrderDetail` trigger handles INSERT/DELETE/UPDATE and directly inserts `Production.TransactionHistory`, updates `Person.Person`, and updates `Sales.SalesOrderHeader.SubTotal`. That header update fires `Sales.uSalesOrderHeader`, which updates `RevisionNumber`; when `SubTotal` is updated it also reads accounting dates through `dbo.ufnGetAccountingStartDate` / `dbo.ufnGetAccountingEndDate` and updates `Sales.SalesPerson.SalesYTD` and `Sales.SalesTerritory.SalesYTD`. `Person.iuPerson` is an enabled nested trigger on `Person.Person` and maintains its demographics XML. Both order triggers call `dbo.uspPrintError` and `dbo.uspLogError` in their CATCH paths; the latter can insert `dbo.ErrorLog` after its rollback logic. The inspected module definitions contain no dynamic-SQL execution markers and catalog dependencies show no cross-database or cross-server reference.

The trigger schemas and every inspected referenced table, function, and procedure are owned through schemas whose owner is `dbo`; the trigger parent tables and trigger schemas have that same owner. The database owner is the local Windows administrator, while database `DB_CHAINING` and `TRUSTWORTHY` are both off. The order triggers and called modules have no explicit `EXECUTE AS` principal. No DML, DDL, login change, permission change, or original-database access was performed for this review.

**Permission-chain assessment:** Metadata suggested same-database, same-owner chaining could allow the order triggers and nested same-owner modules to run without direct mutation grants on trigger targets. The restricted `AdventureWorksTestWriter` test now verifies the successful path at runtime: header/detail INSERT grants caused the detail trigger to insert transaction history and maintain subtotal/revision without direct trigger-target grants. This does not verify the trigger CATCH/error path or authorize an API/production write path.

**Future API writer boundary:** The clone test identity uses only the seven SELECTs and two INSERTs listed above. Its reference reads are test-specific, not an approved API query plan. For any future API writer, compare the final parameterized validation/reload SQL to the needed object list and add only separately reviewed object-level SELECT grants. Candidate objects remain conditional on that SQL: customer/address ownership, offer-product eligibility, shipping-method validation, and direct post-insert reload. Do not copy the read API's full permission set automatically.

Add `SELECT` on `Sales.SalesOrderHeader` and `Sales.SalesOrderDetail` only if the final API reloads trigger-updated/persisted values by direct table queries after insert. Those grants are table-wide, not order-row-scoped, and are not currently effective for the reader. Consider an explicitly reviewed stored procedure or other narrow module boundary for reload instead of broad table visibility; creating such a module would itself require separate approval and schema ownership review. `OUTPUT`-returned pre-trigger values alone do not prove post-trigger persisted totals.

An order submission that inserts a header and one or more details depends on a single transaction covering the header insert, generated order-ID use, all detail inserts, and trigger effects. Commit only after every step succeeds; a failure must leave neither a partial order nor partial trigger-maintained state. The successful trigger path and explicit rollback are verified for the isolated test writer; trigger CATCH/error handling and API transaction ownership/error mapping remain unverified. The current API has no order-write plan, and the test writer does not enable or authorize an API or production write path.

## Migration and administration identities

Do not run migrations as the application runtime identity. The supplied AdventureWorks schema is a reference schema; application migrations should normally target a separately owned application schema or database, not alter AdventureWorks tables. For an explicitly approved isolated database and schema, use a deployment principal scoped to the owned schema and grant only the required DDL capabilities (for example, `CREATE TABLE` at database scope plus `ALTER` on the owned schema, and `REFERENCES` only when creating relevant constraints). Scope metadata visibility as needed. Exact migration permissions depend on the migration tool's emitted SQL and must be reviewed before use.

Database creation, restore, ownership, and recovery are DBA-controlled operations using a separate administrative identity. Do not give the migration principal `sysadmin` or routine `db_owner` membership, and never reuse the DBA identity for application traffic. Do not create an AdventureWorks schema migration or change any permissions without explicit target/change approval and a verified recovery plan.

## Validation checklist

- Confirm the server, database, schema version, and intended principal before any future permission work; the AW-016 grants above are already applied only to the approved clone.
- Compare the deployed query text with the plans cited above and enumerate every referenced object, including views, functions, procedures, and dynamic SQL if introduced.
- Trigger definitions, immediate/nested triggers, dependencies, module contexts, and same-owner schema chain were inspected on the clone. A restricted insert/trigger/rollback test now verifies generated and post-trigger values under `AdventureWorksTestWriter`. Still compare any final API validation/reload SQL to its own conditional SELECT needs; this test identity is not an API runtime identity.
- `AdventureWorksTestReader` remains read-only and denied `INSERT`/`SELECT` on both order tables. `AdventureWorksTestWriter` is clone-only and has exactly two order-table INSERTs plus the seven test-specific SELECTs documented above; it is not `sysadmin`/`db_owner` and cannot connect to/read the original database. AW-014 is Completed for the explicitly approved clone-only success-path and rollback test. This test identity is not an API/runtime identity; API and production write readiness remain separate gates.
- The trigger CATCH/error path was not induced because its `uspLogError` call may persist an ErrorLog row after rollback. Do not add grants automatically if such a path fails; review evidence and seek separate approval for any permission change. Remove this clone-only test identity when it is no longer needed, through explicitly reviewed clone-only cleanup.
- Verify generated values, trigger effects, atomic rollback, and any migration against an isolated target; never use the shared reference database for write tests.
- Keep the test writer separate from the API reader and production identities; record approved grants and the change owner. Remove the test identity when no longer needed through an explicitly reviewed clone-only cleanup.