# AdventureWorks Financial Field Research

**Scope:** Read-only inspection of the local `AdventureWorks2022_Test` database on 2026-10-27 for AW-033. The schema and values below are structural and seeded test-clone evidence, not product/accounting policy or an approved financial authority.

**Safety:** The existing `AdventureWorksTestReader` connection was confirmed to target this clone, but its grants do not permit metadata or SELECT access to the order and tax-rate tables. The requested catalog/formula review therefore used an existing Windows-integrated SQL session explicitly opened to `AdventureWorks2022_Test`. Both sessions stayed on that database. Queries selected catalog metadata, ship-method values, geographic tax-rate values, currency-rate values, and aggregates only; no customer IDs, names, contact details, addresses, or other customer-identifying values were selected or returned. No database DML or DDL was run, and the original `AdventureWorks2022` database was not accessed.

## Verified Schema

| Object | Relevant fields and structure | Evidence and limits |
| --- | --- | --- |
| `Sales.SalesTaxRate` | `SalesTaxRateID` PK; `StateProvinceID` and `TaxType` are non-null; `TaxType` is `tinyint`, `TaxRate` is `smallmoney NOT NULL DEFAULT (0.00)`, and `ModifiedDate` defaults to `getdate()`. Unique key `(StateProvinceID, TaxType)` | FK `StateProvinceID` -> `Person.StateProvince.StateProvinceID`; check constraint permits `TaxType` 1 through 3. `TaxType` has no lookup-table FK. There are no effective-from/effective-to columns; `ModifiedDate` is not a validity interval. |
| `Person.Address` / `Person.StateProvince` / `Sales.SalesTaxRate` | `Address.StateProvinceID` references `StateProvince.StateProvinceID`; `SalesTaxRate.StateProvinceID` references the same state/province key | A schema join path exists from `SalesOrderHeader.ShipToAddressID` -> `Address.AddressID` -> `Address.StateProvinceID` -> `StateProvince.StateProvinceID` -> `SalesTaxRate.StateProvinceID`. This is a structural join only: there is no FK from an order header to a tax-rate row or `TaxType`, and no schema rule declares ship-to jurisdiction authoritative. `StateProvince.TerritoryID` separately references `Sales.SalesTerritory`; that does not make tax rates territory-level. |
| `Purchasing.ShipMethod` | `ShipMethodID`, `Name`, `ShipBase`, `ShipRate` (`money NOT NULL`, each defaulting to `0.00`) | FK from `Sales.SalesOrderHeader.ShipMethodID`. Check constraints require both values to be greater than zero, so the defaults alone do not satisfy the constraints. There is no computed column or trigger formula applying these values to order freight. |
| `Sales.SalesOrderHeader` | `SubTotal`, `TaxAmt`, and `Freight` are `money NOT NULL DEFAULT (0.00)`; `CurrencyRateID` is nullable; `ShipMethodID` is non-null and has no default; `TotalDue` is computed | `TotalDue` is computed as `ISNULL((SubTotal + TaxAmt) + Freight, 0)`. Header checks allow zero but reject negative subtotal, tax, and freight. `CurrencyRateID` references `Sales.CurrencyRate`; `ShipMethodID` references `Purchasing.ShipMethod`. Defaults and checks do not define how tax or freight is calculated. |
| `Sales.SalesOrderDetail` | Includes `OrderQty`, `UnitPrice`, `UnitPriceDiscount`, computed `LineTotal`, `ProductID`, and `SpecialOfferID` | `LineTotal` is computed as `ISNULL((UnitPrice * (1.0 - UnitPriceDiscount)) * OrderQty, 0.0)`. The offer and product fields reference the composite `(SpecialOfferID, ProductID)` key in `Sales.SpecialOfferProduct`; this does not link unit price to product list-price history. |
| `Sales.Currency` / `Sales.CurrencyRate` | `CurrencyRateDate` (`datetime`), `FromCurrencyCode`, `ToCurrencyCode`, `AverageRate`, and `EndOfDayRate` (`money NOT NULL`); unique key `(CurrencyRateDate, FromCurrencyCode, ToCurrencyCode)` | Both currency-code fields reference `Sales.Currency.CurrencyCode`; the header's nullable `CurrencyRateID` references a rate row. The date is a dated observation, not an effective-date interval. No inspected computed column or trigger performs currency conversion, and neither currency table defines tax or freight. |
| `Production.Product` / `Production.ProductListPriceHistory` | `Product.ListPrice` is `money NOT NULL` (precision 19, scale 4); history has `ProductID`, `StartDate`, nullable `EndDate`, and `ListPrice` | History `ProductID` references `Product`. Checks require positive history prices and `EndDate >= StartDate` when present. There is no FK from order-detail `UnitPrice` to either list-price source. |

