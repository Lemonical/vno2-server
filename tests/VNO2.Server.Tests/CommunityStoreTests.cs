using System.Text.Json;
using VNO2.Contracts.Community;
using VNO2.Server;
using VNO2.Server.Domain;
using VNO2.Server.Persistence;

namespace VNO2.Tests;

public sealed class CommunityStoreTests
{
    [Test]
    public async Task MalformedValuesAndCatalogExpressionsAreRejectedWithoutStateOrDeduplicationChanges()
    {
        await WithStoreAsync(async store =>
        {
            await JoinAsync(store, "owner", "alice", "bob");
            await store.ExecuteAsync("alice", Request("character.select", new { characterId = "mira" }), CancellationToken.None);
            var before = JsonSerializer.Serialize(await store.SnapshotAsync("alice"));
            int[] invalidArray = [1];
            object?[] invalidValues = [null, true, "one", invalidArray, new { value = 1 }];
            foreach (var invalid in invalidValues)
            {
                await Assert.That(await ErrorCodeAsync(store, "alice", Request("shop.buy", new { itemId = "potion", quantity = invalid }))).IsEqualTo("invalid_request");
                await Assert.That(await ErrorCodeAsync(store, "alice", Request("credits.transfer", new { targetAccountId = "bob", amount = invalid }))).IsEqualTo("invalid_request");
                await Assert.That(await ErrorCodeAsync(store, "alice", Request("stage.move", new { expression = "happy", scaleX = invalid }))).IsEqualTo("invalid_request");
                await Assert.That(await ErrorCodeAsync(store, "alice", Request("dialogue.send", new { spans = new[] { new { text = "Invalid pause", pauseAfterMs = invalid } } }))).IsEqualTo("invalid_request");
                await Assert.That(JsonSerializer.Serialize(await store.SnapshotAsync("alice"))).IsEqualTo(before);
            }
            await Assert.That(await ErrorCodeAsync(store, "alice", Request("shop.buy", new { itemId = "potion", quantity = 1.5 }))).IsEqualTo("invalid_request");
            await Assert.That(await ErrorCodeAsync(store, "alice", Request("credits.transfer", new { targetAccountId = "bob", amount = 1.5 }))).IsEqualTo("invalid_request");
            await Assert.That(await ErrorCodeAsync(store, "alice", Request("ping", invalidArray))).IsEqualTo("invalid_request");
            var catalogBefore = JsonSerializer.Serialize((await store.SnapshotAsync("owner")).Catalog);
            object?[] invalidExpressions = [null, "neutral", (object?[])[null], (string[])[" "], (string[])[" neutral"], (string[])["bad\nname"], (string[])["bad\u0001name"], (string[])[new string('x', 101)]];
            foreach (var invalid in invalidExpressions)
            {
                await Assert.That(await ErrorCodeAsync(store, "owner", Request("catalog.configure", new { characterId = "mira", name = "Mira", contentReference = "Demo/Mira", expressions = invalid }))).IsEqualTo("invalid_request");
                await Assert.That(JsonSerializer.Serialize((await store.SnapshotAsync("owner")).Catalog)).IsEqualTo(catalogBefore);
            }
            await Assert.That(await ErrorCodeAsync(store, "owner", Request("catalog.configure", new { characterId = "mira", name = "Mira", contentReference = "Demo/Mira", expressions = (string[])["neutral", "Neutral"] }))).IsEqualTo("invalid_expression");
            await Assert.That(await ErrorCodeAsync(store, "owner", Request("room.reserve", new { roomId = "lobby", accountIds = (object?[])[null] }))).IsEqualTo("invalid_request");
            var expression = new string('x', 100);
            await store.ExecuteAsync("owner", Request("catalog.configure", new { characterId = "bounded", name = "Bounded", contentReference = "Tests/Bounded", expressions = new[] { expression } }), CancellationToken.None);
            await store.ExecuteAsync("alice", Request("character.select", new { characterId = "bounded", expression }), CancellationToken.None);
            var rejected = Request("shop.buy", new { itemId = "potion", quantity = "invalid" });
            await Assert.That(await ErrorCodeAsync(store, "alice", rejected)).IsEqualTo("invalid_request");
            var accepted = await store.ExecuteAsync("alice", rejected with { Data = Request("shop.buy", new { itemId = "potion", quantity = 1 }).Data }, CancellationToken.None);
            await Assert.That(accepted.Duplicate).IsFalse();
            await Assert.That((await store.SnapshotAsync("alice")).Resources.Credits).IsEqualTo(90L);
        });
    }

