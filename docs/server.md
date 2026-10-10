# Authoritative community server

`src/VNO2.Server` is an ASP.NET Core 10 community host with a SQLite state store and a versioned JSON WebSocket protocol. The pinned private `VNO2.Shared` package supplies dependency-free wire contracts. The server never loads or serves character assets; clients resolve server-approved references against their raw local directories.

## Run locally

From the repository root, using the pinned .NET SDK:

```sh
dotnet run --project src/VNO2.Server --no-launch-profile -- --urls http://127.0.0.1:5167
```

This defaults to account-bound master authentication. Guest authentication is disabled by default, accepts only loopback connections, and cannot be enabled in Production. For an explicitly local demonstration:

```sh
ASPNETCORE_ENVIRONMENT=Development \
Community__AllowGuestAuthentication=true \
Community__DatabasePath=/tmp/vno2-demo.db \
dotnet run --project src/VNO2.Server --no-launch-profile -- --urls http://127.0.0.1:5167
```

The seeded world contains `lobby` and `stage`, three permitted sample characters (`mira`: `Demo/Mira`, neutral/happy; `rowan`: `Demo/Rowan`, neutral/angry; `echo`: `Demo/Echo`, neutral/talk), and a potion costing 10 credits, restoring 25 HP, with a five-second server-owned cooldown. New accounts start with 100 credits. Point the client's character library at the client repository's `samples/characters`. These are deliberate sample resources, not imported third-party artwork.

The store uses the configured path relative to the host's working directory. Use a fresh database when testing seed changes; existing state is preserved, not silently reseeded.

## Identity, production and configuration

Configuration uses the `Community` section of ASP.NET Core configuration. Environment names use double underscores:

| Setting | Default | Purpose |
|---|---|---|
| `Id` | `local-community` | Registered master server GUID in production |
| `Name` | `VNO2 Community` | Public display name |
| `DatabasePath` | `data/community.db` | Persistent SQLite database |
| `MasterBaseUrl` | empty | Explicitly pinned HTTPS master origin; development permits loopback HTTP |
| `ServerCredential` | empty | Registered server credential, supplied out of band, never in the repository |
| `AdministratorAccountIds__0` etc. | empty | Account GUIDs allowed to administer this community |
| `AllowGuestAuthentication` | false | Development-only, explicit guest mode |
| `EconomyEnabled` | true | Enable optional inventory/resources/credits/shop |
| `FreeformResourcesEnabled` | false | Let members edit only their own HP/MP and maxima |
| `StartingCredits` | 100 | New-account balance, between 0 and 1 billion |
| `MaxConnections` | 512 | Global concurrent WebSocket admission cap |
| `LocalCapacity` | 64 | Distinct-account capacity for explicit development guests, bounded to 1–10,000 |
| `ReconnectGraceSeconds` | 180 | Keep room/stage membership during a brief disconnection |
| `TrustedProxies__0` etc. | empty | Exact reverse-proxy IPs allowed to supply forwarded scheme/client IP |

Register the server through the master, configure the returned server ID and credential privately, and configure the administrator's real account ID. The community calls the pinned master's `POST /api/v1/tickets/validate` with its server credential and the client's one-use ticket. The master checks expiry, audience, account session and ticket consumption, and returns the trusted registered server capacity. Community admission enforces that capacity under the same dispatch lock as session insertion, counting distinct authenticated accounts rather than sockets. A ticket does not reserve a seat: concurrent arrivals beyond capacity receive `server_full`. Existing accounts may reconnect while full, within the four-session-per-account cap. Capacity decreases do not forcibly disconnect current participants; further distinct admission remains closed until occupancy drops. Master/community versions must both support the capacity-bearing validation response. Central account administrators do not automatically become community administrators. Redirects are disabled. A configured server also sends a discovery heartbeat every 30 seconds.

