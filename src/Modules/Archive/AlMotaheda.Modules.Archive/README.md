# Archive module

## Purpose

Owns the digital archive of each construction project: the documents and media stored for a project.
Examples: payment certificates, meeting minutes, site photos and videos, progress reports, contracts, invoices, and technical drawings.

Planned responsibilities, to be detailed when requirements are provided:

- Document categories.
- Uploading, storing, downloading, and previewing files.
- File metadata: project, category, uploader, upload date, description.
- Searching and filtering documents.

Access checks ("may this user see this project's documents?") will be answered by the Projects module
through its future `Contracts` project. This module will not read Projects data directly.

## Current state

Only the module shell exists: `ArchiveModule` is registered in the API host. There are no features, entities, storage, or endpoints yet.

## Layout (as features are added)

```
AlMotaheda.Modules.Archive/
├── ArchiveModule.cs         # Module entry point (the only public type)
├── Features/                # One folder per use case (vertical slice)
│   └── <UseCase>/
│       └── <UseCase>.cs     # Endpoint + request/response + validation + logic
└── Data/                    # Module-owned persistence (added with the database phase)
```

See `docs/architecture.md` for the full rules.
