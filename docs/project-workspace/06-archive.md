# Archive module: project documents

Module `AlMotaheda.Modules.Archive` (it was an empty shell), schema `docs`, permission module `projects`.
Read `00-overview.md` first.

## 1. Tables

`Document`: `ProjectId` (FK to `projects.Project`), `Category`, `Title` nvarchar(200), `Description`
nvarchar(1000) null, `DocumentDate` date null, `FileName` nvarchar(255) (the original name, cleaned),
`ContentType` varchar(100) (decided by the server), `Kind` (`Pdf`, `Image`, `Video`, `Office`, `Drawing`),
`SizeBytes` bigint, `Sha256` char(64), `StorageKey` varchar(200) unique, `UploadedAt`, `UploadedBy`,
`DeletedAt`, `DeletedBy`, `RowVersion`.
Indexes: `(ProjectId, Category, UploadedAt)` and `(ProjectId, UploadedAt)`, both filtered on `DeletedAt IS NULL`.

Categories: `PaymentCertificate`, `MeetingMinutes`, `SitePhoto`, `SiteVideo`, `ProgressReport`, `Contract`,
`Drawing`, `Invoice`, `Receipt`, `Official`, `Other`.

`DocumentLink` (optional link from a document to a record of another module): `DocumentId` FK, `EntityType`
varchar(40), `EntityId` int. Unique `(DocumentId, EntityType, EntityId)`; index `(EntityType, EntityId)`.
Entity types: `Expense`, `SupplierPayment`, `DailyReport`, `ReportRequest`, `LaborSheet`, `SubContract`,
`SubPayment`, `Equipment`, `EquipmentCost`, `Budget`.
This is the ERD's generic attachment link: there is no foreign key because the record belongs to another module.
A document never duplicates an accounting record. It only points to it.

## 2. Storage

- The database keeps metadata and the SHA-256 hash. The bytes live in a file store behind the internal interface
  `IFileStore` (`SaveAsync`, `OpenReadAsync`, `DeleteAsync`). It is an interface because storage is an external
  boundary that tests replace and that a cloud store can replace later.
- `LocalDiskFileStore`: root folder from `Archive:Storage:RootPath` (checked at startup: it must exist or be
  creatable, and be writable). Files are saved as `{projectId}/{yyyy}/{MM}/{guid}` with no extension, outside any
  folder the web server serves. An upload is written to a temporary name and renamed when complete.
- Uploads are streamed straight to the store while the hash and the size are computed. A file is never held in
  memory, and never cached.
- If saving the database row fails, the stored file is deleted. A health check `archive-storage` reports the
  store.

## 3. Rules

- Limits (`Archive:Limits`): images 25 MB, PDF / office / drawings 50 MB, video 500 MB. The request size limit
  of the upload endpoint is raised to the video limit; everything else keeps the server default.
- The type is detected from the file's first bytes, never from the client's content type:
  PDF, JPEG, PNG, WebP, HEIC / HEIF, MP4, MOV, DOCX, XLSX, DWG. The file extension must agree.
  Anything else answers 400 `file_type_not_allowed`. An empty file answers 400 `file_empty`.
- The file name is reduced to a safe base name (no path, no control characters) and is only used for display and
  for the download header.
- Preview (`disposition=inline`) is allowed for PDF, images and video only, with `X-Content-Type-Options: nosniff`
  and `Content-Security-Policy: sandbox`. Other kinds always download. Range requests work, so video can seek.
- Responses with file bytes are `Cache-Control: private, no-store`.
- Photos from a phone camera are ordinary uploads (`multipart/form-data`). The Flutter app takes the picture and
  posts it; nothing is special on the server.
- Delete is a soft delete: the row is hidden, the file is kept.

## 4. Who may do what

| Action | Needs |
|---|---|
| List, read metadata, preview, download | `projects` View, project visible |
| Upload | `projects` Create or Edit, project visible, project not completed |
| Edit title, description, category, date, links | the uploader, or `projects` Edit |
| Delete | the uploader, or `projects` Edit |

A document of a project the caller can't see answers 404 on every endpoint, including the file bytes.

## 5. Endpoints

| Endpoint | Notes |
|---|---|
| `GET /projects/{pid}/documents` | `category`, `kind`, `search` (title, description, file name), `from`, `to` (upload date), `uploadedBy`, `entityType` + `entityId`, paging, newest first. Also returns counts per category |
| `GET /projects/{pid}/documents/{id}` | Metadata and links |
| `GET /projects/{pid}/documents/{id}/content` | The file. `disposition=inline` or `attachment` (default) |
| `POST /projects/{pid}/documents` | `multipart/form-data`: `file`, `title`, `category`, `description?`, `documentDate?`, `entityType?`, `entityId?` |
| `PUT /projects/{pid}/documents/{id}` | Metadata; `rowVersion` |
| `PUT /projects/{pid}/documents/{id}/links` | Replace the links: `{ links: [{ entityType, entityId }], rowVersion }` |
| `DELETE /projects/{pid}/documents/{id}` | Soft delete |

## 6. Error codes

`file_required`, `file_empty`, `file_too_large`, `file_type_not_allowed`, `file_extension_mismatch`,
`project_completed`, `storage_unavailable`.

## 7. Tests that must exist

- Type detection for every allowed type, and refusal of an executable renamed to `.pdf`.
- Size limits per kind; file name cleaning (path traversal names).
- `LocalDiskFileStore` against a temporary folder: save, open, delete; the hash and the size are right; a file
  that is too large is removed.
- Upload, list, download round trip, and "a failed save leaves no file" (SQL Server test).
- 401, 403, and 404 for an invisible project on list, metadata **and** content.
