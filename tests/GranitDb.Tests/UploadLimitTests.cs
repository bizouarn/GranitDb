using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GranitDb.Tests;

/// <summary><c>Catalog:MaxUploadMb</c> est le seul réglage de la taille d'un envoi.</summary>
public sealed class UploadLimitTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "granitdb-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Theory]
    [InlineData(null, 1024)]
    [InlineData("2000", 2000)]
    public async Task The_multipart_limit_of_aspnet_and_the_announced_limit_follow_the_setting(string? configured, int expected)
    {
        var settings = new Dictionary<string, string> { ["Catalog:DataDirectory"] = _directory };
        if (configured != null) settings["Catalog:MaxUploadMb"] = configured;
        using var factory = new KestrelApiFactory(settings);
        using var client = factory.CreateKestrelClient();

        var capabilities = await client.GetFromJsonAsync<JsonElement>("api/v1/Capabilities");
        var form = factory.Services.GetRequiredService<IOptions<FormOptions>>().Value;

        Assert.Equal(expected, capabilities.GetProperty("maxUploadMb").GetInt32());
        // Sans ce réglage, ASP.NET refuse un formulaire de plus de 128 Mo avant même le contrôle de GranitDb.
        Assert.True(form.MultipartBodyLengthLimit > expected * 1024L * 1024L);
    }
}
