# Development and Test Safety Runbook

This runbook covers local React/API checks and the boundary around the isolated-clone AW-014 writer procedure. It does not authorize production access, writes, permissions, or database recovery. Keep `AdventureWorks2022` unchanged; application reads are pinned to `AdventureWorks2022_Test`.

## Routine Local Development

The authenticated local workflow is same-origin: React is built into the API's `wwwroot`, and the API serves the UI and `/api` from one loopback origin. It does not start Vite or use a Vite `/api` proxy. Install client dependencies once if needed:

```powershell
npm install --prefix src/client
```

Choose an unused API port from 1–65535; the default is 5080. The script refuses an occupied port with owner details and never terminates listeners. Do not stop a pre-existing or unidentified process to make room. From the repository root, start the sole foreground service:

```powershell
.\scripts\run-local.ps1 -Mode Api -ApiPort 5081
```

The script checks the port, builds the client into `src/server/AdventureWorks.Api/wwwroot`, then launches only the API at `http://localhost:5081`. The API command scopes `ASPNETCORE_ENVIRONMENT=Development` and `ADVENTUREWORKS_DEV_AUTH_ENABLED=true` to that foreground process; Development Negotiate remains opt-in and the existing startup/runtime guard requires loopback listeners. Prior PowerShell environment values are restored when the process exits. Open the printed API URL (`http://localhost:5081`) in the browser; do not open the Vite URL for the authenticated workflow.

With the API running, use another PowerShell terminal for focused local verification:

```powershell
.\scripts\run-local.ps1 -Mode Verify -ApiPort 5081
```

Verification checks anonymous `/api/health` for HTTP 200 and `{ "status": "ok" }`, then requests the UI root using the current Windows default credentials and requires HTTP 200 HTML. It checks only the loopback API listener and those two endpoints; it does not call SQL, inspect data, or establish database readiness. The request check is useful local Negotiate evidence, but browser policy may differ; confirm the UI in the browser at the same `http://localhost:5081` URL. Do not substitute request headers, tokens, or the API process identity for Windows caller authentication.

The API host serves the generated `wwwroot` assets and SPA fallback behind the `InternalStaff` policy. Runtime verification in the integrated browser loaded the UI from the API origin and reached the order controller; with the SQL credential path deliberately pointed at a nonexistent file, the order lookup displayed the expected sanitized 503. No SQL connection or order-row query occurred. Anonymous health remains independent of UI authentication and database readiness.

The legacy `-Mode Client` is an optional Vite-only preview on `127.0.0.1:5184` (or `-ClientPort`); it has no `/api` proxy, is not authenticated API access, and is not part of this workflow. Its port is checked before launch. Do not use it to assess same-origin Negotiate behavior.

Stop the API by pressing `Ctrl+C` in the exact foreground terminal that started it. The script does not detach or terminate a process tree. Do not use `Stop-Process`, `taskkill`, port-based termination, or other unmanaged cleanup. If Ctrl+C does not release a port, inspect its current owner and leave unidentified processes untouched.

   Never bind Development auth to a wildcard, public interface, tunnel, or reverse proxy.

If `/api/health` returns 200 but the UI root returns 404, the listener may be an older API binary or the React build may not be present in `wwwroot`. Inspect the listener owner and command line without terminating it. Re-run the API workflow on a separately checked free port if needed. A healthy liveness endpoint alone does not prove that the running API binary serves the built UI or contains expected controller routes.

## Validation

From the repository root, run:

```powershell
.\scripts\validate.ps1
```

The script builds and tests the solution using a unique temporary artifacts directory, then runs the client tests, lint, and production build. It removes that temporary directory in its `finally` block. By default, opt-in SQL-clone integration tests are skipped; a passing default validation run is not evidence that a database write or rollback test ran. Report the actual test totals and skipped tests from the command output.

If validation fails, keep the first failing command and full error. Resolve build/test failures in the relevant code or environment; do not delete user data or alter database state to make validation pass. Re-run the same focused check after a local fix, then rerun the full script.

## AW-014 Isolated Clone Writer Procedure

`[scripts/aw014-test-writer.ps1](../../scripts/aw014-test-writer.ps1)` is not part of routine startup or default validation. It performs SQL identity/permission checks and executes a header/detail insert inside a transaction that it explicitly rolls back. If the expected test-writer identity or encrypted credential is absent, the script can create a server login, a clone database user, grants, and a DPAPI credential file. Therefore it is a state-changing administrative/test procedure even though its intended order rows are rolled back.

