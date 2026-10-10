# Build and verification

Run commands from this repository's root with the .NET 10 SDK selected by
`global.json`. No sibling checkout or parent build configuration is required.
See [private package setup](packages.md) before restoring.

```sh
dotnet restore VNO2.slnx --locked-mode --configfile NuGet.Config
dotnet build VNO2.slnx -c Release --no-restore
dotnet test --solution VNO2.slnx -c Release --no-build
dotnet format VNO2.slnx --verify-no-changes --no-restore
```

Tests use TUnit with Microsoft.Testing.Platform. If the CLI orchestrator is blocked
by local named-pipe restrictions, the built test DLL can be executed directly;
record that narrower invocation. Application publish commands use
`-p:Vno2Publish=true` to keep RID-specific lock files under ignored `obj/` directories.
Normal lock files remain committed. Build output belongs under `artifacts/`.

`.github/workflows/build.yml` validates this repository and uploads unsigned build
artifacts. GitHub Actions must run successfully before deployment is enabled.
Native desktop behavior and actual hosting require their own validation.
