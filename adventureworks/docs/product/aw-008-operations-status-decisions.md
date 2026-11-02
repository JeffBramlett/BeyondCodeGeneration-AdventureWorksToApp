# AW-008 Operations Status Decisions

**Status:** Decision aid only. No Operations status meaning, transition, role, or database mapping is approved here. Keep AW-008 deferred until order creation exists and an Operations owner approves the workflow.

**POC storage exception (2026-10-28):** The Project Owner approved omitting `SalesOrderHeader.Status` for POC orders in `AdventureWorks2022_Test` and relying on that clone's verified default only as a storage placeholder. Read-only clone metadata reports `tinyint NOT NULL`, default `DF_SalesOrderHeader_Status ((1))`. This does not assign business meaning to the value or approve production mapping. Confirmation remains “Recorded” only. Operations approval is still required for business status meanings, transitions, authorities, and user-facing lifecycle wording.

## Product boundary

The approved P0 confirmation meaning is only that an order was recorded successfully. It does not mean accepted for fulfillment, shipped, fulfilled, or reserved. Until Operations approves a next-state meaning, the UI and API must not say or imply that an order is “awaiting disposition” or that a particular team will act next.

AW-008 is P1 and depends on a working, authorized order-creation workflow. Do not implement status updates before orders can be safely recorded and the relevant order visibility and authorization rules are approved.

## Operations owner decisions

An Operations owner with authority over fulfillment disposition should approve:

| Decision | Required answer |
|---|---|
| Status meanings | Define each user-visible state in plain language, including what it does not assert. Distinguish at minimum recorded, accepted for fulfillment, shipped, fulfilled, and reserved; include only states the business actually uses. |
| Initial disposition | State whether newly recorded orders have an Operations disposition at all. If yes, define its meaning and the event/owner that establishes it. Do not infer this from a database default or numeric value. |
| Transitions | List allowed transitions, whether any are terminal or reversible, and how cancellation, correction, rejection, or exception cases are represented. |
| Authority | Identify which approved staff role may perform each transition, whether a second approval is needed, and how permissions are reviewed. Identity/IT and Security must validate the corresponding identity mapping separately. |
| Exceptions | Define behavior for stale/invalid transitions, unavailable disposition data, duplicates, and unknown outcomes. Until resolved, reject or show an unknown state; never silently advance or substitute a status. |
| Concurrency | Decide how simultaneous updates are resolved and how a stale screen is detected. Require a conflict response rather than silently overwriting a newer disposition. |
| Audit | Specify the minimum history needed: actor, time, prior and resulting states, and any required reason or approval reference; identify access and retention owners. |
| User wording | Approve exact labels and confirmation text so “recorded” is not confused with fulfillment acceptance, shipment, completion, or reservation. |

The Product Owner should approve the user-visible workflow and acceptance criteria after Operations defines the business meanings. The Project Architect and Database Engineer should then verify that the target schema can represent those meanings without ambiguity. Schema values, constraints, and sample rows are compatibility evidence only; they do not supply policy.

## Recommended sequence

1. Complete and authorize P0 order creation under its separate Finance, Identity, idempotency, and persistence gates.
2. Obtain Operations approval of the meanings, transitions, authority, exception behavior, concurrency, and audit requirements above.
3. Obtain Product Owner approval of the user-facing workflow, then verify the approved concepts against the target schema and authorization model.
4. Implement only the approved transitions, with conflict detection, audit evidence, and tests for unauthorized, invalid, stale, and concurrent updates.

Until those decisions exist, AW-008 remains Proposed. Order confirmation remains “recorded/submitted” only. No numeric database status is selected, and no status-write route or UI is authorized by this brief.