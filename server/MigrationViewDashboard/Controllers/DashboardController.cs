using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using MigrationViewDashboard.Models;
using MigrationViewDashboard.Services;

namespace MigrationViewDashboard.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DashboardController : ControllerBase
{
    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LogsService _logsService;

    public DashboardController(IWebHostEnvironment env, IConfiguration config, IHttpClientFactory httpClientFactory, LogsService logsService)
    {
        _env = env;
        _config = config;
        _httpClientFactory = httpClientFactory;
        _logsService = logsService;
    }

    [HttpGet("seed")]
    public ActionResult<SeedResponse> Seed()
    {
        var dataPath = Path.Combine(_env.ContentRootPath, "data");

        var priorityGroupsJson = System.IO.File.ReadAllText(Path.Combine(dataPath, "priorityGroups.json"));
        var modulesJson = System.IO.File.ReadAllText(Path.Combine(dataPath, "modules.json"));

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var allGroups = JsonSerializer.Deserialize<Dictionary<string, List<PriorityGroup>>>(priorityGroupsJson, options) ?? new();
        var modules = JsonSerializer.Deserialize<List<string>>(modulesJson, options) ?? new();

        return Ok(new SeedResponse
        {
            PriorityGroups = allGroups.Values.FirstOrDefault() ?? new(),
            Modules = modules,
            Environments = new List<string> { "QC", "UAT", "PROD" }
        });
    }

    [HttpGet("data")]
    public async Task<ActionResult<DashboardResponse>> GetData(
        [FromQuery] string environment,
        [FromQuery] string? date,
        [FromHeader(Name = "X-Auth-Token")] string? authorization)
    {
        if (string.IsNullOrWhiteSpace(environment) || string.IsNullOrWhiteSpace(date))
            return BadRequest(new { error = "Both environment and date are required." });

        if (string.IsNullOrWhiteSpace(authorization))
            return BadRequest(new { error = "X-Auth-Token header is required." });

        var dataPath = Path.Combine(_env.ContentRootPath, "data");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        var priorityGroupsJson = System.IO.File.ReadAllText(Path.Combine(dataPath, "priorityGroups.json"));
        var modulesJson = System.IO.File.ReadAllText(Path.Combine(dataPath, "modules.json"));

        var allGroups = JsonSerializer.Deserialize<Dictionary<string, List<PriorityGroup>>>(priorityGroupsJson, options) ?? new();
        var modules = JsonSerializer.Deserialize<List<string>>(modulesJson, options) ?? new();

        var envKey = environment.ToUpper();
        var priorityGroups = allGroups.ContainsKey(envKey) ? allGroups[envKey] : allGroups.Values.FirstOrDefault() ?? new();

        var priorityBpcCodes = priorityGroups
            .Where(pg => pg.Bpcs.Count > 0)
            .SelectMany(pg => pg.Bpcs)
            .ToHashSet();

        var moduleIndexByName = modules
            .Select((module, index) => (module, index))
            .ToDictionary(x => x.module, x => x.index, StringComparer.OrdinalIgnoreCase);

        var priorityGroupByCode = priorityGroups
            .Where(pg => pg.Bpcs.Count > 0)
            .SelectMany(pg => pg.Bpcs.Select(code => new { code, group = pg.DisplayName }))
            .ToDictionary(x => x.code, x => x.group);

        var priorityCardsByGroup = new Dictionary<string, Dictionary<int, BpcCard>>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in priorityGroups.Where(pg => pg.Bpcs.Count > 0))
        {
            priorityCardsByGroup[group.DisplayName] = new Dictionary<int, BpcCard>();
        }

        var restCard = new BpcCard
        {
            BpcCode = 0,
            BpcName = "All Other Domains",
            Modules = modules.Select(CreateModuleCounts).ToList()
        };

