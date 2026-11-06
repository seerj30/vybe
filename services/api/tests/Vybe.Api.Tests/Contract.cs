using System.Net;
using System.Text.Json;
using Json.Schema;
using Yaml2JsonNode;

namespace Vybe.Api.Tests;

/// <summary>Validates API responses against the OpenAPI 3.1 contract (packages/contracts).</summary>
public static class Contract
{
    // Static initializers run in declaration order: the document must load before the build options.
    private static readonly Uri BaseUri = new("https://vybe.test/openapi.json");
    private static readonly JsonElement OpenApi = LoadDocument();
    private static readonly BuildOptions Build = CreateBuildOptions();
    private static readonly Dictionary<string, JsonSchema> Schemas = [];
    private static readonly Lock Gate = new();

    /// <summary>Asserts the status and that the JSON body matches <c>#/components/schemas/{schema}</c>.</summary>
    public static async Task<JsonElement> AssertAsync(HttpResponseMessage response, HttpStatusCode status, string schema)
    {
        ArgumentNullException.ThrowIfNull(response);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"expected {(int)status}, got {(int)response.StatusCode}: {text}");
        using var document = JsonDocument.Parse(text);
        var body = document.RootElement.Clone();
        Validate(body, schema);
        return body;
    }

    /// <summary>Asserts a problem response with the given status and <c>code</c>.</summary>
    public static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        ArgumentNullException.ThrowIfNull(response);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await AssertAsync(response, status, "Problem");
        Assert.Equal(code, body.GetProperty("code").GetString());
        return body;
    }

    public static void Validate(JsonElement body, string schema)
    {
        var result = SchemaFor(schema).Evaluate(body, new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });
        if (!result.IsValid)
        {
            var errors = (result.Details ?? [])
                .Where(d => d.Errors is { Count: > 0 })
                .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Key} {e.Value}"));
            Assert.Fail($"response does not match {schema}: {string.Join("; ", errors)}\n{body}");
        }
    }

    private static JsonSchema SchemaFor(string name)
    {
        lock (Gate)
        {
            if (!Schemas.TryGetValue(name, out var schema))
            {
                schema = JsonSchema.FromText(
                    $$"""{"$schema":"https://json-schema.org/draft/2020-12/schema","$ref":"{{BaseUri}}#/components/schemas/{{name}}"}""",
                    Build);
                Schemas[name] = schema;
            }

            return schema;
        }
    }

    private static JsonElement LoadDocument()
    {
        var path = Path.Combine(ApiFactory.RepoRoot, "packages", "contracts", "openapi", "vybe-api.v1.yaml");
        var stream = YamlSerializer.Parse(File.ReadAllText(path));
        var node = YamlConverter.ToJsonNode(stream.Documents[0])!;
        RemoveKeyword(node, "discriminator");
        return JsonSerializer.SerializeToElement(node);
    }

    /// <summary>
    /// OpenAPI's <c>discriminator</c> is not a JSON Schema 2020-12 keyword. It only documents the
    /// choice: each <c>oneOf</c> member already pins its own <c>const</c> status, so removing it keeps
    /// validation exact.
    /// </summary>
    private static void RemoveKeyword(System.Text.Json.Nodes.JsonNode? node, string keyword)
    {
        switch (node)
        {
            case System.Text.Json.Nodes.JsonObject obj:
                obj.Remove(keyword);
                foreach (var (_, child) in obj.ToList())
                {
                    RemoveKeyword(child, keyword);
                }

                break;
            case System.Text.Json.Nodes.JsonArray array:
                foreach (var child in array)
                {
                    RemoveKeyword(child, keyword);
                }

                break;
        }
    }

    private static BuildOptions CreateBuildOptions()
    {
        var registry = new SchemaRegistry();
        registry.Register(BaseUri, new JsonElementBaseDocument(OpenApi, BaseUri));
        return new BuildOptions { SchemaRegistry = registry };
    }
}
