# Archive module

The digital archive of each construction project: payment certificates, meeting minutes, site photos and videos,
progress reports, contracts, drawings, invoices, receipts and official papers. The design is
`docs/project-workspace/06-archive.md`; decision A1 (`07-decisions.md`) makes it use the `projects` permissions.

## What it does

- **Upload** a file to a project, from a computer or a phone (a photo taken with the phone camera is an ordinary
  `multipart/form-data` upload). One document is one file.
- **Organise** each document with a category, a title, an optional description and document date, and optional
  links to records of other modules (an expense, a daily report, a subcontract, ...). A link only points to the
  record; it never copies it. The record must be on the same project: the module that owns it is asked
  (`IProjectRecordOwnership`), and a record of another project, or one that doesn't exist, answers 400 on that
  link's `entityId`. Links a document already has are kept as they are when the list is replaced.
- **Find** documents by category, kind, text (title, description, file name), upload date, uploader or linked record.
- **Download** any document; **preview** PDF, images and video in the browser. Video can seek (range requests).
- **Edit** the metadata and the links; **delete** (soft: the row is hidden, the file is kept).

## Storage

- The database keeps the metadata and the SHA-256 hash. The bytes are in a file store behind `IFileStore`
  (`SaveAsync`, `OpenReadAsync`, `DeleteAsync`, `ProbeAsync`), so a cloud store can replace the disk later.
- `LocalDiskFileStore` keeps files under `Archive:Storage:RootPath` as `{projectId}/{yyyy}/{MM}/{guid}`, with no
  extension. A file is written to a temporary name and renamed when complete, and flushed to the disk before the
  database row is saved.
- Uploads are streamed: the request body is read part by part (`MultipartReader`) straight into the store, while
  the size and the hash are computed. A file is never held in memory, never buffered by the framework, and never
  cached.
- If anything fails after the file is stored (invalid fields, a failed database save, a dropped connection), the
  stored file is removed.
- The health check `archive-storage` (on `/health`) writes and removes a tiny file in the folder.

## File rules

| Rule | Detail |
|---|---|
| Accepted types | PDF, JPEG, PNG, WebP, HEIC/HEIF (phone photos), MP4, MOV (phone videos), DOCX, XLSX, DWG |
| Type detection | From the first bytes of the file, never from the content type or name the client sends. DOCX and XLSX are told apart by reading the ZIP table of contents (never extracting it) |
| Extension | Must agree with the content (`photo.png` holding a JPEG is refused). A name without an extension (some phones send `blob`) gets the right one added |
| Size limits | Images 25 MB, PDF / office / drawings 50 MB, video 500 MB (`Archive:Limits`) |
| Request size | Raised to the video limit + 1 MB on the upload endpoint only; every other endpoint keeps the server default |
| File name | Reduced to a safe base name: no folders, no control characters, no direction marks, at most 255 characters. Only shown and used in the download header |
| Preview | `disposition=inline` for PDF, images and video only. Office files and drawings always download |
| Response headers | `Cache-Control: private, no-store`, `X-Content-Type-Options: nosniff`, `Content-Security-Policy: sandbox`, `Accept-Ranges: bytes`, an `ETag` (the hash) |

## Who may do what

| Action | Needs |
|---|---|
| List, read metadata, preview, download | projects View, project visible |
| Upload | projects Create or Edit, project visible, project not completed (an Executive may) |
| Edit metadata, replace links, delete | the uploader (while they still have projects Create or Edit), or projects Edit; project not completed |

A project the caller can't see answers 404 on every endpoint, the file bytes included, exactly like a project that
doesn't exist. A document of another project, or a deleted one, also answers 404. The scope is asked from the
Projects module on every request (`IProjectAccess`); it is never read from the token.

## Endpoints

| Endpoint | Who may call |
|---|---|
| `GET /api/projects/{pid}/documents` | projects View |
| `GET /api/projects/{pid}/documents/{id}` | projects View |
| `GET /api/projects/{pid}/documents/{id}/content?disposition=inline\|attachment` | projects View |
| `POST /api/projects/{pid}/documents` (`multipart/form-data`) | projects Create or Edit |
| `PUT /api/projects/{pid}/documents/{id}` | the uploader, or projects Edit |
| `PUT /api/projects/{pid}/documents/{id}/links` | the uploader, or projects Edit |
| `DELETE /api/projects/{pid}/documents/{id}` | the uploader, or projects Edit |

**Upload form:** `file`, `title`, `category`, and optionally `description`, `documentDate` (`yyyy-MM-dd`, not in the
future), `entityType` and `entityId` (both or neither). Parts may come in any order; sending the text fields first
lets a refused form fail before the file is sent in full.

**List parameters:** `category`, `kind` (`Pdf`, `Image`, `Video`, `Office`, `Drawing`), `search`, `from`, `to`
(upload date at the company, `Projects:TimeZone`), `uploadedBy`, `entityType` with `entityId`, `page`, `pageSize`
(at most 100). Newest first. The response has `items`, `page`, `pageSize`, `totalCount` and `categoryCounts` (each
category with documents in the whole project, whatever the filters).

**Edit:** `{ title, category, description, documentDate, rowVersion }`. **Links:**
`{ links: [{ entityType, entityId }], rowVersion }`; the list replaces the old one, `[]` removes every link.

