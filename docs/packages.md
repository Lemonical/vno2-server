# Private Shared package

`VNO2.Shared` is an exact-version NuGet dependency. There is no sibling-project
fallback. `NuGet.Config` maps that package exclusively to the `vno2` source and
maps other packages to nuget.org. Set `VNO2_SHARED_FEED` to either an absolute
local package directory or `https://nuget.pkg.github.com/OWNER/index.json`.

For local development in the complete VNO2 workspace, `python tools/bootstrap.py`
at the workspace root builds the package and all four repositories. For an
independent checkout, obtain the pinned `.nupkg` from its owner, set the feed
variable, and follow [build instructions](build.md).

PowerShell local-feed example:

```powershell
$env:VNO2_SHARED_FEED = 'C:/packages/vno2'
dotnet restore VNO2.slnx --locked-mode --configfile NuGet.Config
```

On Bash, use `export VNO2_SHARED_FEED=/absolute/package/directory`.
For GitHub Packages, set `NuGetPackageSourceCredentials_vno2` to
`Username=YOUR_LOGIN;Password=YOUR_TOKEN;ValidAuthenticationTypes=Basic` using
your shell's secret-entry facility or a CI secret. Never put a token in Git,
Docker build arguments, or a published image. Local users need a classic PAT
with `read:packages` and account access to the package.

CI defaults to the repository owner's GitHub Packages feed. Set the repository
variable `VNO2_PACKAGE_OWNER` if the package belongs to a different owner.
Grant this repository Read access in the package's **Manage Actions access**
settings; its workflow then uses `GITHUB_TOKEN` with `packages: read`.
Publish the pinned package before running consumer CI. Private dependencies
require explicit access for outside contributors and forks; do not work around
that restriction with `pull_request_target` or exposed credentials.

Package versions are immutable. After publishing a new version, change
`Directory.Packages.props`, restore without `--locked-mode`, review all lock
changes, and rerun tests. Commit the resulting locks. The feed's package bytes
must match their recorded content hashes; do not repack an existing version.

References: [GitHub NuGet registry](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-nuget-registry),
[NuGet authenticated feeds](https://learn.microsoft.com/en-us/nuget/consume-packages/consuming-packages-authenticated-feeds).
