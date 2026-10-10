# Community container

Build from this repository root. Configure private package access as described in
[packages](packages.md). Use BuildKit secrets for authentication:

```sh
docker build -f deployment/Dockerfile -t vno2-server:local \
  --build-arg VNO2_SHARED_FEED=https://nuget.pkg.github.com/OWNER/index.json \
  --secret id=vno2_nuget_credentials,env=NuGetPackageSourceCredentials_vno2 .
```

For an offline Shared feed, place the approved `VNO2.Shared.0.1.0.nupkg` under
`artifacts/packages/` and set the build argument to `/src/artifacts/packages`.
Other NuGet dependencies still need a cache or network access. The Docker context
allows only package archives from artifacts; credentials and databases are excluded.
Never put credentials in build arguments. The final image contains only published
files and runs as the .NET image's non-root user.

Register the server with a master first, then set `COMMUNITY_ID`, `MASTER_BASE_URL`
and `SERVER_CREDENTIAL` privately in your shell or an ignored `.env` file. Copy the
non-secret template in `deployment/.env.example` if useful. Run:

```sh
docker compose up -d
```

Compose requires those settings, defaults to Production, binds only
`127.0.0.1:8080`, and persists SQLite in the named `community-data` volume at `/data`.
Put a configured TLS reverse proxy in front of it for public use. `/health` is a
process check; verify master authentication separately. Do not enable guest mode
for public traffic. Guest authentication only accepts actual loopback clients;
port publishing does not turn a Docker bridge client into a loopback client.

One process owns each database. Use `docker compose down` to stop while preserving
the volume. `docker compose down -v` destroys it. Stop writes before copying the
whole SQLite database and any WAL files, or use SQLite's backup API, and test restore.
Do not store live community data in the image or rebuild it to edit a catalog.

Characters, music and items are bounded catalog records changed through the
authenticated server commands described in [the runbook](server.md). Those records,
rooms, roles and state persist in SQLite. Image/audio bytes remain client-local;
environment variables hold service configuration and secrets, not asset libraries.
An optional read-only `/app/appsettings.Production.json` mount can hold ordinary
ASP.NET Core `Community` settings. Environment variables override that file. Keep
secrets private, and back up the configuration separately from the data volume.

The build workflow verifies this image without publishing it. Deployment, TLS,
backup scheduling and remote container registry publication remain operator steps.
