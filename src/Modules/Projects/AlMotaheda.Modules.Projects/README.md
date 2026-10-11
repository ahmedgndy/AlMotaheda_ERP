# Projects module

## Purpose

Owns construction projects, the top-level containers of the digital archive.

Planned responsibilities, to be detailed when requirements are provided:

- Managing construction projects.
- Deciding which users may access which project (project-level access).
  Other modules ask this module for that decision. They never read its data directly.

## Current state

Only the module shell exists: `ProjectsModule` is registered in the API host. There are no features, entities, or endpoints yet.

## Layout (as features are added)

```
AlMotaheda.Modules.Projects/
├── ProjectsModule.cs        # Module entry point (the only public type)
├── Features/                # One folder per use case (vertical slice)
│   └── <UseCase>/
│       └── <UseCase>.cs     # Endpoint + request/response + validation + logic
└── Data/                    # Module-owned persistence (added with the database phase)
```

If another module needs something from this module, create
`AlMotaheda.Modules.Projects.Contracts` next to this project. Put only the public interfaces and DTOs there.

See `docs/architecture.md` for the full rules.