Production WebSockets require HTTPS. Run Kestrel with TLS or terminate TLS at a specifically trusted reverse proxy that sets `X-Forwarded-Proto`. Forwarded headers are ignored unless exact trusted proxy IPs are configured; only one proxy hop is accepted. Do not expose the backend directly when relying on proxy TLS. Do not use Development on a public deployment. No new accounts, remote deployment, credential generation, or production infrastructure are performed by this source implementation.

A development guest receives a cryptographically random opaque `guestToken`. Its SHA-256 hash is stored, and only that token can resume the guest account. A display name or client-supplied account ID never establishes identity. To demonstrate host tools locally, connect once, copy the returned guest account ID into `Community__AdministratorAccountIds__0`, restart, and reconnect with the same private guest token. Never use guest mode for real public accounts.

## HTTP endpoints

- `GET /info`: existing service-information contract
- `GET /health`: process health
- `GET /api/community/info`: community ID, name, protocol version and enabled features
- `GET /api/community/rooms`: public summaries with no passwords, reservation identities or isolated participant counts
- `GET /api/community/catalog`: permitted character metadata, no local files
- `GET /api/community/media`: enabled approved media definitions with ID, kind and relative local reference
- `/ws`: WebSocket upgrade; tickets and credentials are never accepted in URL query parameters

## Wire protocol

Client messages:

```json
{"version":1,"requestId":"unique-client-id","action":"room.join","data":{"roomId":"stage"}}
```

The first message must be `authenticate` with `{ "ticket": "opaque-master-ticket" }`, or in explicit guest mode `{ "guestName": "Alice" }` / `{ "guestToken": "private-resume-token" }`. A `welcome` contains `CommunityWelcome`: the authoritative account ID, display name, optional guest token and complete filtered snapshot.

Server envelopes contain `version`, `type`, optional `requestId`, monotonically increasing event `sequence`, and `data`. Types:

- `welcome`: authenticated identity and snapshot
- `ack`: `{ action, result }` for an accepted request
- `error`: `{ code, message }`, without secret data
- `event`: timestamped `CommunityEvent` containing a stable ID, sequence, kind, room, actor account and accepted data
- `snapshot`: complete `CommunitySnapshot` for this recipient

Snapshots include own role/permissions/resources/inventory, permitted catalog and shop, room summaries, `mediaCatalog` definitions, visible room participants/actors, speaker, background/music, timers and optional `currentDialogue`. The latest public room line is retained for late joining and reconnecting; restoring snapshots must reveal its text without automatically replaying sound/effects. Isolated current lines are stored separately and visible only to their original audience while current permissions still allow them; later moderator promotion does not reveal them. Stage/dialogue events include movement, displacement, expressions, scale and speaker state together. Clients interpolate presentation locally. After state-changing events each connected account receives its own filtered snapshot. Do not infer state authority from a locally available asset.

Requests are limited to 64 KiB, JSON depth 32, 128 transport fragments and 15 seconds to finish a fragmented message. Authentication has a 20-second deadline. Server messages are limited to 1 MiB. Mutations are rejected transactionally if any recipient's resulting snapshot exceeds supported collection limits or snapshots/events exceed the internal 800,000-byte budget. Each visible stage supports at most 32 actors, including isolated actors visible to moderators. A rejected change preserves state, events and retry IDs; it never evicts or hides actors to fit. Replay pages are byte-budgeted as well as count-limited. Slow receivers have a bounded outbound queue and are disconnected rather than consuming unbounded memory. Four simultaneous connections per account, 100 requests per 10 seconds per connection, ten room-entry/password attempts per minute per account, and 30 connection attempts per minute per network address bound common abuse. These are basic application safeguards, not a claim of DDoS protection.

Request IDs contain 1–80 ASCII letters, digits, `_` or `-`. Reuse the same ID and the same action/data JSON when retrying an uncertain accepted action. Accepted mutating request fingerprints, acknowledgments, state and events commit in one SQLite transaction. Read-only replay, snapshot, ping, audit, lookup, cue list and preview requests do not rewrite the world or persist bulky replies in the deduplication ledger; repeated reads return current results. Reusing an already recorded mutation ID for a different read still returns `request_conflict`. A matching retry returns the original acknowledgment without spending or broadcasting twice. Reusing an ID with changed action or differently serialized data returns `request_conflict`. Keep request serialization stable across a retry. Failed requests do not mutate state.

