using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Circularity;

namespace EngineSmokeTest;

/// <summary>Checks the anonymous daily count against a fake server: what is sent, and that a failure never throws.</summary>
internal static class UsagePingTest
{
    private const string Url = "https://usage.test/ping";

    public static async Task Run(Action<string, bool, string> check)
    {
        HttpRequestMessage? seen = null;
        string body = "";
        var ok = new UsagePing(new HttpClient(new FakeHandler(request =>
        {
            seen = request;
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(System.Net.HttpStatusCode.NoContent);
        })), Url);

        bool sent = await ok.SendAsync("test-id");
        check("usage count: sent as a POST to the given URL",
            sent && seen?.Method == HttpMethod.Post && seen.RequestUri?.ToString() == Url, $"{seen?.Method} {seen?.RequestUri}");

        var v = UpdateChecker.CurrentVersion;
        string? id = null, version = null;
        int keys = -1;
        try
        {
            using var json = JsonDocument.Parse(body);
            keys = json.RootElement.EnumerateObject().Count();
            id = json.RootElement.GetProperty("id").GetString();
            version = json.RootElement.GetProperty("version").GetString();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException)
        {
        }
        check("usage count: body is exactly the ID and the version",
            keys == 2 && id == "test-id" && version == $"{v.Major}.{v.Minor}.{v.Build}", body);
        check("usage count: sent as JSON", seen?.Content?.Headers.ContentType?.MediaType == "application/json", "");

        var rejected = new UsagePing(new HttpClient(new FakeHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError))), Url);
        check("usage count: a rejected count returns false", !await rejected.SendAsync("test-id"), "");

        var failing = new UsagePing(new HttpClient(new FakeHandler(_ => throw new HttpRequestException("offline"))), Url);
        bool threw = false, result = true;
        try
        {
            result = await failing.SendAsync("test-id");
        }
        catch (Exception)
        {
            threw = true;
        }
        check("usage count: a network failure returns false without throwing", !threw && !result, "");
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