Run it only under the separately approved AW-014 scope, on the verified local SQL Server and `AdventureWorks2022_Test`, with the intended Windows/SQL administration context. Confirm the current approval and the expected clone, login, and credential state before execution. The script's safety checks are not a substitute for authorization. Never point it at `AdventureWorks2022`; do not broaden permissions if a guard fails.

The expected successful result is verified generated/computed/trigger-maintained values inside one explicit transaction, followed by rollback and checks that no generated header/detail/history rows remain and table counts are unchanged. SQL identity values may still advance after rollback. The trigger error/CATCH path is not covered; do not induce it or claim that path is safe.

### Stop and recover safely

- If a preflight reports a server, database, login, user, or credential mismatch, stop. Do not rerun with altered targets or manually repair partial identity state. Preserve the error text without exposing credentials and have the Database Engineer compare the actual state with `[docs/data/adventureworks-p0-permissions.md](../data/adventureworks-p0-permissions.md)`.
- If the procedure fails after the transaction begins, do not assume rollback succeeded merely because the script attempted it. Stop further test runs. Have the Database Engineer verify the generated IDs, order header/detail rows, trigger side effects, and before/after counts on the test clone. Do not retry until the cause and residual state are understood.
- If any check suggests access to the original database, an unexpected permission, a non-loopback listener, or a credential disclosure, stop the process and report it to the project owner and the relevant database/security owner. Do not attempt cleanup or permission changes without their direction.
- Do not restore, drop, reseed, truncate, or otherwise repair either database as a routine development/test step. Use a recovery procedure only after its separate, explicit operation approval and review; credential/login cleanup also requires separate approval.

### AW-017 Paired-Recovery Test (Executed; Qualified Acceptance)

**Status:** Both approved paired backups passed `VERIFYONLY WITH CHECKSUM` and `FILELISTONLY`. The clone restore `AW017_Restore_AdventureWorks2022_Test` is `ONLINE`/`MULTI_USER`/`READ_ONLY` at its approved MDF/LDF paths, and its separately approved full `DBCC CHECKDB ... WITH MAXDOP = 1` completed with 0 allocation errors and 0 consistency errors. The ledger restore `AW017_Restore_AdventureWorksPocIdempotency` was subsequently completed at its approved paths and settings; its separately approved full `DBCC CHECKDB ... WITH MAXDOP = 1` also completed with 0 allocation errors and 0 consistency errors. Read-only ACL inspection of the restored ledger MDF/LDF remains unavailable to this interactive account; the SQL Server service has an explicit `FullControl` allow ACE on the parent DATA directory, and the ledger restore and CHECKDB completed successfully. Treat file-level ACL visibility as residual uncertainty, not evidence of denied SQL service access; no ACL changes were made. Preserve both backups and both disposable restores as-is. Do not repair, drop, or clean up without new operation-specific approval. A pre-backup settings/schema/object/table-count baseline was not retained, so historical equality with source state at backup time cannot be proven. The restore work establishes no order/ledger transaction atomicity or production recovery objective. There is no order route; keep writes disabled. Backup, restore, DBCC, and cleanup remain separately approved stages.

**Read-only ledger comparison:** The live and restored ledger are both `ONLINE`/`MULTI_USER`, owner `jeffs_dev_pc\jeffj`, `SIMPLE`, compatibility 160, and `SQL_Latin1_General_CP1_CI_AS`, with matching schemas, object-type counts, principal counts, role memberships, and zero user tables. There are no per-table user-row counts to compare. Snapshot isolation is `OFF` on the live ledger and `ON` on the restore; the reason is not established, and the restore should not be normalized without separate explicit approval. Service Broker is enabled on the live ledger and disabled on the restore, while both have the same Broker GUID and identical three services, three queues, and one route; the Architect considers Broker disabled-on-restore an expected restore behavior. All queues have activation disabled. This current-state comparison is nonhistorical because no pre-backup baseline was retained.