        try
        {
            var tasks = modules.Select(async module =>
            {
                return await _logsService.GetEntriesForDate(module, date!, envKey, authorization!);
            });

            var results = await Task.WhenAll(tasks);

            WriteCombinedApiLog(date!, envKey, results);

            foreach (var moduleResult in results)
            {
                if (!moduleIndexByName.TryGetValue(moduleResult.Module, out var moduleIndex))
                    continue;

                foreach (var entry in moduleResult.MatchedEntries)
                {
                    var targetCard = ResolveTargetCard(
                        entry,
                        modules,
                        priorityBpcCodes,
                        priorityGroupByCode,
                        priorityCardsByGroup,
                        restCard);

                    if (targetCard == null)
                        continue;

                    var status = LogsService.NormalizeStatus(entry.Status);
                    var counts = targetCard.Modules[moduleIndex];

                    if (status == "Published")
                    {
                        counts.Published++;
                    }
                    else if (status == "Failed")
                    {
                        counts.Failed++;
                    }
                    else
                    {
                        counts.MissedQueue++;
                    }
                }
            }
        }
        catch (HttpRequestException ex)
        {
            if (ex.StatusCode.HasValue)
                return StatusCode((int)ex.StatusCode.Value, new { error = ex.Message });

            return StatusCode(502, new { error = ex.Message });
        }

        var sections = priorityGroups.Select(pg =>
        {
            if (pg.Bpcs.Count == 0)
            {
                return new PrioritySection
                {
                    DisplayName = pg.DisplayName,
                    Cards = new List<BpcCard> { restCard }
                };
            }

            var cardsByCode = priorityCardsByGroup.GetValueOrDefault(pg.DisplayName) ?? new Dictionary<int, BpcCard>();
            var orderedCards = pg.Bpcs
                .Where(code => cardsByCode.ContainsKey(code))
                .Select(code => cardsByCode[code])
                .ToList();

            return new PrioritySection
            {
                DisplayName = pg.DisplayName,
                Cards = orderedCards
            };
        }).ToList();

        return Ok(new DashboardResponse
        {
            QueueCount = 0,
            Sections = sections
        });
    }

    [HttpGet("queue")]
    public async Task<ActionResult> GetQueueCount([FromHeader(Name = "X-Auth-Token")] string authorization)
    {
        var client = _httpClientFactory.CreateClient();
        var url = _config["ExternalApis:QueueCountUrl"]!;
        var subscriptionKey = _config["ExternalApis:SubscriptionKey"]!;

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Authorization", authorization);
        request.Headers.Add("Ocp-Apim-Subscription-Key", subscriptionKey);
        request.Headers.Add("Accept", "application/json");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            return StatusCode((int)response.StatusCode, new { error = body });
        }

        return Content(body, "application/json");
    }

    private static BpcCard? ResolveTargetCard(
        LogEntry entry,
        List<string> modules,
        HashSet<int> priorityBpcCodes,
        Dictionary<int, string> priorityGroupByCode,
        Dictionary<string, Dictionary<int, BpcCard>> priorityCardsByGroup,
        BpcCard restCard)
    {
        if (entry.BpcCode > 0 && priorityBpcCodes.Contains(entry.BpcCode) && priorityGroupByCode.TryGetValue(entry.BpcCode, out var groupName))
        {
            var cardsByCode = priorityCardsByGroup[groupName];
            if (!cardsByCode.TryGetValue(entry.BpcCode, out var card))
            {
                card = new BpcCard
                {
                    BpcCode = entry.BpcCode,
                    BpcName = string.IsNullOrWhiteSpace(entry.BpcName) ? entry.BpcCode.ToString() : entry.BpcName,
                    Modules = modules.Select(CreateModuleCounts).ToList()
                };

                cardsByCode[entry.BpcCode] = card;
            }

            return card;
        }

        return restCard;
    }

    private static ModuleCounts CreateModuleCounts(string module)
    {
        return new ModuleCounts
        {
            Module = module,
            Published = 0,
            MissedQueue = 0,
            Failed = 0
        };
    }

    private void WriteCombinedApiLog(string date, string environment, IEnumerable<ModuleApiFetchResult> results)
    {
        var logDirectory = Path.Combine(_env.ContentRootPath, "logs");
        Directory.CreateDirectory(logDirectory);

        var outputPath = Path.Combine(logDirectory, "latest_dashboard_api_responses.json");

        var payload = new
        {
            generatedAtUtc = DateTime.UtcNow,
            request = new
            {
                environment,
                date,
                endpoint = "/api/dashboard/data"
            },
            modules = results.Select(r => new
            {
                r.Module,
                r.ProjectGroupId,
                r.IsSkipped,
                fetchedPages = r.PageResponses.Count,
                matchedEntries = r.MatchedEntries.Count,
                pages = r.PageResponses.Select(p => new
                {
                    p.PageNumber,
                    p.StatusCode,
                    p.EntryCount,
                    p.TotalCount,
                    p.ApiResponse,
                    p.RawBody
                })
            })
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        System.IO.File.WriteAllText(outputPath, json);
    }
}
