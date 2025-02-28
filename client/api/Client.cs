using GranitDb.Client.Models;
using Newtonsoft.Json;

namespace GranitDb.Client.api;

public class Client : IDisposable
{
    private static Client? _client;
    private readonly string _baseUrl;
    private readonly HttpClient _httpClient;

    public Client(string baseUrl)
    {
        _baseUrl = baseUrl;
        _httpClient = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        _client = this;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    public static Client? GetClient()
    {
        return _client;
    }

    public async Task<DatabaseInfo[]> GetAllDatabaseAsync()
    {
        var url = $"{_httpClient.BaseAddress}DatabaseInfo";
        var result = await _httpClient.GetAsync(new Uri(url));
        var content = await result.Content.ReadAsStringAsync();
        if (!result.IsSuccessStatusCode || string.IsNullOrEmpty(content)) return [];
        return JsonConvert.DeserializeObject<DatabaseInfo[]>(content) ?? [];
    }
}