**Snapshot-isolation diagnostic (closed unresolved):** Read-only `msdb` history identifies the ledger copy-only full backup at 2026-10-28 15:08:14 and its disposable restore at 16:42:31. `RESTORE HEADERONLY` confirms a normal full backup (`IsSnapshot = 0`), copy-only, with checksums; it does not record `ALLOW_SNAPSHOT_ISOLATION`. The existing default trace contained no matching ledger `ALTER DATABASE`/snapshot-isolation event in the approved 15:00-15:40 window. The retained SQL error log shows backup completion, return to `READ_WRITE`, and database startup, but no snapshot-isolation change. Cause and timing remain unestablished; no setting was changed. On 2026-10-29, the Project Owner accepted this snapshot-option provenance caveat **solely for restore-mechanics evidence**. Both restores passed full `DBCC CHECKDB` with zero allocation/consistency errors; the acceptance does not prove snapshot-option parity, historical equality, or order/ledger recovery. Preserve both backups and restores. Cleanup remains paused pending separate explicit cleanup approval. Further investigation is not indicated unless a contemporaneous SQL Audit/Extended Events or other retained DDL record is identified.

**Pair:** source databases `AdventureWorks2022_Test` and `AdventureWorksPocIdempotency` on local instance `jeffs_dev_pc`. Keep application writes quiesced for the whole pair operation. Before any separately approved run, repeat collision, capacity, source-state, destination, and service-access checks; stop if anything differs from this plan or if a target is occupied.

### Proposed nonempty-pair validation (planning only; not approved for execution)

The Project Owner directed this bounded proposal on 2026-10-29 and selected paired full backups every 1 day, an elapsed-time RPO of at most 1 day from the latest successfully captured recoverable pair, and an RTO of 15 minutes from recovery declaration through both restores, reconciliation, and authorization to resume writes. This is not authorization to access the database or perform backups, restores, DML, recovery, error injection, cleanup, or any other state change. Keep the route disabled. The Project Owner proposed the Deployment Engineer as recovery coordinator; this role may coordinate/document the plan subject to acceptance, but does not replace local-DBA review or gain authority to execute database operations. Count a pair only when both full backups share a pair ID, were captured under approved quiescence, and pass agreed recoverability checks. Measure the maximum 24-hour cadence gap between qualifying pair capture points and RPO age from the recorded pair data-capture point, not later verification. A partial/failed pair does not refresh the recovery point; an age over 24 hours is an objective breach. A daily scheduled start alone does not guarantee the RPO. The 15-minute RTO remains unproven until an approved representative pair exercise is timed.

Before any run, confirm the selected objective can be measured with the specified source pair, allowed test data/effects, test window, quiescence, and individually named disposable database/file targets. Obtain written, operation-specific approval for each backup, restore, test DML, recovery, and trigger-error injection. Review each backup and complete `FILELISTONLY`/`MOVE` map before separate restore approval, as set out below. Obtain separate cleanup approval only after results are reviewed. The RTO clock starts when recovery is declared and ends only after both members are restored, reconciliation passes, and an authorized decision to resume writes; its 15-minute target is unverified. The local DBA must review exact transaction/schema/query/permission boundaries; Security must approve key custody and evidence handling. Do not infer approval from the development authorization or from this proposal.

**Proposed test and evidence checklist:**

1. **Nonempty baseline:** On an explicitly approved isolated test pair, establish one known committed order with its matching durable ledger claim/replay result. Record a pair ID, source identities, test/build and schema versions, relevant settings and key-custody version, and minimal protected reconciliation identifiers/digests. Do not record raw keys, credentials, full customer/order rows, or sensitive payloads in routine evidence. Quiesce writes throughout the paired operation.
2. **Atomic commit:** Verify that order header/details, trigger effects, generated/trigger-maintained values, and the matching replay result commit together and that the persisted confirmation matches the recorded order.
3. **Atomic rollback:** Only under explicit fault-injection/DML approval, inject failures at claim, header, detail, trigger, reload, and replay-save boundaries. Verify failed work leaves no committed order/business effects or replay result. Any diagnostic-only trigger residue must be specifically scoped and accepted in advance; do not assume it is harmless.
4. **Paired restart/replay:** Back up and restore both members together to the individually approved disposable targets. Verify both backup media/file lists, exact restore paths, online state and `DBCC CHECKDB`. Restart the test process with the approved recoverable key custody/version, retry the original request with the original key, and verify the same saved result maps to the original order/lines without creating a second order. Reconcile both directions: each committed order has exactly its matching replay result, and each committed result resolves to the intended order. Never run the application against disposable restore targets.
5. **Evidence and disposition:** Preserve approval references, operator/timestamps, pair ID, settings/schema/key-custody versions, backup verification/file-list results, restore and DBCC results, commit/rollback outcomes, restart/replay comparison, and final paired reconciliation in protected evidence. Keep artifacts until reviewed; cleanup is a separate approved operation.

