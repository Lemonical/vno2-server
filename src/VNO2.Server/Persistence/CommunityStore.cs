using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VNO2.Contracts.Community;
using VNO2.Server.Domain;
using VNO2.Server.Security;

namespace VNO2.Server.Persistence;

public sealed class CommunityStore : IDisposable
{
    private static readonly HashSet<string> ReadOnlyActions = new(StringComparer.Ordinal)
    { "snapshot.get", "ping", "replay.get", "moderation.audit", "director.cue.list", "director.preview", "moderation.lookup" };
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string connectionString;
    private readonly CommunityOptions options;

    public CommunityStore(CommunityOptions options)
    {
        this.options = options;
        var fullPath = Path.GetFullPath(options.DatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = fullPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;";
        command.ExecuteNonQuery();
        command.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        if (version > 2) throw new InvalidOperationException("The community database schema is newer than this server");
        if (version == 0)
        {
            using var transaction = connection.BeginTransaction();
            command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE world (id INTEGER PRIMARY KEY CHECK (id=1), json TEXT NOT NULL);
                CREATE TABLE events (sequence INTEGER PRIMARY KEY AUTOINCREMENT, id TEXT NOT NULL UNIQUE,
                    timestamp TEXT NOT NULL, kind TEXT NOT NULL, room_id TEXT, actor_id TEXT, data TEXT NOT NULL, audience TEXT NOT NULL);
                CREATE TABLE requests (account_id TEXT NOT NULL, request_id TEXT NOT NULL, fingerprint TEXT NOT NULL,
                    reply TEXT NOT NULL, created_at TEXT NOT NULL, PRIMARY KEY(account_id, request_id));
                CREATE TABLE audit (id INTEGER PRIMARY KEY AUTOINCREMENT, timestamp TEXT NOT NULL,
                    actor_id TEXT NOT NULL, action TEXT NOT NULL, target_id TEXT, reason TEXT NOT NULL);
                CREATE INDEX events_room_sequence ON events(room_id, sequence);
                PRAGMA user_version=1;
                """;
            command.ExecuteNonQuery();
            var initial = new WorldState();
            initial.Rooms.Add("lobby", new RoomState { Id = "lobby", Name = "Lobby", Description = "Welcome to the community", Capacity = 100 });
            initial.Rooms.Add("stage", new RoomState { Id = "stage", Name = "Main stage", Description = "Live collaborative roleplay" });
            initial.Characters.Add("mira", new("mira", "Mira", "Demo/Mira", ["neutral", "happy"], "neutral"));
            initial.Characters.Add("rowan", new("rowan", "Rowan", "Demo/Rowan", ["neutral", "angry"], "neutral"));
            initial.Characters.Add("echo", new("echo", "Echo", "Demo/Echo", ["neutral", "talk"], "neutral"));
            initial.Items.Add("potion", new("potion", "Potion", "Restore 25 HP", 10, 25, 0, 5));
            command.CommandText = "INSERT INTO world(id,json) VALUES(1,$json)";
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(initial, ProtocolJson.Options));
            command.ExecuteNonQuery();
            transaction.Commit();
        }
        if (version < 2)
        {
            using var migration = connection.BeginTransaction();
            var state = Load(connection, migration);
            MediaCatalog.Seed(state);
            Save(connection, migration, state);
            command.Transaction = migration;
            command.CommandText = "PRAGMA user_version=2";
            command.Parameters.Clear();
            command.ExecuteNonQuery();
            migration.Commit();
        }
    }

    public async Task<(AccountState Account, string? GuestToken)> AuthenticateAsync(CommunityIdentity? identity,
        string guestName, string guestToken, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var state = Load(connection, transaction);
            AccountState? account;
            string? issuedToken = null;
            if (identity != null)
            {
                state.Accounts.TryGetValue(identity.AccountId, out account);
                if (account?.GuestTokenHash != null) throw new DomainException("authentication_failed", "Invalid account identity");
                if (account == null && state.Accounts.Count >= 5000) throw new DomainException("account_limit", "This community has reached its account storage limit");
                account ??= new AccountState { Id = identity.AccountId, Credits = options.StartingCredits };
                account.DisplayName = identity.DisplayName;
            }
            else if (guestToken.Length > 0)
            {
                var hash = SecretHash.Token(guestToken);
                account = state.Accounts.Values.FirstOrDefault(a => a.GuestTokenHash == hash)
                    ?? throw new DomainException("authentication_failed", "The guest token is invalid");
                issuedToken = guestToken;
            }
            else
            {
                if (state.Accounts.Count >= 5000) throw new DomainException("account_limit", "This community has reached its account storage limit");
                issuedToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                account = new AccountState
                {
                    Id = "guest-" + Guid.NewGuid().ToString("N"),
                    DisplayName = guestName,
                    Credits = options.StartingCredits,
                    GuestTokenHash = SecretHash.Token(issuedToken)
                };
            }
            if (account.Banned) throw new DomainException("banned", "This account is banned from this community");
            if (options.AdministratorAccountIds.Contains(account.Id, StringComparer.Ordinal)) account.Role = "administrator";
            state.Accounts[account.Id] = account;
            foreach (var recipient in state.Accounts.Values.Where(a => !a.Banned))
                EnsureBounded(WorldQueries.Snapshot(state, recipient.Id, 0, options));
            Save(connection, transaction, state);
            transaction.Commit();
            return (account, issuedToken);
        }
        finally { gate.Release(); }
    }

    public async Task<CommunitySnapshot> SnapshotAsync(string accountId, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            using var connection = Open();
            var state = Load(connection);
            if (!state.Accounts.TryGetValue(accountId, out var account) || account.Banned)
                throw new DomainException("banned", "The account is not permitted to connect");
            var snapshot = WorldQueries.Snapshot(state, accountId, Sequence(connection), options);
            EnsureBounded(snapshot);
            return snapshot;
        }
        finally { gate.Release(); }
    }

    public async Task<(RoomSummary[] Rooms, CharacterDefinition[] Catalog, MediaDefinition[] Media)> PublicInfoAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            using var connection = Open();
            var state = Load(connection);
            return (WorldQueries.Rooms(state), state.Characters.Values.ToArray(), state.Media.Values.Where(media => media.Enabled).ToArray());
        }
        finally { gate.Release(); }
    }

    public async Task<CommandOutcome> ExecuteAsync(string accountId, CommunityRequest request, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var state = Load(connection, transaction);
            if (!state.Accounts.TryGetValue(accountId, out var account) || account.Banned)
                throw new DomainException("banned", "The account is not permitted to connect");
            if (request.Data.ValueKind != JsonValueKind.Object)
                throw new DomainException("invalid_request", "Request data must be an object");
            var fingerprint = SecretHash.Token(request.Action + "\n" + request.Data.GetRawText());
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT fingerprint, reply FROM requests WHERE account_id=$account AND request_id=$request";
            command.Parameters.AddWithValue("$account", accountId);
            command.Parameters.AddWithValue("$request", request.RequestId);
            using (var reader = command.ExecuteReader())
            {
                if (reader.Read())
                {
                    if (reader.GetString(0) != fingerprint) throw new DomainException("request_conflict", "This request ID was already used for another action");
                    var saved = JsonSerializer.Deserialize<CommunityMessage>(reader.GetString(1), ProtocolJson.Options)!;
                    return new(saved, [], [], true);
                }
            }
            ActionResult outcome;
            if (request.Action == "replay.get")
            {
                var after = request.Data.Money("afterSequence");
                var limit = request.Data.Number("limit", 1, 500, 200);
                var replay = Replay(connection, transaction, accountId, after, limit);
                var latest = Sequence(connection, transaction);
                var next = replay.Length == 0 ? latest : replay[^1].Sequence;
                outcome = new(new { events = replay, sequence = latest, nextSequence = next, hasMore = next < latest }, [], []);
            }
            else if (request.Action == "moderation.audit")
            {
                new ActionContext(state, account, options).DemandModerator();
                using var audit = connection.CreateCommand();
                audit.Transaction = transaction;
                audit.CommandText = "SELECT timestamp,actor_id,action,target_id,reason FROM audit ORDER BY id DESC LIMIT 100";
                using var reader = audit.ExecuteReader();
                var entries = new List<object>();
                while (reader.Read()) entries.Add(new { timestamp = reader.GetString(0), actorAccountId = reader.GetString(1), action = reader.GetString(2), targetAccountId = reader.IsDBNull(3) ? null : reader.GetString(3), reason = reader.GetString(4) });
                outcome = new(new { entries }, [], []);
            }
            else outcome = CommunityActions.Apply(state, account, request, options);
            EnsureBounded(outcome.Result);
            if (ReadOnlyActions.Contains(request.Action))
            {
                var readReply = ProtocolJson.Message("ack", request.RequestId, Sequence(connection, transaction), new { action = request.Action, result = outcome.Result });
                return new(readReply, [], []);
            }
            foreach (var recipient in state.Accounts.Values.Where(a => !a.Banned))
                EnsureBounded(WorldQueries.Snapshot(state, recipient.Id, 0, options));
            var accepted = Append(connection, transaction, outcome.Events);
            foreach (var entry in outcome.Events.Where(e => e.Kind == "moderation.applied"))
            {
                var data = ProtocolJson.Element(entry.Data);
                using var audit = connection.CreateCommand();
                audit.Transaction = transaction;
                audit.CommandText = "INSERT INTO audit(timestamp,actor_id,action,target_id,reason) VALUES($at,$actor,$action,$target,$reason)";
                audit.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
                audit.Parameters.AddWithValue("$actor", accountId);
                audit.Parameters.AddWithValue("$action", request.Action);
                audit.Parameters.AddWithValue("$target", data.GetProperty("targetAccountId").GetString());
                audit.Parameters.AddWithValue("$reason", data.GetProperty("reason").GetString());
                audit.ExecuteNonQuery();
            }
            Save(connection, transaction, state);
            var reply = ProtocolJson.Message("ack", request.RequestId, Sequence(connection, transaction), new { action = request.Action, result = outcome.Result });
            command.CommandText = "INSERT INTO requests(account_id,request_id,fingerprint,reply,created_at) VALUES($account,$request,$fingerprint,$reply,$at)";
            command.Parameters.AddWithValue("$fingerprint", fingerprint);
            command.Parameters.AddWithValue("$reply", JsonSerializer.Serialize(reply, ProtocolJson.Options));
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
            transaction.Commit();
            return new(reply, accepted, outcome.DisconnectAccountIds);
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<AcceptedEvent>> ExpireTimersAsync(IReadOnlySet<string> connectedAccounts, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var state = Load(connection, transaction);
            var now = DateTimeOffset.UtcNow;
            var changed = false;
            var pending = new List<PendingEvent>();
            foreach (var account in state.Accounts.Values.Where(a => a.RoomId != null))
            {
                if (connectedAccounts.Contains(account.Id))
                {
                    if (account.DisconnectedAt != null) { account.DisconnectedAt = null; changed = true; }
                    continue;
                }
                if (account.DisconnectedAt == null) { account.DisconnectedAt = now; changed = true; }
                if (account.DisconnectedAt > now.AddSeconds(-options.ReconnectGraceSeconds)) continue;
                var oldRoom = account.RoomId!;
                RoomActions.Leave(new ActionContext(state, account, options), account);
                pending.Add(new("room.left", oldRoom, account.Id, new { accountId = account.Id, reason = "disconnected" }, WorldQueries.Audience(state, oldRoom, account)));
                changed = true;
            }
            var expired = state.Timers.Where(t => t.EndsAt <= now).ToArray();
            if (!changed && expired.Length == 0) return [];
            foreach (var timer in expired)
            {
                state.Timers.Remove(timer);
                pending.Add(new("timer.expired", timer.RoomId, null, new { timerId = timer.Id, timer.Label }, WorldQueries.Audience(state, timer.RoomId)));
            }
            var events = Append(connection, transaction, pending);
            Save(connection, transaction, state);
            transaction.Commit();
            return events;
        }
        finally { gate.Release(); }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    private static WorldState Load(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT json FROM world WHERE id=1";
        return JsonSerializer.Deserialize<WorldState>((string)command.ExecuteScalar()!, ProtocolJson.Options)!;
    }

    private static void Save(SqliteConnection connection, SqliteTransaction transaction, WorldState state)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE world SET json=$json WHERE id=1";
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(state, ProtocolJson.Options));
        command.ExecuteNonQuery();
    }

    private static long Sequence(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(sequence),0) FROM events";
        return (long)command.ExecuteScalar()!;
    }

    private static List<AcceptedEvent> Append(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<PendingEvent> pending)
    {
        var accepted = new List<AcceptedEvent>();
        foreach (var item in pending)
        {
            var id = Guid.NewGuid().ToString("N");
            var timestamp = DateTimeOffset.UtcNow;
            EnsureBounded(item.Data, 700_000);
            var data = ProtocolJson.Element(item.Data);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO events(id,timestamp,kind,room_id,actor_id,data,audience) VALUES($id,$at,$kind,$room,$actor,$data,$audience); SELECT last_insert_rowid();";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$at", timestamp.ToString("O"));
            command.Parameters.AddWithValue("$kind", item.Kind);
            command.Parameters.AddWithValue("$room", (object?)item.RoomId ?? DBNull.Value);
            command.Parameters.AddWithValue("$actor", (object?)item.ActorAccountId ?? DBNull.Value);
            command.Parameters.AddWithValue("$data", data.GetRawText());
            command.Parameters.AddWithValue("$audience", JsonSerializer.Serialize(item.Audience, ProtocolJson.Options));
            var sequence = (long)command.ExecuteScalar()!;
            accepted.Add(new(new(sequence, id, timestamp, item.Kind, item.RoomId, item.ActorAccountId, data), item.Audience));
        }
        return accepted;
    }

    private static CommunityEvent[] Replay(SqliteConnection connection, SqliteTransaction transaction, string accountId, long after, int limit)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT sequence,id,timestamp,kind,room_id,actor_id,data FROM events
            WHERE sequence>$after AND EXISTS(SELECT 1 FROM json_each(events.audience) WHERE value=$account)
            ORDER BY sequence LIMIT $limit
            """;
        command.Parameters.AddWithValue("$after", after);
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var events = new List<CommunityEvent>();
        var bytes = 0;
        while (reader.Read())
        {
            var json = reader.GetString(6);
            var eventBytes = Encoding.UTF8.GetByteCount(json) + 1024;
            if (bytes + eventBytes > 750_000 && events.Count > 0) break;
            bytes += eventBytes;
            events.Add(new(reader.GetInt64(0), reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture),
                reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                JsonSerializer.Deserialize<JsonElement>(json, ProtocolJson.Options)));
        }
        return events.ToArray();
    }

    private static void EnsureBounded<T>(T value, int limit = 800_000)
    {
        if (value is CommunitySnapshot snapshot && (snapshot.Rooms.Count > 100 || snapshot.Catalog.Count > 1000 ||
            snapshot.Participants.Count > 256 || snapshot.Actors.Count > 32 || snapshot.Inventory.Count > 1000 ||
            snapshot.Shop.Count > 1000 || snapshot.Timers.Count > 20 || snapshot.MediaCatalog.Count > 500))
            throw new DomainException("state_limit", "This change would exceed the supported snapshot collection limits; reduce room or stage size");
        if (JsonSerializer.SerializeToUtf8Bytes(value, ProtocolJson.Options).Length > limit)
            throw new DomainException("state_limit", "This change would exceed the community protocol state limit; reduce catalog, room, or response size");
    }

    public void Dispose() => gate.Dispose();
}