    [Test]
    public async Task MirroredHorizontalScaleWorksAcrossPlayerDialogueAndDirectorActions()
    {
        await WithStoreAsync(async store =>
        {
            await JoinAsync(store, "owner", "alice");
            await store.ExecuteAsync("alice", Request("character.select", new { characterId = "mira" }), CancellationToken.None);
            await store.ExecuteAsync("alice", Request("stage.move", new { position = "left", scaleX = -1, scaleY = 1 }), CancellationToken.None);
            await Assert.That((await store.SnapshotAsync("alice")).Actors.Single().ScaleX).IsEqualTo(-1d);
            await store.ExecuteAsync("alice", Request("dialogue.send", new { text = "Facing left", scaleX = -2 }), CancellationToken.None);
            await Assert.That((await store.SnapshotAsync("alice")).Actors.Single().ScaleX).IsEqualTo(-2d);
            await store.ExecuteAsync("owner", Request("director.actor", new { targetAccountId = "alice", scaleX = -0.1 }), CancellationToken.None);
            await Assert.That((await store.SnapshotAsync("alice")).Actors.Single().ScaleX).IsEqualTo(-0.1d);
            var before = JsonSerializer.Serialize(await store.SnapshotAsync("alice"));
            foreach (var invalid in new[] { 0d, 0.09, -0.09, 4.01, -4.01 })
            {
                await Assert.That(await ErrorCodeAsync(store, "alice", Request("stage.move", new { position = "right", expression = "happy", scaleX = invalid }))).IsEqualTo("invalid_request");
                await Assert.That(await ErrorCodeAsync(store, "alice", Request("dialogue.send", new { text = "Must roll back", position = "right", scaleX = invalid }))).IsEqualTo("invalid_request");
                await Assert.That(await ErrorCodeAsync(store, "owner", Request("director.actor", new { targetAccountId = "alice", position = "right", scaleX = invalid }))).IsEqualTo("invalid_request");
                await Assert.That(JsonSerializer.Serialize(await store.SnapshotAsync("alice"))).IsEqualTo(before);
            }
            await Assert.That(await ErrorCodeAsync(store, "alice", Request("stage.move", new { scaleY = -1 }))).IsEqualTo("invalid_request");
            using var nonfinite = JsonDocument.Parse("{\"scaleX\":1e999}");
            await Assert.That(await ErrorCodeAsync(store, "alice", new(1, Guid.NewGuid().ToString("N"), "stage.move", nonfinite.RootElement.Clone()))).IsEqualTo("invalid_request");
            await Assert.That(JsonSerializer.Serialize(await store.SnapshotAsync("alice"))).IsEqualTo(before);
        });
    }