**Stop and preserve evidence** on unquiesced writes; any target, file, principal, permission, or source mismatch; unexpected backup files or restore paths; failed restore/DBCC; unavailable or changed key custody; ambiguous transaction outcome; order/ledger mismatch; unexpected business effect or unapproved residue; or missing approval. Do not retry, repair, normalize settings, recover/reset only one member, clear the ledger alone, bypass clone guards, or clean up. Keep submission disabled. See ADR-001 “Recovery acceptance beyond AW-019” for the proposal boundaries and required owner decisions.

**Candidate files and disposable targets:**

| Purpose | Exact name/path |
| --- | --- |
| Clone full backup | `C:\Program Files\Microsoft SQL Server\MSSQL16.MSSQLSERVER\MSSQL\Backup\AdventureWorks2022_Test_AW019_PairProbe.bak` |
| Ledger full backup | `C:\Program Files\Microsoft SQL Server\MSSQL16.MSSQLSERVER\MSSQL\Backup\AdventureWorksPocIdempotency_AW019_PairProbe.bak` |
| Clone restore database | `AW017_Restore_AdventureWorks2022_Test` |
| Clone restore data/log files | `C:\Program Files\Microsoft SQL Server\MSSQL16.MSSQLSERVER\MSSQL\DATA\AW017_Restore_AdventureWorks2022_Test.mdf`; `C:\Program Files\Microsoft SQL Server\MSSQL16.MSSQLSERVER\MSSQL\DATA\AW017_Restore_AdventureWorks2022_Test_log.ldf` |
| Ledger restore database | `AW017_Restore_AdventureWorksPocIdempotency` |
| Ledger restore data/log files | `C:\Program Files\Microsoft SQL Server\MSSQL16.MSSQLSERVER\MSSQL\DATA\AW017_Restore_AdventureWorksPocIdempotency.mdf`; `C:\Program Files\Microsoft SQL Server\MSSQL16.MSSQLSERVER\MSSQL\DATA\AW017_Restore_AdventureWorksPocIdempotency_log.ldf` |

The source logical file names observed in read-only metadata are `AdventureWorks2022` / `AdventureWorks2022_log` for the clone and `AdventureWorksPocIdempotency` / `AdventureWorksPocIdempotency_log` for the ledger. Confirm them again with `RESTORE FILELISTONLY` against the approved backups before restoring; capture and review the complete result set for **each** backup, confirm every data/log file and file type, and map every listed file to an individually approved `MOVE` destination. Stop if either result has an unexpected/additional file or differs from the approved path map; do not assume there are only two files. Never use `WITH REPLACE`, `FORMAT`, or a wildcard path. Backup destinations must still be absent immediately before backup. The SQL service account has an explicit, non-inherited `FullControl` allow ACE on the Backup and DATA directories, but the backup files may contain clone customer/order data: retain them only at the approved local paths and verify/review effective file ACLs before any restore or other access. Do not change ACLs without separate approval.

**Proposed sequence after explicit approvals:**

1. Confirm both source databases are online, the route remains unavailable, no POC writer or other process can mutate either source, the exact backup files and restore names/files remain collision-free, and sufficient space remains. Capture a baseline for both databases: relevant recovery/configuration settings, schema/object definitions, and `COUNT_BIG(*)` per user table. Counts are aggregate-only; do not select or report order rows. Record one pair identifier and the start/finish times for both backups.
2. Take both copy-only full backups before restoring either database:

   ```sql
   BACKUP DATABASE [AdventureWorks2022_Test]
   TO DISK = N'C:\Program Files\Microsoft SQL Server\MSSQL16.MSSQLSERVER\MSSQL\Backup\AdventureWorks2022_Test_AW019_PairProbe.bak'
   WITH COPY_ONLY, CHECKSUM, STATS = 10;

   BACKUP DATABASE [AdventureWorksPocIdempotency]
   TO DISK = N'C:\Program Files\Microsoft SQL Server\MSSQL16.MSSQLSERVER\MSSQL\Backup\AdventureWorksPocIdempotency_AW019_PairProbe.bak'
   WITH COPY_ONLY, CHECKSUM, STATS = 10;
   ```

