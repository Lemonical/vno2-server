using System.Text.Json;
using Microsoft.Extensions.Hosting.Internal;
using VNO2.Server;
using VNO2.Server.Domain;
using VNO2.Server.Security;

namespace VNO2.Tests;

public sealed class JoinTicketVerifierTests
{
    [Test]
    public async Task MalformedMasterResponsesFailClosedWhileValidTicketsBindIdentityAndCapacity()
    {
        var serverId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var expiry = DateTimeOffset.UtcNow.AddMinutes(1);
        var environment = new HostingEnvironment { EnvironmentName = "Development" };
        using var handler = new MasterResponseHandler();
        using var client = new HttpClient(handler);
        var verifier = new JoinTicketVerifier(client, new CommunityOptions
        {
            Id = serverId.ToString("D"),
            MasterBaseUrl = "http://127.0.0.1",
            ServerCredential = "local-test-credential"
        }, environment);
        handler.ResponseJson = JsonSerializer.Serialize(new { account = new { id = accountId, displayName = "Alice" }, serverId, expiresAt = expiry, capacity = 42 });
        var accepted = await verifier.VerifyAsync("local-test-ticket", CancellationToken.None);
        await Assert.That(accepted.AccountId).IsEqualTo(accountId.ToString("D"));
        await Assert.That(accepted.DisplayName).IsEqualTo("Alice");
        await Assert.That(accepted.ServerCapacity).IsEqualTo(42);
        var validAccount = new { id = accountId, displayName = "Alice" };
        foreach (var payload in new object?[]
        {
            null, new[] { 1 }, new { },
            new { account = 1, serverId, expiresAt = expiry, capacity = 42 },
            new { account = validAccount, serverId = 1, expiresAt = expiry, capacity = 42 },
            new { account = validAccount, serverId = "not-a-guid", expiresAt = expiry, capacity = 42 },
            new { account = validAccount, serverId = Guid.NewGuid(), expiresAt = expiry, capacity = 42 },
            new { account = validAccount, serverId, expiresAt = "not-a-date", capacity = 42 },
            new { account = validAccount, serverId, expiresAt = 1, capacity = 42 },
            new { account = validAccount, serverId, expiresAt = DateTimeOffset.UtcNow.AddMinutes(-1), capacity = 42 },
            new { account = validAccount, serverId, expiresAt = expiry, capacity = "42" },
            new { account = validAccount, serverId, expiresAt = expiry, capacity = 0 },
            new { account = new { displayName = "Alice" }, serverId, expiresAt = expiry, capacity = 42 },
            new { account = new { id = "not-a-guid", displayName = "Alice" }, serverId, expiresAt = expiry, capacity = 42 },
            new { account = new { id = Guid.Empty, displayName = "Alice" }, serverId, expiresAt = expiry, capacity = 42 },
            new { account = new { id = accountId, displayName = 1 }, serverId, expiresAt = expiry, capacity = 42 },
            new { account = new { id = accountId, displayName = "\t" }, serverId, expiresAt = expiry, capacity = 42 },
            new { account = new { id = accountId, displayName = "Hidden\u0001control" }, serverId, expiresAt = expiry, capacity = 42 }
        })
        {
            handler.ResponseJson = JsonSerializer.Serialize(payload);
            await Assert.That(await ErrorCodeAsync(verifier)).IsEqualTo("authentication_failed");
        }
        foreach (var response in new[] { "{not valid JSON", new string('x', 65537) })
        {
            handler.ResponseJson = response;
            await Assert.That(await ErrorCodeAsync(verifier)).IsEqualTo("authentication_failed");
        }
        environment.EnvironmentName = "Production";
        await Assert.That(await ErrorCodeAsync(verifier)).IsEqualTo("authentication_unavailable");
    }

    private static async Task<string?> ErrorCodeAsync(JoinTicketVerifier verifier)
    {
        try { await verifier.VerifyAsync("local-test-ticket", CancellationToken.None); }
        catch (DomainException exception) { return exception.Code; }
        return null;
    }
}