## Commands

All payloads are objects. IDs are stable server IDs, not display names. Optional properties are marked `?`.

| Action | Data |
|---|---|
| `snapshot.get`, `ping` | `{}` |
| `room.join` | `roomId`, `password?` |
| `room.leave` | `{}` |
| `room.configure` | Administrator: `roomId`, `name`, `description?`, `capacity?` (1–256), `locked?`, `password?` (empty clears), `entryCost?`, `minimumCredits?`, `requiredItemId?`, `allowPlayerMusic?` |
| `room.reserve` | Director: `roomId`, `accountIds` |
| `character.select` | `characterId`, `expression?` |
| `media.configure` | Administrator: `mediaId`, `name`, `kind` (`music`, `sound`, `background`, `image`, `effect`), `contentReference`, `enabled?`; media IDs have immutable kinds |
| `catalog.configure` | Administrator: `characterId`, `name`, `contentReference` (`Category/Character`), `expressions`, `defaultExpression?`, `reservedAccountId?` (omit preserves; null/empty clears) |
| `dialogue.send` | `text?`, `spans?`, `expression?`, `position?` (`left`, `center`, `right`), `scaleX?`, `scaleY?`, `sound?`, `effect?` |
| `dialogue.clear` | `{}`; member clears their own current line; director/administrator clears room dialogue; isolated member clears only their own private line |
| `chat.send` | `text`, `channel?` (`ooc` or `event`) |
| `stage.move` | `position`, `expression?`, `scaleX?` (magnitude 0.1–4; negative mirrors horizontally), `scaleY?` (0.1–4) |
| `stage.clear` | Director: `{}`; removes stage placements and clears public/private room dialogue and speaker focus |
| `shop.buy` | `itemId`, `quantity?` |
| `shop.configure` | Administrator: `itemId`, `name`, `description?`, `price?`, `hpEffect?`, `mpEffect?`, `cooldownSeconds?`, `consumable?`, `forSale?` |
| `inventory.give` | `itemId`, `quantity?`, `targetAccountId` |
| `inventory.discard`, `inventory.show` | `itemId`, `quantity?` |
| `inventory.use` | `itemId`, `quantity?` (must be 1), `targetAccountId?` |
| `inventory.grant` | Director: `itemId`, `quantity?`, `targetAccountId` |
| `credits.transfer` | `targetAccountId`, positive `amount` |
| `credits.grant` | Director: `targetAccountId`, positive `amount` |
| `resources.set` | Director or enabled freeform self-edit: `targetAccountId?`, `hp?`, `maxHp?`, `mp?`, `maxMp?` |
| `dice.roll` | `count?` (1–100), `sides?` (2–1,000,000), `modifier?` |
| `arithmetic.evaluate` | `expression`, decimal numbers and `+ - * / % ( )` only |
| `moderation.lookup` | Moderator: `query?` |
| `moderation.audit` | Moderator: `{}`; latest 100 accepted moderation actions |
| `moderation.kick`, `.ban`, `.unban`, `.mute`, `.isolate` | Moderator: `targetAccountId`, `reason?`, `enabled?` |
| `moderation.role` | Administrator: `targetAccountId`, `role` (`member`, `director`, `moderator`, `administrator`), `reason?` |
| `director.actor` | Director: `targetAccountId`, `characterId?`, `expression?`, `position?`, `scaleX?`, `scaleY?`, `speaker?`, `remove?`; target must be visible in the same room |
| `director.announce` | Director: `text` |
| `director.background`, `.music`, `.image`, `.effect` | Director: approved media ID in `reference` matching the requested kind; empty/null clears background/music; optional `loop`/`durationMilliseconds`; room permission may allow member music |
| `director.timer` | Director: `label`, `seconds` (1–86,400) |
| `director.timer.cancel` | Director: `timerId` |
| `director.cue.save` | Director: `cueId`, `name`, `background?`, `music?`, `announcement?`, `image?`, `effect?` |
| `director.cue.list` | Director: `offset?`, `limit?` (1–50) |
| `director.preview`, `director.cue.play` | Director: `cueId`; preview never applies the cue |
| `replay.get` | `afterSequence?`, `limit?` (1–500); result includes `events`, `nextSequence`, `hasMore`, `sequence` |