3. Run `RESTORE VERIFYONLY` with `CHECKSUM` and then `RESTORE FILELISTONLY` against each exact backup file. Record outputs, verification status, media identity, and the complete logical-file/type list for both backups. Do not proceed unless both verification commands pass, both backups are recorded as members of the same operator-defined pair, and every logical file has a reviewed, individually approved `MOVE` destination. This is a mandatory stop/review gate; backup approval does not authorize restore.
4. Restore the clone backup to its disposable database and new files:

   ```sql
   RESTORE DATABASE [AW017_Restore_AdventureWorks2022_Test]
   FROM DISK = N'C:\Program Files\Microsoft SQL Server\MSSQL16.MSSQLSERVER\MSSQL\Backup\AdventureWorks2022_Test_AW019_PairProbe.bak'
   WITH MOVE N'AdventureWorks2022' TO N'C:\Program Files\Microsoft SQL Server\MSSQL16.MSSQLSERVER\MSSQL\DATA\AW017_Restore_AdventureWorks2022_Test.mdf',
        MOVE N'AdventureWorks2022_log' TO N'C:\Program Files\Microsoft SQL Server\MSSQL16.MSSQLSERVER\MSSQL\DATA\AW017_Restore_AdventureWorks2022_Test_log.ldf',
        CHECKSUM, RECOVERY, STATS = 10;
   ```

5. Restore the ledger backup to its disposable database and new files:

   ```sql
   RESTORE DATABASE [AW017_Restore_AdventureWorksPocIdempotency]
   FROM DISK = N'C:\Program Files\Microsoft SQL Server\MSSQL16.MSSQLSERVER\MSSQL\Backup\AdventureWorksPocIdempotency_AW019_PairProbe.bak'
   WITH MOVE N'AdventureWorksPocIdempotency' TO N'C:\Program Files\Microsoft SQL Server\MSSQL16.MSSQLSERVER\MSSQL\DATA\AW017_Restore_AdventureWorksPocIdempotency.mdf',
        MOVE N'AdventureWorksPocIdempotency_log' TO N'C:\Program Files\Microsoft SQL Server\MSSQL16.MSSQLSERVER\MSSQL\DATA\AW017_Restore_AdventureWorksPocIdempotency_log.ldf',
        CHECKSUM, RECOVERY, STATS = 10;
   ```

6. Verify both disposable databases are online and accessible, their physical files are exactly the four approved restore paths, and capture their settings, schema/object definitions, and `COUNT_BIG(*)` per user table. Compare those values with the current source databases only as nonhistorical diagnostic evidence; do not claim equality with the source at backup time because no pre-backup baseline was retained. Check the clone restore retains its observed SIMPLE recovery, compatibility 160, and `SQL_Latin1_General_CP1_CI_AS` collation; check the ledger against its captured approved shell settings. Run `DBCC CHECKDB` on each disposable restore and require no errors. Confirm the live source pair remains online and its catalog/file paths are unchanged. Keep writes disabled; any mismatch or failed check means stop and do not reopen submission.
7. Leave disposable restores and backup files in place until results have been reviewed. If a restore command is interrupted or only one target appears, stop and inspect read-only state; do not retry, repair, drop, or clean up without new explicit approval. Cleanup is a separate destructive operation and is not included in backup/restore approval.

**Minimum Project Owner approvals before execution:** obtain (a) a first, backup-only approval for copy-only full backups of the two named source databases to both exact `.bak` paths above; (b) after both backups pass `VERIFYONLY` and their complete `FILELISTONLY` outputs and `MOVE` maps are reviewed, a separate restore approval for both exact disposable database names and every individually listed/approved MDF/LDF destination, explicitly accepting that no pre-backup baseline was retained and limiting acceptance to restore mechanics/integrity; and (c) cleanup approval only after results are reviewed, covering only those two disposable databases, those two `.bak` files, and any of the four listed restore files that remain after database drops. Backup approval does not authorize restore, and restore approval does not authorize cleanup. Cleanup approval must be obtained separately. A failed `DROP DATABASE` is a stop condition; do not force-disconnect sessions or delete files belonging to an attached database. Each approval is local POC scope only and does not authorize production operations.

**Cleanup only after separate approval:** verify the two disposable names still refer to the test restores, then issue `DROP DATABASE` for `AW017_Restore_AdventureWorks2022_Test` and `AW017_Restore_AdventureWorksPocIdempotency`. Confirm both names are absent before removing only the two exact backup paths above and, if still present and detached, only the four exact restore paths above. Do not use wildcard deletion and do not remove either source database or its files.

## Current Limits

- The API reader is DPAPI-protected for the current Windows user and is read-only. It is not a staff identity or a writer.
- The AW-014 test writer is limited to the approved clone test and is not an API/runtime or production identity.
- Production OIDC, staff scope, Finance rules, durable idempotency, and an order route/writer remain unimplemented or gated. There is no order-submission or production recovery procedure in this runbook.