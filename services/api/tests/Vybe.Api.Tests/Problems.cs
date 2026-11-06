using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Vybe.Api.Tests;

internal static class Problems
{
    /// <summary>Checks the RFC 9457 shape of spec §5 and returns the body.</summary>
    public static async Task<JsonElement> AssertAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.Equal((int)status, body.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("traceId").GetString()));
        Assert.False(string.IsNullOrEmpty(body.GetProperty("title").GetString()));
        Assert.Equal("about:blank", body.GetProperty("type").GetString());
        return body;
    }
}
