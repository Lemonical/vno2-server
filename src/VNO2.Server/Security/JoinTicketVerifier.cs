using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using VNO2.Contracts.Community;
using VNO2.Server.Domain;

namespace VNO2.Server.Security;

public sealed class JoinTicketVerifier(HttpClient client, CommunityOptions options, IHostEnvironment environment)
{
    public async Task<CommunityIdentity> VerifyAsync(string ticket, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(options.MasterBaseUrl, UriKind.Absolute, out var master) ||
            master.Scheme != "https" && !(environment.IsDevelopment() && master.IsLoopback && master.Scheme == "http") ||
            !Guid.TryParse(options.Id, out var serverId) || options.ServerCredential.Length == 0)
            throw new DomainException("authentication_unavailable", "The community's master authentication is not configured");
        if (master.UserInfo.Length != 0 || master.Query.Length != 0 || master.Fragment.Length != 0)
            throw new DomainException("authentication_unavailable", "The configured master address is invalid");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(master, "/api/v1/tickets/validate"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ServerCredential);
        request.Content = JsonContent.Create(new { ticket, serverId }, options: ProtocolJson.Options);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) throw new DomainException("authentication_failed", "The join ticket was rejected or has expired");
            if (response.Content.Headers.ContentLength > 65536) throw new DomainException("authentication_failed", "The master response is too large");
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            var bytes = new byte[65537];
            var length = 0;
            while (length < bytes.Length)
            {
                var read = await body.ReadAsync(bytes.AsMemory(length), cancellationToken);
                if (read == 0) break;
                length += read;
            }
            if (length > 65536) throw new DomainException("authentication_failed", "The master response is too large");
            var payload = JsonSerializer.Deserialize<JsonElement>(bytes.AsSpan(0, length), ProtocolJson.Options);
            if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("account", out var account) || account.ValueKind != JsonValueKind.Object ||
                !payload.TryGetProperty("serverId", out var boundServer) || boundServer.ValueKind != JsonValueKind.String || !boundServer.TryGetGuid(out var audience) ||
                audience != serverId || !payload.TryGetProperty("expiresAt", out var expiry) || expiry.ValueKind != JsonValueKind.String ||
                !expiry.TryGetDateTimeOffset(out var expiresAt) || expiresAt <= DateTimeOffset.UtcNow)
                throw new DomainException("authentication_failed", "The join ticket response is invalid");
            if (!payload.TryGetProperty("capacity", out var capacityValue) || capacityValue.ValueKind != JsonValueKind.Number ||
                !capacityValue.TryGetInt32(out var capacity) || capacity is < 1 or > 10000)
                throw new DomainException("authentication_failed", "The master response must include a valid registered capacity");
            if (!account.TryGetProperty("id", out var accountId) || accountId.ValueKind != JsonValueKind.String || !accountId.TryGetGuid(out var id) || id == Guid.Empty ||
                !account.TryGetProperty("displayName", out var displayName) || displayName.ValueKind != JsonValueKind.String)
                throw new DomainException("authentication_failed", "The account identity is invalid");
            var name = displayName.GetString();
            if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(char.IsControl))
                throw new DomainException("authentication_failed", "The account display name is invalid");
            return new(id.ToString("D"), name) { ServerCapacity = capacity };
        }
        catch (HttpRequestException) { throw new DomainException("authentication_unavailable", "The master service could not be reached"); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new DomainException("authentication_unavailable", "The master service did not respond in time"); }
        catch (JsonException) { throw new DomainException("authentication_failed", "The master response is invalid"); }
    }
}
