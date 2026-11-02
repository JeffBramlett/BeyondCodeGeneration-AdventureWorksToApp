# AdventureWorks P0 Mapping

Verified against the read-only catalog metadata of SQL Server 2022 `AdventureWorks2022` on 2026-10-26. This is a schema reference, not an application eligibility or pricing policy. No customer, person, or address data is reproduced here. The API project currently has no ORM or database configuration.

## Product and offers

| Source | P0 fields | Key and relationships |
| --- | --- | --- |
| `Production.Product` | `ProductID`, `Name`, `ProductNumber`, `ListPrice`, `ProductSubcategoryID` | `ProductID` is an identity PK. Nullable `ProductSubcategoryID` references `Production.ProductSubcategory`. |
| `Production.ProductListPriceHistory` | `ProductID`, `StartDate`, `EndDate`, `ListPrice` | PK `(ProductID, StartDate)`; `ProductID` references `Production.Product`. `EndDate` is nullable. |
| `Production.ProductSubcategory` / `Production.ProductCategory` | `ProductSubcategoryID`, `Name`, `ProductCategoryID`; category `ProductCategoryID`, `Name` | Subcategory ID and category ID are identity PKs; subcategory `ProductCategoryID` references category. Product's subcategory FK is nullable. |
| `Sales.SpecialOffer` | `SpecialOfferID`, `Description`, `DiscountPct`, `StartDate`, `EndDate`, `MinQty`, `MaxQty` | `SpecialOfferID` is an identity PK. `MaxQty` is nullable. |
| `Sales.SpecialOfferProduct` | `SpecialOfferID`, `ProductID` | Composite PK `(SpecialOfferID, ProductID)`; `SpecialOfferID` references `Sales.SpecialOffer` and `ProductID` references `Production.Product`. It is the composite target of the order-detail offer/product FK. |

The offer dates and quantity bounds are present in the schema. Their interpretation for current eligibility, quantity checks, display, or price calculation is application policy and is not established by the schema inspection. Do not treat `Product.ListPrice` or list-price history as a finalized order price without an explicit policy.

### Test-clone offer metadata verification (2026-10-27)

Read-only inspection of `AdventureWorks2022_Test` confirmed `SpecialOffer.StartDate` and `EndDate` are SQL Server `datetime` (precision 23, scale 3), `MinQty` is `int NOT NULL` with default 0, `MaxQty` is nullable `int`, and `DiscountPct` is `smallmoney NOT NULL` with default 0. SQL `datetime` stores no timezone identifier, so UTC/local interpretation cannot be derived from the type.

The 16 seeded offers include `SpecialOfferID = 1`, described as `No Discount`, with `DiscountPct = 0`, `MinQty = 0`, and `MaxQty = NULL`. The Project Owner conditionally approved it as an internal persistence-only marker for a no-promotion line, including outside its promotional date window. This does not make it an eligible/selectable offer or establish price policy. The seeded offers have stored date windows ending in 2014; row values do not establish an ongoing eligibility policy.

### AW-034 reader-only current eligibility check (2026-10-27)

The opt-in AW-034 integration check used only the protected `AdventureWorksTestReader` connection to `AdventureWorks2022_Test`. At `2026-10-27T18:37:43Z`, all 16 offers had `EndDate` no later than `2014-11-30`; zero were date-eligible under the approved UTC instant and half-open interval, and zero had a date-eligible interval intersecting the positive SQL `smallint` quantity range 1–32767. Across all 504 `Production.Product` rows, zero products had an eligible linked offer pair and zero eligible linked pairs were found.

Offer 1 was `No Discount`, `DiscountPct = 0.0000`, `StartDate = 2011-05-01 00:00:00`, `EndDate = 2014-11-30 00:00:00`, `MinQty = 0`, `MaxQty = NULL`; 295 links existed, all to finished goods (0 to non-finished products). It was not date-eligible at the check instant. Its zero discount and product links do not override the approved date rule or establish a no-offer business meaning.

