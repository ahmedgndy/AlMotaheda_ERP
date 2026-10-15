# Permission matrix (with HR and Purchasing)

Date: 2026-10-16. Letters: V View · C Create · E Edit · A Approve · F See money. Empty = no access.
**Bold** = new or changed compared with `PermissionCatalog.RoleDefaults` today. The Executive can still change
any role's defaults on the Roles page; this table is the seed.

## 1. How the model is extended (no redesign)

- Two **new roles** appended to the fixed list: `hr` (id 7) and `purchasing` (id 8). Existing role ids don't
  change.
- Two **new permission modules** appended at the end of the module list: `hr` and `procurement`. Permission
  ids are `moduleIndex * 5 + action + 1`, so appending keeps every existing id. One seed migration.
- Salary visibility = `hr:F`. Only HR, Accountant and Executive get it.
- Project scope (who sees which project) is unchanged. Executive and Accountant see every project. Others see
  their projects. HR and Purchasing get **no** project access: they see their own module's records company-wide,
  never project financials.

## 2. Role defaults

| Module | Executive | Manager | Engineer | Office admin | Accountant | Staff | **HR** | **Purchasing** |
|---|---|---|---|---|---|---|---|---|
| projects | VCEAF | VCEF | VC | | VAF | V | | |
| subcontractors | VCEAF | VCEF | | | VCEF | | | |
| workers | VCEAF | VCAF | VC | | VCEAF | | | |
| site | VCEAF | VCAF | VC | | VF | | | |
| fuel | VCEAF | VAF | VC | | VAF | VC | | |
| fleet | VCEAF | VCF | VC | | VAF | V | | |
| equipment | VCEAF | VEA | V | | VAF | V | | |
| office | VCEAF | | | VCE | VCEAF | | | |
| custody | VCEAF | V | VC | VC | VAF | VC | | |
| income | VCEAF | V | | | VCEF | | | |
| catalog | VCEAF | VCEF | V | | VF | | | **V** |
| reports | VCEAF | VCEA | VC | VC | VCEA | VC | **VC** | |
| monitoring | VE | V | | | | | | |
| payments | VCEAF | V | | V | VCF | | | |
| finance | VCEAF | | | | VCEAF | | | |
| accounts | VCEAF | | | | V | | | |
| settings | VCEAF | | | | | | | |
| **hr** | **VCEAF** | | | | **VAF** | | **VCEF** | |
| **procurement** | **VCEAF** | **VCAF** | **VC** | | **VAF** | | | **VCEF** |

## 3. What each letter means in the new modules

### hr

| Action | Allows |
|---|---|
| V | See employees, documents metadata, salary sheets (without amounts unless F) |
| C | Create employees, documents, bonus / deduction / advance inputs, a monthly salary sheet |
| E | Edit employee master data, salary structure (new version), a salary sheet while Draft or Returned; submit it for review |
| A | **Financial review** of a submitted salary sheet: approve or return with a reason. Approving lets the Accountant create the salary payment request |
| F | See salaries and amounts |

Rules: HR has no `payments` or `finance` access, so it **cannot execute payments** [Company]. The Accountant
has `hr:VAF` and no C/E, so it **cannot edit employee master data**, only review [Company]. The person who
prepared a sheet can't approve it (same "own record" rule as every approval) [Industry]. The Executive decides
the salary **payment request** in Finance (`payments:A`), unchanged flow [Company].

### procurement

| Action | Allows |
|---|---|
| V | See procurement documents (scope below) |
| C | Create a purchase requisition (PR) for a project the user can see |
| E | Purchasing work: receive approved PRs, quotations, recommend, create / revise POs, manage supplier records, track deliveries |
| A | Approve a PR when `Procurement:AutoApproveRequisitions` is off (Project Manager first, then Accountant) [Company]. Never one's own PR [Company] |
| F | See prices and amounts |

Done by checks in the slice, not by a letter:

- **Request quotations, select a quotation, return a set to Purchasing**: Executive only, re-checked in the DB [Company].
- **Change the auto-approval policy**: Executive only, audit-logged [Company].
- **Goods receipt (GRN)**: `site:C` + member of that project (the receiving engineer or project manager). Purchasing
  has no `site` access, so it can't record receipts [Company].
- **Record a supplier invoice, resolve a 3-way-match mismatch**: `finance:C` / `finance:A` (Accountant) [Company].
- **Payment request** for a matched invoice: `payments:C` (Accountant) → Executive (`payments:A`) [Company].
- Purchasing sees **every** procurement document company-wide (it serves all projects) but has no `projects`,
  `payments`, `finance`, `hr` access: no salaries, payroll or company financials [Company].
- Engineers and managers see procurement documents only for their projects.

### payments and finance (Finance module)

| Who | Can |
|---|---|
| Accountant (`payments:C`, `finance:VCEAF`) | Create payment requests, send approved requests to the bank, record bank confirmation / failure, invoices, reconciliation, ledger |
| Executive (`payments:A`) | Reject / return / approve / approve & pay payment requests |
| Manager, Office admin (`payments:V`) | See payment requests of what they can see (no amounts without F) |

## 4. Supplier records

Today `projects:C/E`. After: `projects:C/E` **or** `procurement:E` (one global `Supplier` table, owned by
Projects) [Company].

## 5. Navigation contract (what the frontend consumes)

Unchanged mechanism: `GET /api/me` returns the role code and the effective permissions; the app shows a section
when the user has View on its module. New entries:

| Navigation entry | Shown when | Roles by default |
|---|---|---|
| HR → Employees | `hr:V` | Executive, Accountant, HR |
| HR → Salary sheets | `hr:V` (amounts with `hr:F`) | Executive, Accountant, HR |
| Procurement → Requisitions, Quotations, Purchase orders, Receipts | `procurement:V` | Executive, Manager, Engineer (own projects), Accountant, Purchasing |
| Finance → Payment requests | `payments:V` | Executive, Manager, Office admin, Accountant |
| Finance → Ledger, Reconciliation, Supplier invoices | `finance:V` | Executive, Accountant |
| Reports centre | `reports:V`; each report also lists its allowed roles | per report (Phase 4 catalog) |
| Settings → Backup, Procurement policy | Executive | Executive |
| Notifications bell | signed in | everyone |

## 6. Migration of existing data and permissions

- No employee data exists today, so nothing moves out of the Accountant. HR starts empty; HR staff are added as
  employees and, if they sign in, as users with role `hr`.
- Existing users keep their roles. Per-user overrides are untouched. New modules start with no overrides.
- The seed migration only **adds** rows (2 roles, 10 permissions, role-permission rows). It is reversible.
