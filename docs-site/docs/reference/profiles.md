# Convention profiles

A convention profile tells TDM how a domain's codebase is shaped: how entity classes are
named, where the repositories are, what the fakers are called. Two profiles are built in;
custom profiles are plain settings entries.

## Built-in: `modern`

| Setting | Value |
|---|---|
| Entity class pattern | `{Name}Entity` |
| Entity namespace suffix (fallback scan only) | `Data.Persistence.Domain` |
| Write repository probe order | `I{Name}WriteRepository`, `I{Name}Repository` |
| Read repository probe order | `I{Name}ReadRepository`, `I{Name}Repository` |
| Write repository required (ADR-0001) | **yes** |
| Faker pattern | `{Name}Faker` |
| Natural key default | `Name` |

## Built-in: `legacy`

| Setting | Value |
|---|---|
| Entity class pattern | `{Name}Model` |
| Entity namespace suffix (fallback scan only) | `Data.Infrastructure` |
| Repository probe order (write & read) | `I{Name}Repository` |
| Write repository required | no |
| Faker pattern | `{Name}Faker` |
| Natural key default | `Name` |

## How resolution actually works

1. **EF-model-first**: entities come from `DbContext.Model` — the compiled output of your
   `IEntityTypeConfiguration<T>` classes. Namespaces don't matter on this path; the class
   pattern only strips the suffix to produce the logical name (`CustomerEntity` → `Customer`).
2. **Configuration cross-check**: a type with an `IEntityTypeConfiguration<T>` that is *not*
   in any context model (a missed `ApplyConfiguration`) is discovered and warned about.
3. **Namespace fallback scan**: last resort for unmapped, convention-named types —
   generation only.

Repositories are probed by the pattern lists in order; the first interface with a concrete
implementation wins. Persist methods are matched via the well-known generics
(`IRepository<T>`, `IWriteRepository<T>`, `IRepositoryWrite<T>`) first, then duck-typed
names (`Add`, `AddAsync`, `Add{Name}`, `Insert`, `Create`, …) with a single entity-typed
parameter. Reads (verification, natural-key lookup) always go through the DbContext —
see the [decision log](decisions.md) for why.

## Custom profile

```jsonc
"conventionProfiles": {
  "acme-services": {
    "entityClassPattern": "{Name}Record",
    "entityNamespaceSuffix": "Storage.Records",
    "writeRepositoryPatterns": ["I{Name}Store", "I{Name}Repository"],
    "readRepositoryPatterns": ["I{Name}Query"],
    "requireWriteRepository": true,
    "fakerPattern": "{Name}Builder",
    "naturalKeyDefault": "Code",
    "addMethodNames": ["Save", "SaveAsync", "Add"],
    "updateMethodNames": ["Save", "SaveAsync"],
    "deleteMethodNames": ["Remove", "RemoveAsync"]
  }
},
"domains": [{ "name": "Services", "conventionProfile": "acme-services" }]
```

The single `repositoryPattern` key from earlier versions still works — it is prepended to
both probe lists.

Check what a profile resolved with `tdm list-entities` — it prints entity → CLR type,
key info, natural key, faker, persist route and read repository per domain.
