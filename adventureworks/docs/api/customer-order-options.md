# Customer Order Options API

`GET /api/customers/{customerId}/order-options` accepts a positive customer ID and returns:

- `addresses`: distinct address IDs linked to the selected customer through the union of its non-null `Sales.Customer.PersonID` and `StoreID` business-entity IDs, `Person.BusinessEntityAddress`, `Person.Address`, and `Person.AddressType`.
- `shipMethods`: up to 100 rows from `Purchasing.ShipMethod`, ordered by ID.

Each address includes `addressId`, `addressLine1`, optional `addressLine2`, `city`, optional `postalCode`, and all linked `addressTypes` labels. Address types are labels only; none is filtered or treated as a billing/shipping restriction. This response contains address data needed for selection, but omits person names, contact details, state metadata, and unrelated database columns. Duplicate address IDs are returned once while retaining distinct type labels.

Each ship method includes only `shipMethodId` and `name`. AdventureWorks has no active flag on `ShipMethod`, and this contract does not infer a freight calculation from `ShipBase` or `ShipRate`.

The API returns `400 Bad Request` for a non-positive ID, `404 Not Found` when a guarded lookup confirms the customer does not exist, and a sanitized `503 Service Unavailable` when the configured read adapter, runtime permission guard, or database query fails. A customer with no verifiably linked addresses receives an empty `addresses` list, not an address inferred from an order-header foreign key. Submission must revalidate the customer, selected address links, and ship method; this read response is not a reservation or authorization token.

The route uses parameterized SQL and does not write to AdventureWorks. On each order-options query, the DPAPI-configured adapter requires its existing approved read permissions plus `SELECT` on `Person.BusinessEntityAddress`, `Person.Address`, `Person.AddressType`, and `Purchasing.ShipMethod`. Missing permissions fail closed with `503`; no grants are made by the application. The minimum-permission review and clone-only grant evidence are recorded in [adventureworks-p0-permissions.md](../data/adventureworks-p0-permissions.md). The route is available only through the loopback-only Development identity in the local/test-clone setup; production exposure remains blocked pending AW-036 owner decisions and implementation.