# AdventureWorks

React and ASP.NET Core solution backed by SQL Server AdventureWorks2022.

## Projects

- `src/client` - React and Vite frontend
- `src/server/AdventureWorks.Api` - ASP.NET Core 10 Web API
- `AdventureWorks.slnx` - solution containing the API project
- `backlog.csv` - prioritized work queue and agent ownership

The browser calls the API only. Optional SQL access is server-side and uses a current-user DPAPI-encrypted password file at `%LOCALAPPDATA%\AdventureWorks\test-reader.credential`. Set `ADVENTUREWORKS_READONLY_SQL_CREDENTIAL_FILE` only to override the encrypted file path; never set a plaintext SQL connection string. The API decrypts the password in-process and constructs a fixed connection to `localhost`, database `AdventureWorks2022_Test`, as `AdventureWorksTestReader`. If the file is absent, cannot be decrypted by the current Windows user, or the database identity/permissions do not pass validation, business read routes stay unavailable. Do not put SQL Server credentials in browser code or source control. Keep the supplied `AdventureWorks2022` sample database unchanged; any future write tests must use an isolated verified restore and a separately approved identity.

## Run locally

The authenticated workflow builds React into the API's `wwwroot` and runs only the API on loopback. Install client dependencies once if needed:

```powershell
npm install --prefix src/client
```

Start the API from the repository root (default port 5080):

```powershell
.\scripts\run-local.ps1 -Mode Api -ApiPort 5081
```

The script checks the port, builds the client into the API's `wwwroot`, and launches the loopback API with Development Negotiate explicitly enabled for that process only. Open the printed API URL at `http://localhost:5081`. Verify anonymous health and authenticated UI availability using the current Windows default credentials:

```powershell
.\scripts\run-local.ps1 -Mode Verify -ApiPort 5081
```

`-Mode Client` is an optional unauthenticated Vite-only preview; it has no `/api` proxy and is not the authenticated workflow. Stop the foreground API with `Ctrl+C` in the terminal that started it. The script refuses occupied ports and never terminates listeners.

The authenticated workflow serves the UI and API from one localhost origin; Vite's optional preview is not an authenticated API workflow. The UI can load while business data routes remain unavailable when the local read adapter or its approved permissions are not configured. See the [local runbook](docs/runbooks/development-and-test-safety.md).

## Local demo (full order workflow)

The demo records real orders in the isolated `AdventureWorks2022_Test` clone only. Production, authentication hardening, and security review are out of scope. Prerequisites (one-time, already done on the demo machine):

- The SQL Server default instance on `localhost` hosts `AdventureWorks2022_Test`.
- `AdventureWorksTestReader` has the catalog/customer read grants plus the seven-column order SELECT grant.
- `AdventureWorksTestWriter` has INSERT on the order header and detail tables.
- DPAPI credential files exist at `%LOCALAPPDATA%\AdventureWorks\test-reader.credential` and `test-writer.credential`.
- Clone offer EndDates are extended to 2030-12-31 so volume discounts are eligible.

Start the demo with `.\scripts\run-local.ps1 -Mode Api`, or, if script execution is blocked:

```powershell
npm.cmd run build --prefix src/client
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ADVENTUREWORKS_DEV_AUTH_ENABLED = 'true'
dotnet run --project src/server/AdventureWorks.Api --artifacts-path $env:TEMP\aw-demo-artifacts --urls http://localhost:18080
```

Then open `http://localhost:18080` and run the demo:

1. **Catalog:** search, for example, `Mountain-200`, then **View** a product and **Add priced line to draft**.
2. **Offers:** set the draft quantity to 11–14 and pick **Volume Discount 11 to 14** from the line's Offer list.
3. **Customer and shipping:** use **Choose customer and shipping**, search `A Bike Store`, **Select**, then choose the bill-to address, ship-to address and ship method.
4. **Review and submit:** **Review order**, then **Submit order**. The confirmation shows the recorded `SO…` reference and the server amounts: discounted list price, 7.5% demo tax and $25.00 flat freight.
5. **Look up the order:** **View in Order Lookup** shows the stored order and its lines.

Demo limitations:

- Submission tokens are held in memory, so a restart forgets them.
- Demo orders accumulate in the clone. Restore the clone from its backup to reset it.

## Validate

After installing the client dependencies, run the complete local validation suite from the repository root:

```powershell
.\scripts\validate.ps1
```

For local failure interpretation and the isolated-clone test boundary, see [the development and test safety runbook](docs/runbooks/development-and-test-safety.md).

## Backlog coordination

`backlog.csv` is the shared work queue. The orchestrator keeps items ordered, owned, dependency-aware, and ready before implementation begins. Specialist agents should update task status only for work they have actually completed and add discovered follow-up tasks rather than expanding scope silently.