Room entry checks capacity, unoccupied reservations, password, locks, item requirements, minimum balance and a separately charged toll. Rejoining one's current room does not charge again. A normal player can move only their own actor, select only catalog characters/expressions, and displace one regular actor occupying the chosen stage slot. A character may have one optional reserved account; the account must exist, and both ordinary selection and director reassignment enforce the reservation. Staff have no implicit reservation bypass: an administrator must explicitly clear or change it first. Changing a reservation revokes conflicting active selections. Unreserved characters remain freeform and may be selected by several accounts. Isolated actors cannot displace ordinary actors, spend on other players, change rooms or perform privileged director, administrator or moderation mutations. This includes grants, resource edits on others, reservations and stage clearing; freeform self-edit remains subject to its explicit server option. All item ownership, quantities, balances, resource maxima and cooldowns are server-owned. Targeted transfers/use require the same visible room. All ordinary sound/effect, director music/background/image/effect, and cue references must name an enabled media-catalog ID of the matching kind. Only administrators can add/update/disable media permissions. Catalogs are bounded to 500 entries and 256 KiB. Seed IDs are `demo-music` (music, `demo.wav`), `demo-sound` (sound, `demo.wav`), `shake` (effect, `builtin:shake`) and `flash` (effect, `builtin:flash`). File references are relative to the client media root and prohibit absolute paths, traversal and URI components. Built-in effects are restricted to the two supported presets. Cue playback revalidates all permissions, so disabling a media entry also disables saved cues that refer to it. The server never downloads assets. Mutes prohibit dialogue, chat and ordinary performance actions. Moderators cannot moderate staff; configured owner accounts are protected.

Events store the exact audience account IDs at acceptance. Replay checks this stored audience; later room entry or role promotion does not retroactively reveal private history. Inventory/resource transactions are private to involved accounts. Moderation reasons are staff-only. Isolated performance is visible only to the isolated account and moderators, while ordinary players' snapshots never contain isolated actors or private moderation details. Replay data represents recorded outcomes and must never be resubmitted as live commands.

Isolation disables staff lookup/audit access, privileged visibility of other isolated accounts, and receipt of new staff-only/private events, even when the account retains a staff role. Previously accepted history retains its original audience; isolation does not revoke history already disclosed. Current private dialogue still requires both its original audience and current visibility permission.

`dialogue.clear` requires an unmuted room member. Clearing another account's public line without director authority returns `forbidden`; clearing when no applicable line exists is accepted without an event. Accepted clears emit ordered `dialogue.cleared` stage state with `detail.accountId` naming the caller and no dialogue text. Actors and resources remain intact, while the cleared dialogue's speaker focus is removed. An isolated clear preserves public room dialogue/focus and reaches only its private audience. Director clearing removes all room public/private lines and focus; `stage.clear` additionally removes stage placements and emits `stage.changed` after `dialogue.cleared`. Snapshots may reveal a remaining eligible private line after another participant clears their public line. Matching retry IDs return the original acknowledgment without clearing a newer line or emitting twice.

Catalog expression arrays contain at most 200 identifiers of 1–100 characters, without surrounding whitespace or control characters. Names must be unique ignoring case, matching local content lookup. Dialogue, movement and director actor edits all accept signed horizontal scale with magnitude 0.1–4; vertical scale stays positive.

## Structured dialogue

