using System.Text.Json;
using System.Text.Json.Nodes;

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

    public async Task<ModuleApiFetchResult> GetEntriesForDate(string module, string date, string environment, string authorization)
    {
        var projectGroupId = _config[$"ExternalApis:ModuleProjectGroups:{module}"];
        if (string.IsNullOrEmpty(projectGroupId) || projectGroupId == "PLACEHOLDER")
        {
            _logger.LogInformation("[{Module}] Skipped - PLACEHOLDER", module);
            return new ModuleApiFetchResult
            {
                Module = module,
                ProjectGroupId = projectGroupId,
                IsSkipped = true,
                MatchedEntries = new List<LogEntry>(),
                PageResponses = new List<ModuleApiPageResponse>()
            };
        }

        var baseUrl = _config["ExternalApis:LogsUrl"]!;
        var subscriptionKey = _config["ExternalApis:SubscriptionKey"]!;
        var targetDate = DateOnly.Parse(date);
        var normalizedEnvironment = environment.Trim().ToUpperInvariant();
        const int pageSize = 100;
        const int maxPages = 50;

        var timestamp = $"{date}T00:00:00.000Z";
        var pageNumber = 1;
        var matchedEntries = new List<LogEntry>();
        var pageResponses = new List<ModuleApiPageResponse>();
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        while (pageNumber <= maxPages)
        {
            var url = $"{baseUrl}?pageNumber={pageNumber}&pageSize={pageSize}&projectGroupId={projectGroupId}&timestamp={timestamp}";

            _logger.LogInformation("[{Module}] Calling: GET {Url}", module, url);

            var client = _httpClientFactory.CreateClient();
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Authorization", authorization);
            request.Headers.Add("Ocp-Apim-Subscription-Key", subscriptionKey);
            request.Headers.Add("Accept", "application/json");

            var response = await client.SendAsync(request);
            _logger.LogInformation("[{Module}] Page {Page} response status: {StatusCode}", module, pageNumber, (int)response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync();
                var snippet = errBody[..Math.Min(500, errBody.Length)];
                throw new HttpRequestException($"[{module}] Logs API failed with {(int)response.StatusCode}: {snippet}", null, response.StatusCode);
            }

            var body = await response.Content.ReadAsStringAsync();
            _logger.LogInformation("[{Module}] Page {Page} response length: {Len} chars", module, pageNumber, body.Length);

            JsonNode? apiResponseNode;
            try
            {
                apiResponseNode = JsonNode.Parse(body);
            }
            catch
            {
                apiResponseNode = null;
            }

            var apiResponse = JsonSerializer.Deserialize<LogsApiResponse>(body, options);
            var entries = apiResponse?.ReturnValue?.PublishQueueList;

            pageResponses.Add(new ModuleApiPageResponse
            {
                PageNumber = pageNumber,
                StatusCode = (int)response.StatusCode,
                EntryCount = entries?.Count ?? 0,
                TotalCount = apiResponse?.ReturnValue?.TotalCount ?? 0,
                ApiResponse = apiResponseNode,
                RawBody = apiResponseNode == null ? body : null
            });

            if (entries == null || entries.Count == 0)
            {
                _logger.LogInformation("[{Module}] Page {Page} empty - stopping", module, pageNumber);
                break;
            }

            _logger.LogInformation("[{Module}] Page {Page} got {Count} entries (totalCount: {Total})", module, pageNumber, entries.Count, apiResponse!.ReturnValue!.TotalCount);

            var hasOlderThanTarget = false;

            foreach (var entry in entries)
            {
                if (!DateTime.TryParse(entry.Timestamp, out var entryDate))
                    continue;

                var entryDateOnly = DateOnly.FromDateTime(entryDate);
                if (entryDateOnly < targetDate)
                {
                    hasOlderThanTarget = true;
                    continue;
                }

                if (entryDateOnly != targetDate)
                    continue;

                if (!string.Equals((entry.Environment ?? string.Empty).Trim(), normalizedEnvironment, StringComparison.OrdinalIgnoreCase))
                    continue;

                matchedEntries.Add(entry);
            }

            if (hasOlderThanTarget)
            {
                _logger.LogInformation("[{Module}] Page {Page} has entries older than target date - stopping", module, pageNumber);
                break;
            }

            pageNumber++;
        }

        _logger.LogInformation("[{Module}] Final matched entries for {Date}/{Environment}: {Count}", module, date, normalizedEnvironment, matchedEntries.Count);
        return new ModuleApiFetchResult
        {
            Module = module,
            ProjectGroupId = projectGroupId,
            IsSkipped = false,
            MatchedEntries = matchedEntries,
            PageResponses = pageResponses
        };
    }

    public static string NormalizeStatus(string? status)
    {
        if (string.Equals(status, "Published", StringComparison.OrdinalIgnoreCase))
            return "Published";

        if (string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase))
            return "Failed";

        return "MissedQueue";
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
    public string? Environment { get; set; }
}

public class ModuleApiFetchResult
{
    public string Module { get; set; } = string.Empty;
    public string? ProjectGroupId { get; set; }
    public bool IsSkipped { get; set; }
    public List<LogEntry> MatchedEntries { get; set; } = new();
    public List<ModuleApiPageResponse> PageResponses { get; set; } = new();
}

public class ModuleApiPageResponse
{
    public int PageNumber { get; set; }
    public int StatusCode { get; set; }
    public int EntryCount { get; set; }
    public int TotalCount { get; set; }
    public JsonNode? ApiResponse { get; set; }
    public string? RawBody { get; set; }
}