    [Test]
    public async Task IsolatedStaffCannotReadPrivilegedStateOrReceiveNewPrivateHistory()
    {
        await WithStoreAsync(async store =>
        {
            await JoinAsync(store, "owner", "moderator", "alice", "later", "observer");
            await store.ExecuteAsync("owner", Request("moderation.role", new { targetAccountId = "moderator", role = "moderator" }), CancellationToken.None);
            var prior = await store.ExecuteAsync("owner", Request("moderation.mute", new { targetAccountId = "observer", reason = "previously accepted staff history" }), CancellationToken.None);
            await store.ExecuteAsync("owner", Request("moderation.isolate", new { targetAccountId = "moderator", enabled = true }), CancellationToken.None);
            await Assert.That(await ErrorCodeAsync(store, "moderator", Request("moderation.lookup", new { query = "alice" }))).IsEqualTo("isolated");
            await Assert.That(await ErrorCodeAsync(store, "moderator", Request("moderation.audit", new { }))).IsEqualTo("isolated");
            await store.ExecuteAsync("owner", Request("moderation.isolate", new { targetAccountId = "alice", enabled = true }), CancellationToken.None);
            await store.ExecuteAsync("alice", Request("character.select", new { characterId = "mira" }), CancellationToken.None);
            var privateLine = await store.ExecuteAsync("alice", Request("dialogue.send", new { text = "Private accepted line" }), CancellationToken.None);
            var staff = await store.ExecuteAsync("owner", Request("moderation.mute", new { targetAccountId = "observer", enabled = false, reason = "new private staff reason" }), CancellationToken.None);
            await Assert.That(privateLine.Events.Single().Audience.Contains("moderator")).IsFalse();
            await Assert.That(staff.Events.Single(item => item.Event.Kind == "moderation.applied").Audience.Contains("moderator")).IsFalse();
            var isolatedStaff = await store.SnapshotAsync("moderator");
            await Assert.That(isolatedStaff.Permissions.Contains("moderation")).IsFalse();
            await Assert.That(isolatedStaff.Participants.Any(item => item.AccountId == "alice")).IsFalse();
            await Assert.That(isolatedStaff.Actors.Any(item => item.AccountId == "alice")).IsFalse();
            await Assert.That(isolatedStaff.CurrentDialogue).IsNull();
            await store.ExecuteAsync("owner", Request("moderation.role", new { targetAccountId = "later", role = "moderator" }), CancellationToken.None);
            await Assert.That((await store.SnapshotAsync("later")).CurrentDialogue).IsNull();
            string[] viewers = ["moderator", "later", "observer"];
            foreach (var id in viewers)
            {
                var replay = await store.ExecuteAsync(id, Request("replay.get", new { afterSequence = 0 }), CancellationToken.None);
                var events = replay.Reply.Data.GetProperty("result").GetProperty("events").EnumerateArray().ToArray();
                await Assert.That(events.Any(item => item.GetProperty("id").GetString() == privateLine.Events.Single().Event.Id)).IsFalse();
                await Assert.That(events.Any(item => item.GetProperty("id").GetString() == staff.Events.Single(entry => entry.Event.Kind == "moderation.applied").Event.Id)).IsFalse();
                if (id == "moderator")
                    await Assert.That(events.Any(item => item.GetProperty("id").GetString() == prior.Events.Single(entry => entry.Event.Kind == "moderation.applied").Event.Id)).IsTrue();
            }
        });
    }