Plain-text `dialogue.send` remains supported and produces one default span. Alternatively, send `spans`: an array of 1–64 `DialogueSpan` objects with `text`, optional `bold`/`italic` booleans, optional `color` (`#RRGGBB` or null), `pauseAfterMs` (0–5,000) and `charactersPerSecond` (finite 1–120; default 35). Total UTF-16 text length is at most 4,000 characters; explicit pauses across a line total at most 15 seconds. Empty spans may carry pauses, but the complete line needs visible text. Unknown/duplicate span fields, null spans and unsupported styles are rejected. There is no HTML execution or arbitrary styling.

The server derives canonical plain text by concatenating span text exactly, preserving boundaries and spaces. If a separate `text` is provided it must match exactly. Accepted dialogue event `data.detail` contains both `text` and `spans`, allowing rich display, history, replay and accessibility to use the same authoritative content. All span/media/stage validations occur before the transaction commits. `currentDialogue` snapshots persist the same canonical spans, text and timestamp.

## Persistence, backup and operational limits

SQLite schema version 1 creates the world, ordered event log, deduplication ledger and moderation audit atomically. Schema version 2 adds the persisted media catalog and approved demo entries without deleting existing accounts or world state. Existing unapproved room media references are hidden from snapshots until explicitly authorized; old cues must pass the new catalog checks before preview/play. Opening a newer unsupported schema fails explicitly. SQLite WAL and full synchronization are enabled; each command works on a fresh in-transaction world and commits its state, events and dedup result together. Concurrent requests are serialized for deterministic authority and broadcast order. Restart preserves accounts, roles, inventories, catalogs, rooms, cues, timer deadlines and replay history. Expired timers emit their result on the next tick. Room membership is retained for the configurable reconnect grace, then released to avoid permanently occupied seats.

Back up with SQLite's online backup API/CLI while running, or stop the host cleanly before copying the database. A live `.db` copy without its WAL is not a safe backup. Restore to a separate path, start the same schema-compatible server, then verify snapshots and replay before replacing a production deployment. The application does not upload backups or perform destructive migrations.

Older databases that already exceed snapshot collection/byte limits fail closed with `state_limit` during snapshot delivery or authentication. Stored accounts and actors are preserved. There is no automatic repair or truncation; an operator must restore or repair a backup before affected clients can reconnect.

This initial server deliberately uses a transactionally stored JSON world for small community deployments. It is not a horizontally scalable database architecture; no throughput benchmark is claimed. There is a 5,000-account storage cap, 100-room cap, 1,000-character/item catalog caps, 200 reusable cue cap, and 20 timers per room; byte budgets can impose a lower practical limit. Events and deduplication records are not automatically pruned, because deleting deduplication records could allow old spending retries to execute again. Operators must monitor disk space. High-volume retention, automatic external asset hosting, production dashboards and centralized live-session revocation are not implemented.

## Validation

`dotnet build src/VNO2.Server/VNO2.Server.csproj -c Release` includes warnings-as-errors. The retained `tools/community-smoke.py` needs Python 3 and `websockets`, and a fresh development guest server on port 5167. Run both processes in the same execution environment:

```sh
python tools/community-smoke.py
# Or start temporary hosts and include process-restart/moderation privacy checks:
python tools/community-smoke.py --self-host
```

It covers fragmented requests, two-player stage displacement, exactly-once spending and fingerprint conflicts, transfers, forbidden grants/moderation, cooldowns, arithmetic, replay, dialogue clear/retry/reconnect, mirrored movement/dialogue, and malformed/version/binary/oversized handshake rejection. The self-host mode also verifies that repeated reads do not grow the request ledger. It additionally verifies actual process-restart durability, isolated-event and staff-reason privacy, isolation of privileged reads, mute/ban enforcement, media kind/permission/revocation validation, non-destructive schema migration, character reservation enforcement/clear, concurrent distinct-account capacity admission with same-account reconnect, and timer expiration. Source regressions cover malformed JSON values, expression limits, signed X scale across all actor actions, atomic all-recipient actor bounds, preserved oversized legacy state, clear-dialogue privacy/resources, complete stage cleanup, and fail-closed malformed master ticket responses. Neither loopback tests nor headless checks prove production TLS/proxy deployment, vendor animation SDKs, or production scalability.
