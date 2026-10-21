# Prompt: finish custody, vehicle fuel, GPS, HR cases, incentives, payroll and audit in AlMotaheda ERP

You are continuing work on the AlMotaheda ERP backend, a .NET 10 modular monolith on SQL Server with EF Core 10.
Repo: `~/Desktop/dotnet /AlMotaheda_ERP` (the folder name contains a space, so quote the path). The Flutter app is a
separate repo, `~/StudioProjects/al_motaheda_erp_flutter`.

Your job is to **verify, repair and complete** what exists, not to design a new system. Most of the building blocks
are already there. Some are only half-built on the current branch. Read before you write.

---

## 0. Ground rules

1. **Start from the current state.** The branch is `feature/treasury-custody-fleet`. It has a large amount of
   **uncommitted and untracked** work in Finance, Fleet, HR, Projects and Workforce, and its plan is
   `docs/project-workspace/11-treasury-custody-audit.md`.
   - Read that file first. Treat its tables as the feature matrix you extend.
   - Do not discard, rewrite or "clean up" that work. Finish it.
   - Ignore the copies under `.claude/worktrees/*`.
2. **Follow `docs/architecture.md`.** In particular:
   - Vertical slices: one folder per use case under `Features/`.
   - Everything inside a module is `internal`.
   - Modules talk only through `*.Contracts` projects.
   - Each module owns one schema and its own `DbContext` and migrations.
   - `SharedTransaction` is used for operations that span two modules.
   - Errors go out as `ApiProblem`, each with a `code`.
   - Settings are options classes bound with `ValidateOnStart()`.
   - `ModuleBoundaryTests` and `EndpointAuthorizationTests` must stay green.
3. **Keep one book of money.**
   - `finance.LedgerEntry` (single entry, signed, append-only, reversed by negated copies) is the only ledger.
   - Every custody balance must reconcile to it, or to the custody sub-ledgers listed in §2.
   - Do not add another ledger, balance table or "wallet".
4. **Reuse the existing helpers rather than writing your own:**
   - Concurrency and idempotency: `rowversion` (`RowVersions`), `SqlAppLock`, unique `IdempotencyKey` columns, and the "re-check the permission in the database" pattern (`IPermissionCheck.HoldsAsync`, `IExecutiveCheck`).
   - Status rules: `StatusFlow<T>`.
   - Company time: `CompanyClock` (`Projects:TimeZone`, default `Africa/Cairo`).
5. **Backend first.**
   - Change Flutter only where a backend capability would otherwise be unreachable.
   - List every Flutter screen still needed in the final report.
6. **Do not invent data.** That means no fake GPS points, sample balances or seeded "demo" cases presented as real.
7. **Do not commit, push, merge or deploy** unless I ask. Leave the work as uncommitted changes and report them.
8. **Phases.** Work in the phases of §9. After each phase:
   - Build.
   - Run that phase's tests.
   - Write a short progress note in `docs/project-workspace/11-treasury-custody-audit.md`.
   - Then continue.
   - Stop and ask only for the decisions in §1 that block the phase. Everything else: decide, record the decision in the audit doc, and keep going.

---

## 1. Roles: how my business terms map to the code

Roles are fixed codes in `AccessRoles.cs`. Permissions are `module:letters`, with the letters
V(iew) C(reate) E(dit) A(pprove) F (see money). Defaults are in `PermissionCatalog.RoleDefaults` and
`docs/modernization/03-permission-matrix.md`. **Do not add roles.** Map my terms like this:

| My term | In the code | Notes |
|---|---|---|
| Financial Manager / Executive Manager | `executive` | The same person. Top-level approver. |
| Accountant | `accountant` | Sees widely: `custody:VAF`, `payments:VCF`, `finance:VCEAF`, `hr:VAF`. |
| Engineer | `engineer` + `ProjectMember` (`SiteEngineer` / `ProjectManager`) | Sees only projects with an active membership. |
| Office Manager | `office_admin` | Office custody is being built on the **other branch**, `feature/office-management`. See D5. |
| Driver | `hr.Employee` + `fleet.VehicleDriverAssignment`; signs in (if at all) as `staff` | `staff` has `fuel:VC` and `fleet:V`. |
| HR reviewer | `hr` (and `executive`, since there is no HR employee yet) | `hr` has no `payments` or `finance` access, by design. |
| "مسؤول الكتب" | **No such role.** Treat as an `hr.Employee` who holds a custody account. | See D1. |