    [Test]
    public async Task ActorLimitRejectsIsolatedPerformanceAtomicallyForEveryRecipient()
    {
        await WithStoreAsync(async store =>
        {
            await JoinAsync(store, "owner", "left", "right");
            foreach (var (id, position) in new[] { ("owner", "center"), ("left", "left"), ("right", "right") })
            {
                await store.ExecuteAsync(id, Request("character.select", new { characterId = "mira" }), CancellationToken.None);
                await store.ExecuteAsync(id, Request("dialogue.send", new { text = "Public " + id, position }), CancellationToken.None);
            }
            var isolatedAccounts = Enumerable.Range(0, 30).Select(index => "isolated-" + index).ToArray();
            foreach (var id in isolatedAccounts)
            {
                await JoinAsync(store, id);
                await store.ExecuteAsync("owner", Request("moderation.isolate", new { targetAccountId = id }), CancellationToken.None);
                await store.ExecuteAsync(id, Request("character.select", new { characterId = "echo" }), CancellationToken.None);
                if (id != isolatedAccounts[^1])
                    await store.ExecuteAsync(id, Request("dialogue.send", new { text = "Private " + id, position = "center" }), CancellationToken.None);
            }
            var ownerBefore = JsonSerializer.Serialize(await store.SnapshotAsync("owner"));
            var extraBefore = JsonSerializer.Serialize(await store.SnapshotAsync(isolatedAccounts[^1]));
            await Assert.That((await store.SnapshotAsync("owner")).Actors.Count).IsEqualTo(32);
            var rejected = Request("dialogue.send", new { text = "Would create actor 33", position = "center" });
            await Assert.That(await ErrorCodeAsync(store, isolatedAccounts[^1], rejected)).IsEqualTo("state_limit");
            await Assert.That(JsonSerializer.Serialize(await store.SnapshotAsync("owner"))).IsEqualTo(ownerBefore);
            await Assert.That(JsonSerializer.Serialize(await store.SnapshotAsync(isolatedAccounts[^1]))).IsEqualTo(extraBefore);
            foreach (var id in isolatedAccounts.Append("owner").Append("left").Append("right"))
                await Assert.That((await store.SnapshotAsync(id)).Actors.Count <= 32).IsTrue();
            await store.ExecuteAsync("owner", Request("director.actor", new { targetAccountId = isolatedAccounts[0], remove = true }), CancellationToken.None);
            var accepted = await store.ExecuteAsync(isolatedAccounts[^1], rejected, CancellationToken.None);
            var duplicate = await store.ExecuteAsync(isolatedAccounts[^1], rejected, CancellationToken.None);
            await Assert.That(accepted.Duplicate).IsFalse();
            await Assert.That(duplicate.Duplicate).IsTrue();
            await Assert.That(duplicate.Events.Count).IsEqualTo(0);
            await Assert.That((await store.SnapshotAsync("owner")).Actors.Count).IsEqualTo(32);
            await Assert.That((await store.SnapshotAsync("right")).Actors.Count).IsEqualTo(3);
        });
    }

