# Order read API (AW-007)

The order endpoints are read-only and available to the authenticated local
Development audience. Development authentication is enabled only with the
existing loopback-listener guard. The SQL adapter refuses targets other than
`AdventureWorks2022_Test` and the configured test-reader login.

## Search

`GET /api/orders?orderReference={reference}&page={page}&pageSize={pageSize}`

or

`GET /api/orders?fromDate={yyyy-MM-dd}&toDate={yyyy-MM-dd}&page={page}&pageSize={pageSize}`

Supply exactly one search mode. Reference search trims outer whitespace, then
performs an exact `SalesOrderNumber` match (case behavior follows the database's
case-insensitive collation); the normalized reference is limited to 25
characters. Date search requires both dates and includes every calendar date
from `fromDate` through `toDate`. The maximum span is 31 inclusive calendar
days. SQL uses a half-open datetime interval ending at midnight after `toDate`.

Order pages default to 25 and allow at most 100 results. Pages are ordered by
stored `OrderDate`, then internal `SalesOrderID` for deterministic ties. A
successful response contains `items`, `page`, `pageSize`, and `hasMore`; each
item contains only `orderReference` and the stored `orderDate`. The API maps the
verified database `SalesOrderNumber` column to the client-facing
`orderReference` property.

## Order details

`GET /api/orders/{orderReference}?linePage={linePage}&linePageSize={linePageSize}`

The route reference is trimmed and matched exactly. Detail pages default to 50
lines and allow at most 100. Lines are ordered by internal
`SalesOrderDetailID`. The response contains `orderReference`, the stored
`orderDate`, `lines`, line-page metadata, and `hasMore`; each line contains only
`productId` and `orderQty`. The API maps the verified database `ProductID`
column to the client-facing `productId` property. `SalesOrderID` and
`SalesOrderDetailID` are internal join/order/paging keys and are not response
fields.

`OrderDate` is returned as the database's stored `datetime`, without timezone
conversion. Responses intentionally omit status, customer, address, offer,
price, tax, freight, and other financial data. No order write route is exposed.

Invalid or incomplete search criteria and out-of-range paging return 400.
Unknown order references return 404. An unconfigured adapter or failed
permission check returns a sanitized 503.

## Permission and integration boundary

The adapter verifies the effective existing reader permissions and requires
exact column-level SELECT access to header columns `SalesOrderID`,
`SalesOrderNumber`, and `OrderDate`, and detail columns `SalesOrderID`,
`SalesOrderDetailID`, `ProductID`, and `OrderQty`. It rejects database-, Sales
schema-, or order-object-wide SELECT grants and any additional SELECT column on
either order table. A missing or broader permission causes the order endpoints
to fail closed with 503.

The implementation and tests are offline. No current column grants have been
verified, and this work does not grant permissions or connect to SQL Server.
The Project Owner approved the exact seven-column grant for this test clone;
separate Security approval remains outstanding. The grant has not been applied.