**Decisions you need from me.** For each, use the default unless I override it, and note in the audit doc which
default you used:

- **D1. Who is "مسؤول الكتب"?** Default: an employee custody holder. No new role.
- **D2. Fuel review.** Default: the existing rule.
  - The Accountant (`fuel:A`) reviews.
  - The Executive must also decide when the claim is escalated or above `Fleet:ClaimExecutiveApprovalAbove`.
  - Do **not** add a mandatory two-step approval unless I ask.
- **D3. Accountant price proposals.** The Accountant has `catalog:VF` today, so it cannot propose a price range.
  - Default: grant `catalog:C` to `accountant` in a seed migration.
  - Approval stays Executive-only.
- **D4. Negative custody balances.** `Projects:CustodyBalanceCheck` defaults to `Warn`, which allows a negative balance.
  - Default: keep `Warn`.
  - But every negative balance must show as a shortfall item on the custody monitoring view (§6).
- **D5. Office custody.** Default: **do not** re-implement it here.
  - Report what `feature/office-management` already provides.
  - Ask before merging it.
  - A `CustodyAccount` of kind `Office` is already reserved.
- **D6. GPS source.** Hardware telematics, or the driver's phone. See §4. Default: build the provider seam only and report the dependency.
- **D7. Legal limits on deductions** (Egyptian Labor Law No. 14 of 2025). Never hardcode them. See §5.D.

---

## 2. What already exists (verify it; don't rebuild it)

Confirm each item by reading the code and running its tests. If something is wrong, fix it in place.

**Finance (schema `finance`). Routes sit directly under `/api` (no `/api/finance` segment).**
- `PaymentRequest` (`PAY-…`, `IdempotencyKey`, `RowVersion`, `SourceCustodyAccountId`).
  - Statuses: Submitted → Approved → SentToBank → Paid, plus Rejected / Failed / Cancelled / Reversed.
  - The creator can never approve their own request (`PaymentRequestRules`, decision `OwnRequest`).
- `LedgerPosting.Paid`, `.Transfer` and `.Reversal`. A reversal happens only through `IPaymentRequests.ReverseAsync`.
- `TreasuryAccount` (Bank, Cash): opening balance set once by the Executive. Balance = opening balance + ledger. `GET /treasury`.
- `CustodyAccount` (`CA-…`). Kinds: `Accountant`, `PayrollBatch`, `EngineerProject`, `Vehicle`, `Office`.
  - Finance stores the movements itself only for `Accountant` and `PayrollBatch` (`CustodyMovement`, append-only).
  - `EngineerProject` maps to `projects.CustodyEntry`.
  - `Vehicle` maps to `fleet.VehicleCustodyEntry`.
  - Both of those are reached through `ICustodyLedgerSource`.
- `CustodyTransfer` (`CT-…`).
  - Flow: Requested → Approved → Disbursed → Received, plus Rejected / Cancelled / Reversed.
  - Reservations hold money while a transfer is open.
  - Routes: `/custody-transfers/{id}/approve|reject|disburse|receive|cancel|reverse`.
- `CustodyAdjustment`, `CustodyCashCount` and `CustodyPeriodClose`, under `/custody-accounts/{id}/…`.
  - An adjustment needs `custody:A` and is never made by the holder.
- `PayrollBatch` and its allocations. `IPayrollEntitlementSource` is implemented by Workforce (`DailyLabor`) and by HR (`Salaries`).

**Projects**
- `CustodyEntry`: the engineer's project custody (Funding, Return, Payroll, AdjustmentIn, AdjustmentOut).
- `DailyReport` (+ lines, materials, equipment). It holds **no money**. There is one per author, project and day.
- `ProjectItem` ("بند"): the hierarchical item tree we use instead of a BOQ or cost code.
- `ProjectMember`.
- Site geofence: `Project.SiteLatitude`, `SiteLongitude`, `SiteRadiusMeters`.
- User location: `LocationSharing`, `LocationPing` (kept 90 days, `[NotAudited]`), `LocationAlert` / `OutsideSiteAlert`.
- **Price Control** (`docs/project-workspace/10-price-control.md`):
  - `PriceItem`, plus `PriceRange`. A range is **insert-only**: Pending → Approved | Rejected, effective from a date, general or per supplier.
  - `PriceException` and `Projects:PriceCheckPolicy`.