    [Test]
    public async Task ExistingOversizedStagesFailClosedWithoutDeletingStoredActors()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"vno2-existing-stage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var database = Path.Combine(directory, "world.db");
            using var store = new CommunityStore(new CommunityOptions { DatabasePath = database, AdministratorAccountIds = ["owner"] });
            await JoinAsync(store, "owner");
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = database }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT json FROM world WHERE id=1";
            var state = JsonSerializer.Deserialize<WorldState>((string)command.ExecuteScalar()!, JsonSerializerOptions.Web)!;
            for (var index = 0; index < 33; index++)
            {
                var id = "existing-isolated-" + index;
                state.Accounts.Add(id, new AccountState { Id = id, DisplayName = id, RoomId = "lobby", CharacterId = "echo", Expression = "neutral", Position = "center", Isolated = true });
            }
            var stored = JsonSerializer.Serialize(state, JsonSerializerOptions.Web);
            command.CommandText = "UPDATE world SET json=$json WHERE id=1";
            command.Parameters.AddWithValue("$json", stored);
            command.ExecuteNonQuery();
            string? snapshotError = null;
            try { await store.SnapshotAsync("owner"); }
            catch (DomainException exception) { snapshotError = exception.Code; }
            await Assert.That(snapshotError).IsEqualTo("state_limit");
            string? authenticationError = null;
            try { await store.AuthenticateAsync(new("owner", "Changed name"), "", "", CancellationToken.None); }
            catch (DomainException exception) { authenticationError = exception.Code; }
            await Assert.That(authenticationError).IsEqualTo("state_limit");
            command.CommandText = "SELECT json FROM world WHERE id=1";
            await Assert.That((string)command.ExecuteScalar()!).IsEqualTo(stored);
            await store.ExecuteAsync("owner", Request("stage.clear", new { }), CancellationToken.None);
            await Assert.That((await store.SnapshotAsync("owner")).Actors.Count).IsEqualTo(0);
            var recovered = JsonSerializer.Deserialize<WorldState>((string)command.ExecuteScalar()!, JsonSerializerOptions.Web)!;
            await Assert.That(recovered.Accounts.Count).IsEqualTo(34);
            await Assert.That(recovered.Accounts.Values.All(account => account.Position == null)).IsTrue();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task DialogueClearPreservesActorsAndResourcesAndStageClearPersistsCompleteCleanup()
    {
        await WithStoreAsync(async store =>
        {
            await JoinAsync(store, "owner", "alice", "bob", "isolated");
            string[] performers = ["alice", "bob", "isolated"];
            foreach (var id in performers)
                await store.ExecuteAsync(id, Request("character.select", new { characterId = "mira" }), CancellationToken.None);
            var noOp = await store.ExecuteAsync("bob", Request("dialogue.clear", new { }), CancellationToken.None);
            await Assert.That(noOp.Events.Count).IsEqualTo(0);
            await store.ExecuteAsync("alice", Request("shop.buy", new { itemId = "potion" }), CancellationToken.None);
            await store.ExecuteAsync("alice", Request("dialogue.send", new { text = "Own public line", position = "left" }), CancellationToken.None);
            var bobBefore = JsonSerializer.Serialize(await store.SnapshotAsync("bob"));
            await Assert.That(await ErrorCodeAsync(store, "bob", Request("dialogue.clear", new { }))).IsEqualTo("forbidden");
            await Assert.That(JsonSerializer.Serialize(await store.SnapshotAsync("bob"))).IsEqualTo(bobBefore);
            await Assert.That((await store.SnapshotAsync("bob")).CurrentDialogue!.AccountId).IsEqualTo("alice");
            var before = await store.SnapshotAsync("alice");
            var clear = Request("dialogue.clear", new { });
            var first = await store.ExecuteAsync("alice", clear, CancellationToken.None);
            var duplicate = await store.ExecuteAsync("alice", clear, CancellationToken.None);
            var after = await store.SnapshotAsync("alice");
            await Assert.That(first.Events.Single().Event.Kind).IsEqualTo("dialogue.cleared");
            await Assert.That(first.Events.Single().Event.Data.GetProperty("detail").GetProperty("accountId").GetString()).IsEqualTo("alice");
            await Assert.That(duplicate.Duplicate).IsTrue();
            await Assert.That(duplicate.Events.Count).IsEqualTo(0);
            await Assert.That(JsonSerializer.Serialize(first.Reply)).IsEqualTo(JsonSerializer.Serialize(duplicate.Reply));
            await Assert.That(after.Sequence).IsEqualTo(first.Reply.Sequence);
            await Assert.That(after.CurrentDialogue).IsNull();
            await Assert.That(after.SpeakerAccountId).IsNull();
            await Assert.That(after.Actors.Single().IsSpeaker).IsFalse();
            await Assert.That(after.Actors.Single().ActorId).IsEqualTo(before.Actors.Single().ActorId);
            await Assert.That(JsonSerializer.Serialize(after.Resources)).IsEqualTo(JsonSerializer.Serialize(before.Resources));
            await Assert.That(JsonSerializer.Serialize(after.Inventory)).IsEqualTo(JsonSerializer.Serialize(before.Inventory));
            await store.ExecuteAsync("alice", Request("dialogue.send", new { text = "Newer line survives an old clear retry", position = "left" }), CancellationToken.None);
            await store.ExecuteAsync("alice", clear, CancellationToken.None);
            await Assert.That((await store.SnapshotAsync("alice")).CurrentDialogue!.Text).IsEqualTo("Newer line survives an old clear retry");
            await store.AuthenticateAsync(new("outside", "outside"), "", "", CancellationToken.None);
            await Assert.That(await ErrorCodeAsync(store, "outside", Request("dialogue.clear", new { }))).IsEqualTo("not_in_room");
            await store.ExecuteAsync("bob", Request("dialogue.send", new { text = "Public room line", position = "right" }), CancellationToken.None);
            await store.ExecuteAsync("owner", Request("moderation.isolate", new { targetAccountId = "isolated" }), CancellationToken.None);
            await store.ExecuteAsync("isolated", Request("dialogue.send", new { text = "Private room line" }), CancellationToken.None);
            var privateClear = await store.ExecuteAsync("isolated", Request("dialogue.clear", new { }), CancellationToken.None);
            await Assert.That(privateClear.Events.Single().Audience.Contains("owner")).IsTrue();
            await Assert.That(privateClear.Events.Single().Audience.Contains("isolated")).IsTrue();
            await Assert.That(privateClear.Events.Single().Audience.Contains("bob")).IsFalse();
            await Assert.That((await store.SnapshotAsync("owner")).CurrentDialogue!.AccountId).IsEqualTo("bob");
            await Assert.That((await store.SnapshotAsync("bob")).SpeakerAccountId).IsEqualTo("bob");
            await Assert.That((await store.SnapshotAsync("isolated")).Actors.Any(actor => actor.AccountId == "isolated")).IsTrue();
            await store.ExecuteAsync("isolated", Request("dialogue.send", new { text = "Another private room line" }), CancellationToken.None);
            await store.ExecuteAsync("owner", Request("dialogue.clear", new { }), CancellationToken.None);
            await Assert.That((await store.SnapshotAsync("owner")).CurrentDialogue).IsNull();
            await Assert.That((await store.SnapshotAsync("isolated")).CurrentDialogue).IsNull();
            await Assert.That((await store.SnapshotAsync("owner")).Actors.Count).IsEqualTo(3);
            await store.ExecuteAsync("owner", Request("moderation.mute", new { targetAccountId = "alice" }), CancellationToken.None);
            await Assert.That(await ErrorCodeAsync(store, "alice", Request("dialogue.clear", new { }))).IsEqualTo("muted");
            await Assert.That(await ErrorCodeAsync(store, "isolated", Request("stage.clear", new { }))).IsEqualTo("isolated");
            await store.ExecuteAsync("bob", Request("dialogue.send", new { text = "Stage clear public line" }), CancellationToken.None);
            await store.ExecuteAsync("isolated", Request("dialogue.send", new { text = "Stage clear private line" }), CancellationToken.None);
            var stage = await store.ExecuteAsync("owner", Request("stage.clear", new { }), CancellationToken.None);
            await Assert.That(string.Join(",", stage.Events.Select(entry => entry.Event.Kind))).IsEqualTo("dialogue.cleared,stage.changed");
            await store.AuthenticateAsync(new("isolated", "isolated"), "", "", CancellationToken.None);
            foreach (var id in performers.Prepend("owner"))
            {
                var snapshot = await store.SnapshotAsync(id);
                await Assert.That(snapshot.CurrentDialogue).IsNull();
                await Assert.That(snapshot.SpeakerAccountId).IsNull();
                await Assert.That(snapshot.Actors.Count).IsEqualTo(0);
            }
            await Assert.That((await store.SnapshotAsync("alice")).Inventory.Single().Quantity).IsEqualTo(1);
        });
    }

    [Test]
    public async Task SpendingRetriesAreAtomicAndPrivateStateIsAudienceFiltered()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"vno2-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var store = new CommunityStore(new CommunityOptions { DatabasePath = Path.Combine(directory, "world.db") });
            foreach (var id in new[] { "alice", "bob", "observer" })
            {
                await store.AuthenticateAsync(new CommunityIdentity(id, id), "", "", CancellationToken.None);
                await store.ExecuteAsync(id, Request("room.join", new { roomId = "lobby" }), CancellationToken.None);
            }
            var purchase = Request("shop.buy", new { itemId = "potion", quantity = 1 });
            var first = await store.ExecuteAsync("alice", purchase, CancellationToken.None);
            var retry = await store.ExecuteAsync("alice", purchase, CancellationToken.None);
            var snapshot = await store.SnapshotAsync("alice");
            await Assert.That(retry.Duplicate).IsTrue();
            await Assert.That(JsonSerializer.Serialize(first.Reply)).IsEqualTo(JsonSerializer.Serialize(retry.Reply));
            await Assert.That(snapshot.Resources.Credits).IsEqualTo(90L);
            await Assert.That(snapshot.Inventory.Single(item => item.ItemId == "potion").Quantity).IsEqualTo(1);
            var conflicting = purchase with { Action = "credits.transfer" };
            await Assert.That(await ErrorCodeAsync(store, "alice", conflicting)).IsEqualTo("request_conflict");
            await Assert.That(await ErrorCodeAsync(store, "alice", Request("credits.grant", new { targetAccountId = "bob", amount = 100 }))).IsEqualTo("forbidden");
            await Assert.That((await store.SnapshotAsync("bob")).Resources.Credits).IsEqualTo(100L);
            var replay = await store.ExecuteAsync("observer", Request("replay.get", new { afterSequence = 0 }), CancellationToken.None);
            var events = replay.Reply.Data.GetProperty("result").GetProperty("events");
            await Assert.That(events.EnumerateArray().Any(item => item.GetProperty("kind").GetString() == "inventory.changed")).IsFalse();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task IsolationAndServerCatalogRulesAreEnforcedBeforePublishing()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"vno2-authority-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var store = new CommunityStore(new CommunityOptions
            {
                DatabasePath = Path.Combine(directory, "world.db"),
                AdministratorAccountIds = ["owner"],
            });
            foreach (var id in new[] { "owner", "alice", "bob" })
            {
                await store.AuthenticateAsync(new CommunityIdentity(id, id), "", "", CancellationToken.None);
                await store.ExecuteAsync(id, Request("room.join", new { roomId = "lobby" }), CancellationToken.None);
            }
            await Assert.That(await ErrorCodeAsync(store, "alice", Request("character.select", new { characterId = "unlisted-local-character" }))).IsEqualTo("character_not_permitted");
            await store.ExecuteAsync("owner", Request("moderation.isolate", new { targetAccountId = "alice", enabled = true }), CancellationToken.None);
            var bob = await store.SnapshotAsync("bob");
            var owner = await store.SnapshotAsync("owner");
            await Assert.That(bob.Participants.Any(item => item.AccountId == "alice")).IsFalse();
            await Assert.That(owner.Participants.Any(item => item.AccountId == "alice")).IsTrue();
            await Assert.That(await ErrorCodeAsync(store, "alice", Request("room.join", new { roomId = "stage" }))).IsEqualTo("isolated");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static CommunityRequest Request(string action, object data) => new(1, Guid.NewGuid().ToString("N"), action,
        JsonSerializer.SerializeToElement(data, JsonSerializerOptions.Web));

    private static async Task JoinAsync(CommunityStore store, params string[] accountIds)
    {
        foreach (var id in accountIds)
        {
            await store.AuthenticateAsync(new(id, id), "", "", CancellationToken.None);
            await store.ExecuteAsync(id, Request("room.join", new { roomId = "lobby" }), CancellationToken.None);
        }
    }

    private static async Task WithStoreAsync(Func<CommunityStore, Task> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"vno2-regression-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var store = new CommunityStore(new CommunityOptions { DatabasePath = Path.Combine(directory, "world.db"), AdministratorAccountIds = ["owner"] });
            await check(store);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<string?> ErrorCodeAsync(CommunityStore store, string account, CommunityRequest request)
    {
        try { await store.ExecuteAsync(account, request, CancellationToken.None); }
        catch (DomainException exception) { return exception.Code; }
        return null;
    }
}