Categories: `PaymentCertificate`, `MeetingMinutes`, `SitePhoto`, `SiteVideo`, `ProgressReport`, `Contract`,
`Drawing`, `Invoice`, `Receipt`, `Official`, `Other`. Link types: `Expense`, `SupplierPayment`, `DailyReport`,
`ReportRequest`, `LaborSheet`, `SubContract`, `SubPayment`, `Equipment`, `EquipmentCost`, `Budget`.
Names are accepted in any letter case.

Each document carries `abilities` (`edit`, `delete`, `preview`) so the frontend shows only the buttons that will work.

## Errors

| Status | Code | When |
|---|---|---|
| 400 | `validation_failed` | Field errors, all together (a missing file is listed as `file` when other fields fail too) |
| 400 | `file_required` | No file part, or the body is not `multipart/form-data` |
| 400 | `file_empty` | The file has no bytes |
| 400 | `file_type_not_allowed` | The content is not one of the accepted types (for example a program renamed to `.pdf`) |
| 400 | `file_extension_mismatch` | The name's extension doesn't match the content |
| 403 | `forbidden` | Missing permission, or not the uploader and no projects Edit |
| 404 | `not_found` | The project is outside your scope or doesn't exist; the document isn't on this project or was deleted |
| 409 | `project_completed`, `concurrency_conflict` | Completed project (not an Executive); stale `rowVersion` |
| 413 | `file_too_large` | Larger than the limit of its kind |
| 503 | `storage_unavailable` | The file store can't be written or read, or a stored file is missing |

File errors list their message under `errors.file`, like a field error.

## Tables (schema `docs`)

- `Document`: project, category, title, description, document date, file name, content type, kind, size, SHA-256,
  storage key (unique), uploaded at / by, deleted at / by, row version. Indexes `(ProjectId, Category, UploadedAt)`
  and `(ProjectId, UploadedAt)`, both filtered on `DeletedAt IS NULL`. Checks: category, kind, size > 0, hash is 64
  lower-case hex characters, deleted at and by together.
- `DocumentLink`: document, entity type, entity id. Unique `(DocumentId, EntityType, EntityId)`; index
  `(EntityType, EntityId)`. No foreign key to the linked record: it belongs to another module.
- Migration `InitialArchive` (`Data/Migrations`). Hand-written foreign keys in it: `Document.ProjectId` to `projects.Project`, `Document.UploadedBy` and
  `Document.DeletedBy` to `access.User`. **Apply the Access and Projects migrations before the Archive migrations.**

## Configuration

| Setting | Default | Notes |
|---|---|---|
| `Archive:Storage:RootPath` | `App_Data/archive` under the content root, **except in Production, where it is required** | Checked at startup: must exist or be creatable, and be writable. Use a folder outside the application folder, on backed-up storage. With two or more servers it must be shared storage |
| `Archive:Limits:ImageMaxMegabytes` | `25` | 1 to 10240 |
| `Archive:Limits:DocumentMaxMegabytes` | `50` | PDF, office files, drawings |
| `Archive:Limits:VideoMaxMegabytes` | `500` | Also sets the upload endpoint's request size limit |

Behind IIS, also raise `maxAllowedContentLength` in `web.config` (IIS refuses bodies over 30 MB by default); behind
nginx, `client_max_body_size`. Kestrel needs nothing: the endpoint raises its own limit.

## Known limits (accepted)

- A document's file can't be replaced; delete it and upload again. Deleted files stay on the disk (soft delete);
  there is no purge job yet.
- There is no virus scan. The type check, `nosniff` and the `sandbox` policy stop a file from running in the
  browser, but a harmful PDF or Office file can still be downloaded and opened on a computer.
- The OpenAPI document does not describe the upload form's fields: declaring `multipart/form-data` on the endpoint
  would make routing answer a bare 415 to any other body instead of the 400 `file_required` the frontend expects.
  The form is described here.
- Some browsers' built-in PDF viewers may refuse to show a PDF served with `Content-Security-Policy: sandbox`. If
  that happens in the target browsers, the frontend can download the bytes (`disposition=attachment`) and show them
  with its own viewer.
- Until the platform AuditLog table exists, the audit trail is the structured log (`ArchiveLog`, event ids 6001 to
  6301): who uploaded, changed, linked or deleted which document, by id; never titles or file names.

## Code layout

- `Features/Documents`: `ListDocuments`, `GetDocument`, `GetDocumentContent`, `UploadDocument` (with `UploadForm`,
  the streaming multipart reader), `UpdateDocument`, `ReplaceDocumentLinks`, `DeleteDocument`, the shared field rules
  (`DocumentFields`, `LinkInput`), queries (`DocumentQueries`) and views (`DocumentView`).
- `Files`: type detection (`FileTypes`) and file name cleaning (`FileNames`). Pure, unit tested.
- `Storage`: `IFileStore`, `LocalDiskFileStore`, the options and their startup checks, the health check.
- `Security`: the caller and the permission rules (`ArchiveCaller`).
- `Common`: validation, error responses, the audit log, company dates.
- `Data`: entities, `ArchiveDbContext`, table configuration and migrations.
- `Startup`: applies migrations when enabled.