- Suppliers (`projects.Supplier`). Supplier payments.

**Workforce**
- `Worker`, `WorkerAssignment`, and `DailySheet` (the labor sheet: Draft → Submitted → Approved / Returned / Rejected).
- `AttendanceLine`: days, overtime hours, rate, allowances, deductions.
- `LaborAllocation` to `ProjectItem`.
- `PayrollSettlement`, the weekly settlement: Pending → Confirmed / Rejected / Reversed. Confirming it needs `custody:A` and posts `CustodyEntry` Payroll.
- `WorkerPayment` (FIFO).
- `/projects/{pid}/labor/payable`.
- Overtime: `Workforce:RegularHours`, `Workforce:OvertimeMultiplier`.

**HR**
- `Employee` (kinds `OfficeEmployee`, `Engineer`, `Staff`; optional `UserId`), `Department`, `EmployeeDocument`.
- `SalaryStructure`, versioned by `effectiveFrom`.
- `PayrollInput`: Bonus / Deduction / AdvanceRecovery.
- `SalarySheet`: Draft → Submitted → Returned / Approved → Paid. The Accountant approves. The Executive decides the Finance payment request.
- New on this branch: `EmployeeAssignment` and `HrReferral`.
  - `HrReferral` statuses: Open, UnderInvestigation, Closed.
  - Outcomes: DeductionApplied, Warning, NoAction.
  - Sources: VehicleExpense, VehicleViolation.

**Fleet** (new on this branch, **not wired up yet**)
- `Vehicle` (an extension of `Equipment` with category `Vehicle`), `VehicleDriverAssignment`, `VehicleCustodyEntry`.
- `VehicleExpense`, the claim: Submitted → Approved / Rejected / EvidenceRequested / Escalated, with a posting status.
- `VehicleExpenseFlag`: duplicate and plausibility flags.
- `VehicleViolation`.
- `FleetAttachment`: private storage, file type checked from the bytes, SHA-256 hash, served only through the API with `no-store`.

**Procurement**
- `Requisition`, `PurchaseOrder`, `GoodsReceipt`, `SupplierInvoice` (3-way match, `PriceHold`).

**Platform**
- `AuditTrailInterceptor` writes `platform.AuditEntry` (field-level old/new JSON) and `platform.OutboxMessage` in the same transaction as every module save.
- `GET /audit-entries` (Executive only).
- Notifications (`NotificationRules`, FCM push, SignalR `/hubs/erp`).
- The work inbox (`GET /inbox`).

**Reports**
- The `IReportSource` engine, `/reports/{code}` (Excel and print). Codes include FIN-CUSTODY, FIN-CUSTODY-RECON, FIN-TREASURY, FIN-PAYROLL-BATCH, FLT-CLAIMS, FLT-VEHICLE-CUSTODY and WRK-PAYROLL.
- **There are no dashboards.**

---

## 3. Known gaps and suspected defects (fix these first)

These were found by reading the code. Confirm each one, then fix it.

1. **Fleet wiring and the new migrations: verify them.** This was being finished while this prompt was written. As
   of 2026-10-20:
   - `FleetFeatures.Map` maps the vehicle, attachment, claim, violation and GPS endpoints.
   - `FleetOptions` and `IFleetFileStore` are registered, and so is `VehicleCustodyLedgerSource`.
   - The migration `VehiclesAndCustody` exists.

   Confirm all of it builds and works. Then check that these migrations apply cleanly, in the documented module
   order, on an empty database: Finance `TreasuryAndCustody`, HR `AssignmentsAndReferrals`, Projects
   `CustodyAdjustmentsAndVehicleExpenseCases` and Fleet `VehiclesAndCustody`. Finally, check that a
   `CustodyTransfer` can fund a `Vehicle` account end to end.
2. **A rejected fuel claim makes spent cash "come back".** Today, Reject *releases* the Pending custody entry, so the driver's available balance goes back up.
   - That is wrong when the cash was already spent.
   - Required behaviour: a rejected claim leaves an **unresolved custody item** (amount, claim, reason) that keeps lowering the available balance.
   - It stays until one of these happens: (a) the claim is resubmitted and approved; (b) the driver returns the cash (`Return`); or (c) an authorized `CustodyAdjustment` charges it to the holder with a reason.
   - The same rule applies to rejected engineer and office expenses.