## 2026-10-28 Read-Identity Metadata Follow-up

Using the existing DPAPI-protected `AdventureWorksTestReader` connection, the connection target was `localhost` / `AdventureWorks2022_Test`. SQL Server reported `SERVERPROPERTY('ServerName') = jeffs_dev_pc`; `ORIGINAL_LOGIN()`, `SUSER_SNAME()`, and `USER_NAME()` were all `AdventureWorksTestReader`. No credential value was output.

The requested catalog query exposed only `Production.Product.ListPrice`, verified as `money`, precision 19, scale 4, not nullable, and not computed. The reader has object-level `SELECT` on `Production.Product` but not `VIEW DEFINITION`; it has neither `SELECT` nor `VIEW DEFINITION` on `Sales.SalesOrderDetail` or `Sales.SalesOrderHeader`. Their requested columns were absent from the reader-visible catalog metadata, so this pass does not independently verify their exact types, precision, scale, nullability, or computed definitions. Existing structural/formula notes above derive from the separately documented prior clone metadata inspection and are not a fresh verification under this reader.

No permissions were changed or requested. Do not broaden grants to complete this pass; obtain a separately approved read-only metadata path if fresh verification of the order columns is required. The original `AdventureWorks2022` database was not accessed.

## 2026-10-28 Windows-Integrated Monetary Metadata Verification

At `2026-10-28T15:39:26.3112877Z`, a metadata-only query connected to `localhost` with the existing Windows-integrated context and explicitly selected `AdventureWorks2022_Test`. Before querying catalog metadata, SQL Server reported `SERVERPROPERTY('ServerName') = jeffs_dev_pc`, `DB_NAME() = AdventureWorks2022_Test`, `ORIGINAL_LOGIN() = MicrosoftAccount\jeff.j.bramlett@outlook.com`, `SUSER_SNAME() = jeffs_dev_pc\jeffj`, and `USER_NAME() = dbo`. The query inspected only `sys.schemas`, `sys.tables`, `sys.columns`, `sys.types`, and `sys.computed_columns` for the requested fields. All requested metadata was visible. No credential was output; no DML, DDL, grants, or login changes were performed, and `AdventureWorks2022` was not accessed.

| Object | Column | SQL type | Precision | Scale | Nullable | Computed | Computed definition |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `Sales.SalesOrderDetail` | `UnitPrice` | `money` | 19 | 4 | No | No | n/a |
| `Sales.SalesOrderDetail` | `UnitPriceDiscount` | `money` | 19 | 4 | No | No | n/a |
| `Sales.SalesOrderDetail` | `LineTotal` | `numeric` | 38 | 6 | No | Yes | `(isnull(([UnitPrice]*((1.0)-[UnitPriceDiscount]))*[OrderQty],(0.0)))` |
| `Sales.SalesOrderHeader` | `SubTotal` | `money` | 19 | 4 | No | No | n/a |
| `Sales.SalesOrderHeader` | `TaxAmt` | `money` | 19 | 4 | No | No | n/a |
| `Sales.SalesOrderHeader` | `Freight` | `money` | 19 | 4 | No | No | n/a |
| `Sales.SalesOrderHeader` | `TotalDue` | `money` | 19 | 4 | No | Yes | `(isnull(([SubTotal]+[TaxAmt])+[Freight],(0)))` |
| `Production.Product` | `ListPrice` | `money` | 19 | 4 | No | No | n/a |

This verifies the clone's catalog definitions at the stated time, including the computed expressions; it does not inspect monetary row values or establish pricing, tax, freight, currency, or rounding policy. It supersedes the earlier read-identity follow-up's unresolved metadata visibility for these columns. The earlier reader-only inspection remains accurate for that principal and no permissions were broadened.