Under this reader, `StartDate` and `EndDate` are `datetime`, precision 3, non-null. Their visible extended-property descriptions are only “Discount start date” and “Discount end date”; neither has a visible default constraint. SQL `datetime` has no timezone identifier, and these metadata facts provide no historical UTC/local convention. Stored sample dates are not evidence of timezone semantics. The reader did not expose `SalesOrderDetail` column or foreign-key metadata; a separately authorized read-only test-writer query verified the current FK as described below.

No DML, DDL, grants, credential output, or original-database access occurred. On the observed date, no product has a currently date- and quantity-valid linked offer pair. The separately approved Offer 1 persistence-marker exception is independent of promotional eligibility and is available only for products with a verified link and pair FK.

No DML/DDL was performed. These are schema and clone-data facts, not approval of business rules. The Project Owner separately approved the inclusive/exclusive date boundaries, UTC application mapping, positive quantity handling, null `MaxQty` behavior, optional single offer/no stacking, and renewed review; see [AW-034 Offer Eligibility Decisions](../product/p0-offer-eligibility-decision.md). This data still does not establish a valid no-offer order-detail representation, orderability, or price calculation.

### AW-038 SpecialOfferID 1 product-link coverage (2026-10-27)

Using the protected `AdventureWorksTestReader` connection, a read-only query on `AdventureWorks2022_Test` left-joined every `Production.Product` row to `Sales.SpecialOfferProduct` for `SpecialOfferID = 1`, grouping by finished-goods flag and product category. Offer 1 was returned as `No Discount`.

| Product group | Products | Linked to offer 1 | Not linked |
| --- | ---: | ---: | ---: |
| All products | 504 | 295 | 209 |
| Finished goods | 295 | 295 | 0 |
| Non-finished | 209 | 0 | 209 |
| Accessories (finished) | 29 | 29 | 0 |
| Bikes (finished) | 97 | 97 | 0 |
| Clothing (finished) | 35 | 35 | 0 |
| Components (finished) | 134 | 134 | 0 |
| Uncategorized (non-finished) | 209 | 0 | 209 |

Representative linked product IDs by finished-goods category: Accessories `7007`, Bikes `749`, Clothing `709`, Components `680`. Representative unlinked non-finished product ID: `1`. No customer/person/address data was queried or returned. No DML/DDL was performed. Link coverage alone does not establish eligibility or policy.

### AW-034 Offer 1 pair-FK verification (2026-10-27)

After explicit Project Owner authorization for a single read-only check, the Database Engineer used the existing clone-only test-writer identity to query only the test clone's database identity, foreign-key metadata, and aggregate offer/product pair counts. `FK_SalesOrderDetail_SpecialOfferProduct_SpecialOfferIDProductID` maps `(SalesOrderDetail.SpecialOfferID, ProductID)` to `(SpecialOfferProduct.SpecialOfferID, ProductID)` and is enabled (`is_disabled = 0`) and trusted (`is_not_trusted = 0`). All 295 existing Offer 1 product links match the referenced composite key; zero linked pairs are unmatched. Combined with the per-product link query, this verifies the marker's pair-FK requirement for 295 products only. The other 209 products have no Offer 1 pair and cannot use this marker under the current schema. This check did not validate full order submission, other line/header constraints, current offer eligibility, price, or trigger behavior. It ran SELECT only; no DML/DDL, grants, credential output, customer data, or original-database access occurred.

## Customer and addresses