3. **Fuel evidence.** `FleetAttachmentPurpose` has no "vehicle at the pump" value.
   - For `VehicleExpenseType.Fuel`, require a `Receipt` (or `Invoice`) photo **and** a vehicle-at-pump photo.
   - Add the enum value and the check constraint in the Fleet migration.
4. **Engineer funding requests.** Can an engineer *ask* for money (a `CustodyTransfer` in Requested, destination = their own `EngineerProject` account, source = the Accountant's account)?
   - If not, allow it.
   - Approval and disbursement must be done by someone other than the requester.
   - An approved request moves no money. Money moves only at Disbursed/Received.
5. **Audit immutability is only a convention.**
   - Protect `platform.AuditEntry` at the database level, with an `INSTEAD OF UPDATE, DELETE` trigger or `DENY` in a Platform migration.
   - Make sure the bulk `ExecuteUpdate`/`ExecuteDelete` calls in Access still produce audit rows, or add them explicitly.
6. **`HrReferral` close with `DeductionApplied`** immediately creates a `PayrollInput` Deduction. Keep that flow, but it must also:
   - require a reason and evidence;
   - check the configured, versioned deduction policy (§5.D);
   - refuse to let anyone decide their own case;
   - keep the deduction off the salary sheet until the sheet's normal Accountant approval.
7. **More referral sources.** `HrReferralSources` has only VehicleExpense and VehicleViolation. Add the ones §5.C needs.

---

## 4. Vehicle fuel custody and GPS

**A. Driver custody account.** Each vehicle has a `Vehicle` custody account. The accountable driver is taken from
`VehicleDriverAssignment` at the date of each entry. Expose, from the backend:
- posted balance;
- pending claims;
- reserved amount;
- unresolved (rejected but spent) amount;
- available balance;
- full history.

Worked example:
1. The vehicle receives EGP 2,000 through a `CustodyTransfer`.
2. The driver submits a EGP 500 fuel claim. Posted stays 2,000; available becomes 1,500.
3. The claim is approved and posted **once**. Posted becomes 1,500, linked to the vehicle, the driver, the project (if any) and the Fuel category through Price Control.
4. If the claim is rejected instead, posted stays 2,000, available stays 1,500, and an unresolved item of EGP 500 is shown.

**B. Minimal driver flow.** Drivers may read and write very little. The flow is:
1. Open "my vehicle".
2. Tap **"تسجيل بنزين"**.
3. Enter the amount.
4. Take a receipt photo.
5. Take a photo of the vehicle at the pump.
6. Submit.

Everything else must be derived on the server: the vehicle, the driver, the date and the GPS coordinates from the
phone. The backend validates:
- the amount;
- that the caller is the vehicle's **currently assigned** driver;
- the required attachments;
- the file types (`FleetFileTypes`);
- the claim age (`Fleet:MaxClaimAgeDays`);
- the `IdempotencyKey`.

`VehicleExpenseFlag` raises review flags. It never blocks the submission.
- Flutter: only this one screen and the reviewer's detail view, in the separate repo, and only if the endpoints are otherwise unusable.
- Otherwise list them in the report.

**C. Review.** Reviewers use the existing claim routes: approve, reject (reason required), request-evidence, escalate,
refer-to-hr and reverse. They see the amount, vehicle, driver, date, flags and both images (through
`/fleet/attachments/{id}/content`). Approval posts exactly once, under a lock with a row-version check. Neither the
submitter nor the driver may review.

**D. GPS.** No real tracker is connected. A seam already exists: `Features/Gps/VehicleLocation.cs` has
`IVehicleLocationProvider`, `NullVehicleLocationProvider` and endpoints for the latest position, positions and trips.
They answer `NotConfigured` when there is no provider. Verify the seam and complete it:
- Today it only asks the provider for data. Add a stored history, `fleet.VehicleLocationPoint`:
  - append-only;
  - indexed on (VehicleId, RecordedAt);
  - pruned after `Fleet:Gps:RetentionDays`;
  - marked `[NotAudited]`, like `LocationPing`.

  Also add an ingestion path a real provider (pull or webhook) can feed, so route history survives the provider's own retention.
- A day view (an existing endpoint, or `GET /vehicles/{id}/route?date=`) that defaults to today in `CompanyClock`, using the correct day boundaries. It returns:
  - the latest point;
  - the day's points (paged);
  - trip start/end and distance **only when the data supports them**;
  - the assigned driver(s) at those times;
  - points outside every project's site geofence, as **review items**.
- Restrict it to `executive` (and `fleet:A` if you judge it fit). Audit who reads it.

Rules:
- With no provider configured, answer `providerStatus: "NotConfigured"` with no points. Never synthesize a route.
- Do not label a driver as at fault. A deviation becomes a review item, never an automatic deduction.
- There is no "company office" location today. If you need one for deviation checks, add it as configuration, not as a fake project.
- `LocationPing` (users' phones) is a different thing. Do **not** silently reuse it as vehicle GPS. If you think the
  driver's phone should be the vehicle tracker, describe that option (D6) in the report instead.

---

## 5. HR: cases, attendance/overtime, warnings, deductions, employee requests

There is no HR employee yet, so the `executive` (and the `hr` role when one is assigned) does this work. Extend
the existing HR module. **Do not create a second employee table.** Drivers and engineers are `hr.Employee` rows,
linked to `access.User` through `UserId`. Daily-paid workers stay in Workforce and **never** go on the monthly
`SalarySheet`.

**A. Employee records.** Reuse `Employee`, `EmployeeDocument`, `SalaryStructure` (a new version per approved change)
and `EmployeeAssignment`. Salaries are visible only with `hr:F`. An employee must never edit their own salary or
approve their own inputs.

**B. Employee attendance and overtime.** These are not built for salaried staff; `10-hr-design.md` marks them [Confirm].
- Add a small, traceable model: record → approve (never your own) → becomes an eligible `PayrollInput` earning (add an `Overtime` kind if needed). Holiday work is handled the same way.
- The rate rules are a **configurable, versioned policy**. Do not hardcode legal multipliers.
- Every correction keeps its source, evidence, approver and timestamp.
- Unapproved overtime never reaches an approved sheet.

**C. Review cases from operational events.** Extend `HrReferral` (rename nothing). Each case links to its source
record and evidence. Add only the sources the system can actually detect:

| Event | Detected from | Also routed to |
|---|---|---|
| Fuel claim with missing or unreadable evidence | `VehicleExpense` refer-to-hr (exists) | — |
| Engineer repeatedly outside the site without a reason | `OutsideSiteAlert` / `LocationAlert` (exists) | Project manager |
| Material received without an invoice (e.g. 20 t of steel) | Procurement `GoodsReceipt` with no `SupplierInvoice`, or a project supply with no Archive document. **Inspect which records are real.** There is no "material delivery" entity. | **Procurement / Accountant first**; HR only if disciplinary review is warranted |
| Damage or incident with assigned equipment or a phone | Manually reported only. Label it `Unverified`. | Equipment owner |
| Attendance discrepancy or unapproved absence | The attendance model from B | — |

A case never moves money by itself. The review steps are:
- record the employee's explanation;
- then close with NoAction, Warning or Further investigation;
- or **propose** a deduction, which needs approval by someone other than the proposer;
- or escalate.

A consequential decision needs a reason. Warnings are only an outcome today. Add a small `EmployeeWarning` record if
warnings must be listed per employee.

**D. Deductions and the law.**
- Before writing any deduction rule, research Egyptian Labor Law No. 14 of 2025 and its implementing regulations, using official sources (the Official Gazette and the Ministry of Labour). Cite what you used.
- Put every limit in a versioned `hr` policy record, with an effective date and the source it came from.
- Where you could not verify something, leave it unset and fail closed: a deduction that needs that limit cannot be approved.
- List the open legal questions for professional review. Do not present legal conclusions as settled.

**E. Employee requests to HR, for the engineer account.**
- Add an `HrRequest` with these types: attendance correction, leave or permission, overtime clarification, payroll question, and reply to a case.
- Each request has attachments, a status (Submitted / InfoRequested / Approved / Rejected / Closed) and threaded replies.
- Notify through the existing `NotificationRules` and inbox.
- An employee sees only their own requests and cases. They cannot approve their own requests or change approved payroll inputs.

---

## 6. Treasury and custody: the controlled money flow

Use `TreasuryAccount`, `CustodyAccount`, `CustodyTransfer`, `ICustodyLedgerSource`, `PaymentRequest.SourceCustodyAccountId`
and Workforce `PayrollSettlement`. Prove the following scenario end to end with an integration test in
`tests/AlMotaheda.Integration.Tests`. Use the real HTTP endpoints and different users for each role. **Approving a
step moves no money; only Disbursed/Received, a confirmed settlement or a paid request moves money.**

| Step | Mechanism | Treasury Cash | Accountant `CA` | Engineer project custody | Labor still owed |
|---|---|---|---|---|---|
| Opening balance | `PUT /treasury/Cash/opening-balance` | 1,000,000 | 0 | 0 | — |
| Fund the Accountant | `CustodyTransfer` Treasury → Accountant, received | 900,000 | 100,000 | 0 | — |
| Fund the engineer | `CustodyTransfer` Accountant → EngineerProject, received | 900,000 | 50,000 | 50,000 | — |
| Week's labor approved: 55,000 | `DailySheet`s approved; `labor/payable` = 55,000 | — | 50,000 | 50,000 | 55,000 |
| Engineer pays 50,000 | `PayrollSettlement` Pending → Confirmed by the Accountant | — | **50,000 (no second debit)** | 0 | 5,000 |
| Engineer requests 5,000 | `CustodyTransfer` Requested, then Approved | — | 50,000 | 0 | 5,000 |
| 5,000 disbursed and received | `CustodyTransfer` Disbursed → Received | — | 45,000 | 5,000 | 5,000 |
| Engineer pays the rest | second settlement confirmed | — | 45,000 | 0 | 0 |

Total labor expense recorded: 55,000, each part posted once. Also test:
- **Accountant → "مسؤول الكتب".** From a 100,000 Accountant balance, a transfer of 5,000 leaves 95,000 **only after** it is received. While Requested or Approved, the money is reserved, not posted.
- The same transfer cannot be posted twice: same idempotency key, a concurrent approve, a double receive.
- **Transfers are never expenses.** FIN-CASH and FIN-CUSTODY show them as internal transfers.
- Duplicate and concurrent approval of the same claim or settlement posts once.
- A posted entry is never edited or deleted. Corrections go through `Reversal` or `CustodyAdjustment`.
- The roles in §1 can do only what their permission letters allow. A direct API call cannot skip an approval step.
- Money is `decimal(18,2)` everywhere (`StorageConventions`).
- Balances are computed from the books and checked against FIN-CUSTODY-RECON.

**Manager-initiated adjustments.** `CustodyAdjustment` exists. Verify, or add, all of the following:
- debit and credit;
- a required reason and optional evidence;
- the initiating Executive or reviewer, never the holder;
- a notification to the holder;
- an optional **dispute** reply from the holder (through `HrRequest` type "reply" or a small dispute field; your call, recorded);
- posting only when valid;
- a shortfall shown when a debit exceeds the balance (D4).

A custody debit **never** becomes a payroll deduction. That needs the separate HR case → approved `PayrollInput` → salary sheet path.

**Unsupported expenditure.**
- An expense charged to any custody account (engineer, vehicle, office, accountant cash payment) without the evidence policy requires must be flagged as an exception and shown as "unreconciled".
- Preserve it. Never treat it as reconciled.
- An emergency without a document needs an explicit exception approval, not a bypass.

---

## 7. Labor, incentives and payroll

**A. "Daily Labor Cost Report" = the existing Workforce `DailySheet`.**
- Do **not** create a third daily report. `DailyReport` (Projects) records the work and holds no money. `DailySheet` records attendance, rates and cost, allocated to `ProjectItem`.
- If useful, link them: a daily report shows that day's labor sheets.
- Keep these stages distinct in data and in the API: recorded → cost calculated → approved → paid (`WorkerPayment` via a confirmed settlement) → reconciled against custody.
- A sheet or a settlement plan is never proof of payment.
- The weekly view for the engineer comes from `labor/payable` and the settlements. It shows workers, days, rates, amount due, amount paid, amount outstanding and discrepancies.

**B. Incentives.** Nothing exists for this. Build it small:
- An `Incentive` case with a type: `DailyReportCompleteness` or `MaterialSaving`.
  - Lifecycle: Proposed → Approved / Rejected, by an approver who is not the beneficiary.
  - Approval creates one `PayrollInput` Bonus that references the case.
  - A unique source key prevents paying it twice.
- **Daily report completeness:** a configurable checklist (required fields, photos, labor sheet approved). Submitting a report never creates a bonus by itself.
- **Material saving:**
  - Saving = (approved `PriceRange` reference valid **on the invoice date**, same `PriceItem` and the same supplier scope where applicable − invoiced unit price) × **accepted** `GoodsReceipt` quantity.
  - It requires a matched `SupplierInvoice` and must include the configured extra costs.
  - Exclude price-hold, rejected or unmatched lines.
  - Apply the configured share and cap.
  - The case is only proposed. It is never paid automatically.
  - Worked example: reference 1,000, paid 900 per ton gives a saving of 100 per ton.

**C. Payroll.**
- `SalarySheet` is the payroll. Extend its inputs with approved overtime, holiday work, incentives and deductions. Keep its snapshot lines.
- Verify these controls:
  - draft → submit → approve (not the preparer) → Finance payment request → paid exactly once (no second request while one is open);
  - an approved or paid sheet is locked;
  - corrections go into a later month as an adjustment input.
- Weekly labor stays in Workforce and `PayrollBatch`.

---

## 8. Approval model, price changes, audit and dashboards

**A. Pending vs effective.** Every approval flow distinguishes Draft, Submitted, Approved, Rejected, Executed/Posted
and Reversed. Reuse `StatusFlow<T>` and each module's existing rules; do not build a generic workflow engine.
Calculations use only effective values.

**B. Price changes.** Verify the existing `PriceRange` flow against this example:
1. The Accountant (after D3) proposes 1,400 against an approved 1,300.
2. The proposal is stored with the old value, the requester, a reason and an effective date.
3. It appears in the Executive's queue.
4. Price checks keep using 1,300 until it is approved.
5. Approval makes 1,400 effective from its date. Rejection keeps 1,300, and the rejected 1,400 stays in history.
6. Purchase orders and records already priced keep their comparison.

Then list the other financially consequential fields an Accountant can change. Candidates include budgets
(`Budget` has an approve step), supplier payment details, subcontract values and payment classification. For each:
- say whether it is already approval-gated;
- gate the ones that are not, where the business impact justifies it.

**C. Universal audit.** `AuditTrailInterceptor` already records every EF save. You need to:
- protect the table (§3.5);
- make sure approvals and decisions record actor, reason, previous value, new value and resulting transaction reference;
- confirm deletes of business records are soft deletes;
- extend `GET /audit-entries` with server-side filters and paging: actor, affected person or account, project, module and entity, date range, action, amount where applicable.

It must answer: "who changed this value, from what, to what was proposed, who decided, when, and what is effective now."

**D. Dashboards.** There are none. Add read-only endpoints composed from existing queries and report sources, with
no new tables and no hardcoded numbers:
- `GET /dashboards/executive`:
  - vehicle positions, only when a provider is configured, otherwise `NotConfigured`;
  - pending and rejected fuel claims;
  - unresolved custody items and shortfalls;
  - labor summary by project;
  - HR cases awaiting decision;
  - proposed incentives;
  - payroll inputs awaiting a sheet.
- `GET /dashboards/accountant`: custody balances, transfers in and out, pending settlements, funding requests, claims to review, unreconciled expenses.
- `GET /dashboards/hr`: attendance and overtime exceptions, referred cases, warnings, proposed deductions, approved inputs by pay month.
- `GET /me/engineer-overview`: the engineer's projects, custody per project (company money, **never** mixed with their salary), pending requests and settlements, recent movements, labor sheets, settlement status, HR requests.
- **Central custody page** `GET /custody-holders?from=&to=`. One row per holder, across all account kinds, with:
  - balance;
  - received;
  - approved spending;
  - pending;
  - unresolved;
  - owed;
  - last movement;
  - project.

  Each row drills down into that holder's paged history with evidence links. It reuses FIN-CUSTODY and FIN-CUSTODY-RECON logic.

Every dashboard enforces the same permissions as the underlying records.

---

## 9. Phases and tests

| Phase | Scope | Must pass before moving on |
|---|---|---|
| 1 | Baseline. Run `dotnet build AlMotahedaERP.slnx` and the existing tests **before** changing anything, and record the failures. | Baseline recorded |
| 2 | §3 gaps 1–5. Verify the Fleet wiring and migrations; fix rejected-claim custody, fuel evidence, engineer funding request, audit protection. | Fleet, Finance and Platform tests; migrations apply in order on an empty database |
| 3 | §6 money flow plus adjustments plus the unsupported-expense flag | Scenario table test; concurrency, idempotency and authorization tests |
| 4 | §4 fuel flow plus GPS seam | Fuel and GPS tests (below) |
| 5 | §5 HR (cases, attendance and overtime, policy, requests) plus §3 gaps 6–7 | HR tests |
| 6 | §7 labor link, incentives, payroll extensions | Workforce, incentive and payroll tests |
| 7 | §8 price-change verification, field gating, audit search, dashboards | Approval, audit and dashboard tests |
| 8 | Full regression | All tests |

**Required tests.** Use xUnit. Database tests use Testcontainers and `[DatabaseFact]`. Cross-module chains go in
`AlMotaheda.Integration.Tests`.
- **Finance:** every row of the §6 table; no second debit of the Accountant; a funding request ≠ a transfer; final balance 0; duplicate and concurrent approvals post once; unauthorized roles refused; a 5,000 transfer from 100,000 leaves 95,000 only after it is received, and cannot be posted twice.
- **Fuel:** the assigned driver can submit; an unassigned vehicle is refused; images and amount are enforced; pending stays visible; approval posts once; rejection keeps the unresolved item; refer-to-hr creates a case.
- **GPS:** history kept and retrieved by local day; unauthorized access refused; `NotConfigured` returns no points. Use a fake provider **only inside tests**.
- **HR and payroll:** approved overtime reaches the sheet and unapproved overtime does not; an event creates a case; a case moves no money; a deduction beyond the configured policy, or unverified, is refused; a duplicate input is not paid; a locked sheet is not paid twice; salary is hidden without `hr:F`; an employee sees only their own requests.
- **Workforce and incentives:** the sheet links to the correct project, workers and items; existing workers are reused; settlements reconcile with custody; a saving uses the date-valid reference and accepted quantities; an incentive is not payable without approval.
- **Approval and audit:** a rejected price leaves 1,300 effective; a pending price doesn't affect checks; audit rows can't be updated or deleted (assert against the database); a direct API call can't skip approval; a custody debit doesn't create a `PayrollInput`.

**Commands.** This machine has 8 GB of RAM and Docker runs one container at a time. Use the low-memory sequence
from `docs/qa/README.md`:

```bash
dotnet build AlMotahedaERP.slnx
```

```bash
for p in tests/AlMotaheda.Api.Tests tests/AlMotaheda.Integration.Tests tests/Modules/*.Tests; do dotnet test "$p" --no-build -m:1; done
```

If Docker isn't running, the database tests are reported as **skipped**. Say so; never count them as passed.

---

## 10. Final report

Write it to `docs/project-workspace/12-custody-fleet-hr-delivery.md` and summarize it in chat:

1. **Audit result per feature:** working / incomplete / defective / missing, and what you did about each.
2. **Changes:** files, entities, endpoints, migrations (per module, in apply order) and tests.
3. **The §6 table with real values** from the test run, and how double debits are prevented (locks, row versions, idempotency keys, unique movement keys).
4. **The Accountant's limits:** which actions are gated and where the backend enforces each one.
5. **Price changes:** before approval, after approval and after rejection.
6. **The central custody page:** how each balance reconciles to the books.
7. **Adjustments vs payroll deductions:** how they differ.
8. **Audit protection:** how audit rows are protected, and how to query them.
9. **HR and payroll:** what works, which decisions need a human, legal sources used, and open legal questions.
10. **GPS:** whether it works (it won't without a provider), and exactly what hardware, provider and credentials are needed to activate it.
11. **Commands run, with their real output:** pass, fail and skip counts, plus the baseline failures from phase 1.
12. **Remaining items:** the D1–D7 defaults used, Flutter screens still needed, the office-branch status and anything unresolved.
