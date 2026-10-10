using System.Net;
using System.Text;

namespace VNO2.Tests;

internal sealed class MasterResponseHandler : HttpMessageHandler
{
    public string ResponseJson { get; set; } = "{}";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ResponseJson, Encoding.UTF8, "application/json") });
}
