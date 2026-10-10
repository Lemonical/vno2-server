# Contributing to vno2-server

Use the .NET SDK pinned in `global.json` and follow [build instructions](docs/build.md).
Keep changes focused, preserve validation and error handling, and test the behavior
affected by each change. Dependency versions live in `Directory.Packages.props`;
regenerate and review lock files when intentionally changing dependencies.

Do not commit credentials, local runtime data, caches, or build output. Wire changes
must remain compatible with the independently released VNO2 applications. Include
the relevant test results and limits in change descriptions. No project source
license has been selected yet.
