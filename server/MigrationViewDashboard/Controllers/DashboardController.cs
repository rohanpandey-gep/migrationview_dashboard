using System.Text.Json;
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
        var dataPath = Path.Combine(_env.ContentRootPath, "data");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        var priorityGroupsJson = System.IO.File.ReadAllText(Path.Combine(dataPath, "priorityGroups.json"));
        var modulesJson = System.IO.File.ReadAllText(Path.Combine(dataPath, "modules.json"));

        var allGroups = JsonSerializer.Deserialize<Dictionary<string, List<PriorityGroup>>>(priorityGroupsJson, options) ?? new();
        var modules = JsonSerializer.Deserialize<List<string>>(modulesJson, options) ?? new();

        var envKey = environment.ToUpper();
        var priorityGroups = allGroups.ContainsKey(envKey) ? allGroups[envKey] : allGroups.Values.FirstOrDefault() ?? new();

        // Collect all priority BPC codes (to exclude from "Rest of Domains" count)
        var priorityBpcCodes = priorityGroups
            .Where(pg => pg.Bpcs.Count > 0)
            .SelectMany(pg => pg.Bpcs)
            .ToHashSet();

        // TODO: Replace with real API call that returns all BPCs with codes and names
        var allBpcs = new List<BpcInfo>
        {
            new() { BpcCode = 70022010, BpcName = "Chevron" },
            new() { BpcCode = 70021828, BpcName = "LeoSMB" },
            new() { BpcCode = 70022201, BpcName = "BOFA" },
            new() { BpcCode = 70019999, BpcName = "Shell" },
            new() { BpcCode = 70018888, BpcName = "TotalEnergies" }
        };

        // Fetch missed/queue counts for "Rest of Domains" from logs API
        var restModuleCounts = new Dictionary<string, int>();
        if (!string.IsNullOrEmpty(authorization) && !string.IsNullOrEmpty(date))
        {
            var tasks = modules.Select(async m =>
            {
                var count = await _logsService.GetMissedQueueCount(m, date, authorization, priorityBpcCodes);
                return (Module: m, Count: count);
            });
            var results = await Task.WhenAll(tasks);
            foreach (var r in results)
                restModuleCounts[r.Module] = r.Count;
        }

        var sections = priorityGroups.Select(pg =>
        {
            List<BpcCard> cards;
            if (pg.Bpcs.Count > 0)
            {
                cards = pg.Bpcs
                    .Select(code => allBpcs.FirstOrDefault(b => b.BpcCode == code))
                    .Where(b => b != null)
                    .Select(b => BuildCard(b!, modules))
                    .ToList();
            }
            else
            {
                // "Rest of the Domains" — single card with aggregated counts
                cards = new List<BpcCard>
                {
                    new BpcCard
                    {
                        BpcCode = 0,
                        BpcName = "All Other Domains",
                        Modules = modules.Select(m => new ModuleCounts
                        {
                            Module = m,
                            Published = 0,
                            MissedQueue = restModuleCounts.GetValueOrDefault(m, 0)
                        }).ToList()
                    }
                };
            }

            return new PrioritySection
            {
                DisplayName = pg.DisplayName,
                Cards = cards
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

    private static BpcCard BuildCard(BpcInfo bpc, List<string> modules)
    {
        return new BpcCard
        {
            BpcCode = bpc.BpcCode,
            BpcName = bpc.BpcName,
            Modules = modules.Select(m => new ModuleCounts
            {
                Module = m,
                Published = 0,
                MissedQueue = 0
            }).ToList()
        };
    }
}
