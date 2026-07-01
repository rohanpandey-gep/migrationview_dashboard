namespace MigrationViewDashboard.Models;

public class PriorityGroup
{
    public string DisplayName { get; set; } = string.Empty;
    public List<int> Bpcs { get; set; } = new();
}

public class BpcInfo
{
    public int BpcCode { get; set; }
    public string BpcName { get; set; } = string.Empty;
}

public class ModuleCounts
{
    public string Module { get; set; } = string.Empty;
    public int Published { get; set; }
    public int MissedQueue { get; set; }
    public int Failed { get; set; }
}

public class BpcCard
{
    public int BpcCode { get; set; }
    public string BpcName { get; set; } = string.Empty;
    public List<ModuleCounts> Modules { get; set; } = new();
}

public class PrioritySection
{
    public string DisplayName { get; set; } = string.Empty;
    public List<BpcCard> Cards { get; set; } = new();
}

public class DashboardResponse
{
    public int QueueCount { get; set; }
    public List<PrioritySection> Sections { get; set; } = new();
}

public class SeedResponse
{
    public List<PriorityGroup> PriorityGroups { get; set; } = new();
    public List<string> Modules { get; set; } = new();
    public List<string> Environments { get; set; } = new();
}
