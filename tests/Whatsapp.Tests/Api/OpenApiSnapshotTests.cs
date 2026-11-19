using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Whatsapp.Tests.Support;

namespace Whatsapp.Tests.Api;

/// <summary>
/// The OpenAPI document is committed as <c>docs/openapi.json</c>. This test fails when the generated document differs.
/// Regenerate the file with: <c>UPDATE_OPENAPI=1 dotnet test --filter "FullyQualifiedName~OpenApiSnapshotTests"</c>
/// (PowerShell: <c>$env:UPDATE_OPENAPI=1; dotnet test --filter "FullyQualifiedName~OpenApiSnapshotTests"</c>).
/// </summary>
public sealed class OpenApiSnapshotTests
{
    private const string WebhookPath = "/api/v1/webhooks/whatsapp";

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    [Fact]
    public async Task The_generated_openapi_document_matches_the_committed_file()
    {
        using var factory = new ApiFactory();
        using var admin = factory.Admin();

        var response = await admin.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var generated = Normalise(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var path = Path.Combine(FindRepositoryRoot(), "docs", "openapi.json");
        if (Environment.GetEnvironmentVariable("UPDATE_OPENAPI") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, generated, TestContext.Current.CancellationToken);
            return;
        }

        File.Exists(path).ShouldBeTrue("docs/openapi.json is missing. Regenerate it with UPDATE_OPENAPI=1 (see the class summary).");
        var committed = Normalise(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        generated.ShouldBe(committed, "docs/openapi.json is out of date. Regenerate it with UPDATE_OPENAPI=1 (see the class summary).");
    }

    [Fact]
    public async Task The_openapi_document_is_not_served_without_a_key()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClientWithKey(null);

        (await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Every_operation_documents_its_security_and_error_responses()
    {
        using var factory = new ApiFactory();
        using var admin = factory.Admin();
        var document = JsonNode.Parse(await admin.GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken))!;

        document["components"]!["securitySchemes"]!["ApiKey"]!["name"]!.GetValue<string>().ShouldBe("X-Api-Key");
        document["security"]!.AsArray().Count.ShouldBe(1);
        var paths = document["paths"]!.AsObject();
        paths.Count.ShouldBe(7);
        var operations = 0;
        foreach (var (path, item) in paths)
        {
            path.ShouldStartWith("/api/v1/");
            foreach (var (_, operation) in item!.AsObject())
            {
                operations++;
                if (path == WebhookPath)
                {
                    // The provider is the caller here: no API key, and a delivery is authenticated by its signature.
                    operation!["security"]!.AsArray().ShouldBeEmpty($"{path} is not behind an API key");
                    continue;
                }

                operation!["responses"]!.AsObject().ContainsKey("401").ShouldBeTrue($"{path} documents 401");
            }
        }

        operations.ShouldBe(9);
    }

    private static string Normalise(string json)
    {
        var node = JsonNode.Parse(json)!;
        return node.ToJsonString(Pretty).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n";
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WhatsappNotifications.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("The repository root (WhatsappNotifications.slnx) was not found.");
    }
}