| Source | P0 fields | Key and relationships |
| --- | --- | --- |
| `Sales.Customer` | `CustomerID`, `PersonID`, `StoreID` | `CustomerID` is an identity PK. Nullable `PersonID` references `Person.Person.BusinessEntityID`; nullable `StoreID` references `Sales.Store.BusinessEntityID`. |
| `Person.Person` | `BusinessEntityID` | Entity key referenced by `Customer.PersonID`. Do not load or expose person details unless separately required and approved. |
| `Sales.Store` | `BusinessEntityID`, `Name` | `BusinessEntityID` is the PK and references `Person.BusinessEntity.BusinessEntityID`. |
| `Person.BusinessEntityAddress` | `BusinessEntityID`, `AddressID`, `AddressTypeID` | Composite PK `(BusinessEntityID, AddressID, AddressTypeID)`; separate FKs to `Person.BusinessEntity`, `Person.Address`, and `Person.AddressType`. |
| `Person.Address` | `AddressID`, `AddressLine1`, `AddressLine2`, `City`, `StateProvinceID`, `PostalCode` | `AddressID` is an identity PK; `StateProvinceID` references `Person.StateProvince`. `AddressLine2` is nullable. |
| `Person.AddressType` | `AddressTypeID`, `Name` | `AddressTypeID` is an identity PK. |

`Sales.SalesOrderHeader.BillToAddressID` and `ShipToAddressID` each reference `Person.Address.AddressID`. Those FKs establish that the address row exists; they do **not** establish that it belongs to the selected customer, is connected through `BusinessEntityAddress`, or is eligible for billing/shipping. Enforce any ownership and address-type rules in application policy using the appropriate business-entity relationship; do not infer them from the header FKs.

## Shipping and orders

| Source | P0 fields | Key and relationships |
| --- | --- | --- |
| `Purchasing.ShipMethod` | `ShipMethodID`, `Name`, `ShipBase`, `ShipRate` | `ShipMethodID` is an identity PK. Referenced by `Sales.SalesOrderHeader.ShipMethodID`. |
| `Sales.SalesOrderHeader` | `SalesOrderID`, `OrderDate`, `DueDate`, `ShipDate`, `Status`, `CustomerID`, `BillToAddressID`, `ShipToAddressID`, `ShipMethodID`, `SubTotal`, `TaxAmt`, `Freight`, `TotalDue` | `SalesOrderID` is an identity PK. FKs to Customer, two Address rows, and ShipMethod. `SalesOrderNumber` is also identity-generated; `TotalDue` is computed. `ShipDate` is nullable. |
| `Sales.SalesOrderDetail` | `SalesOrderID`, `SalesOrderDetailID`, `OrderQty`, `ProductID`, `SpecialOfferID`, `UnitPrice`, `UnitPriceDiscount`, `LineTotal` | Composite PK `(SalesOrderID, SalesOrderDetailID)`; detail ID is identity-generated. FKs to header and the composite `(SpecialOfferID, ProductID)` key in `Sales.SpecialOfferProduct`. `LineTotal` is computed. |

The catalog also reports checks for nonnegative order quantity, unit price and discount, nonnegative header monetary amounts, `DueDate >= OrderDate`, `ShipDate >= OrderDate` when present, and `SpecialOffer.EndDate >= StartDate`. These checks are database constraints, not a complete order-validation contract.

## Generated values and triggers

Catalog metadata reports enabled DML triggers `Sales.uSalesOrderHeader` on `SalesOrderHeader` and `Sales.iduSalesOrderDetail` on `SalesOrderDetail`. This inspection established trigger presence and enabled state, not their bodies or side effects; review and test those effects against an isolated AdventureWorks restore before implementing order writes.

Treat identity and computed columns as database-generated and do not send values for them on insert. When EF Core with the SQL Server provider is introduced, configure the affected table mappings for their triggers (`HasTrigger(...)`, or the equivalent SQL-output configuration supported by the chosen EF Core version). SQL Server `OUTPUT`-based write generation must be compatible with tables that have enabled triggers. Verify generated values and trigger effects with focused integration tests on an isolated restore; never run write tests against the shared sample database.

## Verification boundary

Catalog metadata and the AW-038 product-ID/category aggregates were queried. No customer/person/address data was queried, no DML or DDL was run, and no application mappings or database configuration were changed. This guide records structural and clone-data facts; policy decisions, trigger effects, and transaction behavior remain to be confirmed before write-path implementation.