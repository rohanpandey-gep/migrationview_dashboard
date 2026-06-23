using System.Text.Json;

namespace MigrationViewDashboard.Services;

public class LogsService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<LogsService> _logger;

    public LogsService(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<LogsService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
    }

    public async Task<int> GetMissedQueueCount(string module, string date, string authorization, HashSet<int> priorityBpcCodes)
    {
        var projectGroupId = _config[$"ExternalApis:ModuleProjectGroups:{module}"];
        if (string.IsNullOrEmpty(projectGroupId) || projectGroupId == "PLACEHOLDER")
        {
            _logger.LogInformation("[{Module}] Skipped - PLACEHOLDER", module);
            return 0;
        }

        var baseUrl = _config["ExternalApis:LogsUrl"]!;
        var subscriptionKey = _config["ExternalApis:SubscriptionKey"]!;

        var timestamp = $"{date}T00:00:00.000Z";
        var url = $"{baseUrl}?pageNumber=1&pageSize=100&projectGroupId={projectGroupId}&timestamp={timestamp}";

        _logger.LogInformation("[{Module}] Calling: GET {Url}", module, url);

        var client = _httpClientFactory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Authorization", authorization);
        request.Headers.Add("Ocp-Apim-Subscription-Key", subscriptionKey);
        request.Headers.Add("Accept", "application/json");

        var response = await client.SendAsync(request);
        _logger.LogInformation("[{Module}] Response status: {StatusCode}", module, (int)response.StatusCode);

        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("[{Module}] Error: {Body}", module, errBody[..Math.Min(500, errBody.Length)]);
            return 0;
        }

        var body = await response.Content.ReadAsStringAsync();
        _logger.LogInformation("[{Module}] Response length: {Len} chars", module, body.Length);

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var apiResponse = JsonSerializer.Deserialize<LogsApiResponse>(body, options);

        if (apiResponse?.ReturnValue?.PublishQueueList == null)
        {
            _logger.LogWarning("[{Module}] PublishQueueList is null", module);
            return 0;
        }

        var entries = apiResponse.ReturnValue.PublishQueueList;
        _logger.LogInformation("[{Module}] Got {Count} entries (totalCount: {Total})", module, entries.Count, apiResponse.ReturnValue.TotalCount);

        var count = 0;
        var targetDate = DateOnly.Parse(date);

        foreach (var entry in entries)
        {
            if (!DateTime.TryParse(entry.Timestamp, out var entryDate))
                continue;

            if (DateOnly.FromDateTime(entryDate) != targetDate)
                continue;

            if (string.Equals(entry.Status, "Published", StringComparison.OrdinalIgnoreCase))
                break;

            if (entry.BpcCode > 0 && priorityBpcCodes.Contains(entry.BpcCode))
                continue;

            count++;
        }

        _logger.LogInformation("[{Module}] Final missed/queue count: {Count}", module, count);
        return count;
    }
}

public class LogsApiResponse
{
    public LogsReturnValue? ReturnValue { get; set; }
    public bool IsSuccess { get; set; }
}

public class LogsReturnValue
{
    public List<LogEntry>? PublishQueueList { get; set; }
    public int TotalCount { get; set; }
}

public class LogEntry
{
    public string? Status { get; set; }
    public string? Timestamp { get; set; }
    public int BpcCode { get; set; }
    public string? BpcName { get; set; }
}
