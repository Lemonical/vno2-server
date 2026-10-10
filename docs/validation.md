# Split verification — 25 October 2026

Windows x64, .NET SDK 10.0.401: locked restore, Release build and formatting/analyzers
passed against Shared 0.1.0 as a NuGet package. All ten server TUnit tests passed
without failures/skips. The built DLL ran directly because sandboxed orchestration
blocked IPC. The server tests include authority, durable store/ticket validation,
actor limits and omitted broadcast request IDs without a client project dependency.

The real WebSocket smoke passed authentication/authority, fragmented JSON, privacy,
retry deduplication, malformed handshake, moderation, media approval/revocation,
catalog reservations, capacity, schema, timers and restart durability. The optional
workspace identity/community regression passed all 19 checks separately on SQLite
and PostgreSQL masters. Normal local network access was needed for the probes.

The Dockerfile built on Docker Desktop's Linux engine. A non-root server container
authenticated through master tickets, persisted credits and a media catalog change
in its named SQLite volume, and retained them after restart. Guest authentication
remained disabled; Development was explicitly used only to permit a loopback HTTP
master within the disposable test's shared network namespace. Runtime images had
no source checkout or NuGet.Config. Test containers and volumes were removed.

Docker validation used a local Shared package, not authenticated GitHub Packages.
Remote CI, private-feed access, registry publication, production TLS/reverse proxy,
load and backup-restoration checks remain unrun. See [container hosting](container.md).
