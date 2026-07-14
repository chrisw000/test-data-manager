# TDM compatibility matrix

The TDM host ships its own EF Core provider assemblies, version-aligned to the org EF
baseline (handoff §3). A domain data package built against a different EF major/minor is
rejected at plugin load with an *EF version skew* error that links here.

| TDM version | Host runtime | EF Core baseline | Providers shipped | `Tdm.Identity` targets |
|---|---|---|---|---|
| 0.1.x | .NET 10 | 8.0.x (pinned 8.0.28) | Sqlite, SqlServer | netstandard2.0, net10.0 |

## Rules

- **Domain packages** must reference `Microsoft.EntityFrameworkCore` at the same
  major/minor as the TDM host's baseline (patch differences are tolerated). Rebuild the
  domain data package against the org baseline, or move to a TDM version that ships your
  EF line.
- **`Tdm.Identity` is contract-frozen** (SemVer): any change to the UUIDv5 derivation is a
  **major** version bump across every TDM package. The identity contract version is
  recorded in each run manifest, so historic manifests remain interpretable.
- The `tdmVersion` recorded in every manifest is the host's
  `AssemblyInformationalVersion`, stamped from the release tag (`vX.Y.Z` →
  `-p:TdmVersion=X.Y.Z`).

## Distributions

| Channel | Artifact | Install / use |
|---|---|---|
| dotnet tool | `Tdm.Tool` | `dotnet tool install --global Tdm.Tool` → `tdm validate ...` |
| Container | `ghcr.io/chrisw000/tdm:{version}` | `docker run --rm -v $PWD:/work -w /work ghcr.io/chrisw000/tdm:{version} validate --settings tdm.settings.json` |
| Libraries | `Tdm.Identity`, `Tdm.Core`, `Tdm.EfCore`, `Tdm.Plugins`, `Tdm.Observability` | NuGet package references for embedding or extending TDM |