Other relevant order constraints include positive `OrderQty`, nonnegative `UnitPrice` and `UnitPriceDiscount`, and nonnegative header `SubTotal`, `TaxAmt`, and `Freight`. These are integrity bounds, not taxability, pricing, or freight policies. `SalesTaxRate.TaxRate` is stored independently by state/province and type; no order-header field or FK selects a rate, combines types, or records an effective period.

## Triggers, Views, and Procedures

The clone has enabled AFTER triggers `Sales.iduSalesOrderDetail` on `Sales.SalesOrderDetail` and `Sales.uSalesOrderHeader` on `Sales.SalesOrderHeader`. The detail trigger recomputes the affected header `SubTotal` as the sum of its detail `LineTotal` values after detail changes. The header trigger updates `RevisionNumber`; when `SubTotal` changes it also maintains sales-person/territory year-to-date aggregates using accounting-date functions. It does not calculate tax or freight.

The database contains 60 non-null SQL-module definitions. A read-only text scan of all definitions found no mention of tax, freight, `ShipBase`, `ShipRate`, `TaxRate`, or `TaxAmt`; a name-and-definition scan found no matching trigger, stored procedure, or view. No procedure or trigger calculates `TaxAmt` or `Freight`, and no formula derives either component from rate or ship-method inputs. `TotalDue` is the sole relevant computed header formula: it adds the already-stored `SubTotal`, `TaxAmt`, and `Freight`; it does not calculate either component. `Sales.iduSalesOrderDetail` maintains only subtotal from line totals. Text scans cannot detect opaque/dynamically assembled business policy outside literal module text, so this is not proof of an external authority or policy.

## Seeded Clone Data Observations

These observations describe only the contents queried on 2026-10-27. They must not be used as current market/tax data or approved order rules.

- `Sales.SalesTaxRate` contains 29 rows spanning 26 state/provinces and 10 sales territories; the stored rates range from 5.0000 to 19.6000. The uniqueness key allows multiple `TaxType` values for one state/province. These seeded rows do not establish which jurisdiction applies, how types combine, taxable bases, exemptions, rounding, or present-day rates.
- The five seeded ship methods have `ShipBase` values from 3.9500 to 29.9500 and `ShipRate` values from 0.9900 to 2.9900. These aggregates do not document how freight should be calculated from an order, quantity, weight, destination, or method. The clone's 31,465 order headers had no zero `SubTotal`, `TaxAmt`, or `Freight` values, despite schema defaults/checks allowing zero for those header amounts; sample absence does not decide whether zero or omission is allowed by policy.
- `Sales.CurrencyRate` contains 13,532 rows across 14 currency pairs, dated from 2011-05-31 through 2014-05-31. The stored average-rate range is 0.6046 to 1500.0000, and end-of-day range is 0.6041 to 1499.9500. These are historical sample values, not a current conversion source or an approved rate-selection/rounding rule.
- The clone contains 31,465 order headers: 17,489 have no `CurrencyRateID` and 13,976 do. At a 0.005 comparison tolerance, no sampled header violated `TotalDue = SubTotal + TaxAmt + Freight`; no detail violated the computed line-total expression; and no header `SubTotal` differed from the sum of its details. This is evidence consistent with the stored computed-column/trigger behavior, not an instruction to reproduce it as the application's accounting policy.
- `Production.ProductListPriceHistory` contains 395 rows, with `StartDate` values from 2011-05-31 through 2013-05-30. The 504 products have stored `ListPrice` values from 0.0000 to 3578.2700. These fields and `SalesOrderDetail.UnitPrice` are candidate price data only; the schema does not establish a current quote, an offer price, currency treatment, or the price to use for a new order.

## AW-033 Policy Boundary

AdventureWorks provides candidate tax-rate, ship-method, currency-rate, and price fields, plus database behavior for order subtotal, line total, and total due. It does not establish that the seeded values are current or authoritative, nor supply approved jurisdiction selection, taxable-base and tax-type composition rules, freight formula, currency-rate source/date/direction/rounding, price/offer rules, or monetary rounding policy. The database's check constraints and observed sample formulas are not approval of those rules.

Finance/Controller approval is still required to identify authoritative tax and freight sources, calculation and rounding rules, and permitted zero/omitted cases. Keep financially final production writes blocked until those decisions are recorded. AW-014's separate isolated write-test gate and AW-028's least-privilege implementation gate also remain applicable.