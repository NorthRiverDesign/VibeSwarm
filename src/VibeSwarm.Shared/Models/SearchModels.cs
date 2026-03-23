namespace VibeSwarm.Shared.Models;

public enum SearchResultType
{
    Project,
    Job,
    Idea
}

public class SearchResultItem
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Subtitle { get; set; }
    public SearchResultType Type { get; set; }
    public string Url { get; set; } = string.Empty;
    public string? StatusLabel { get; set; }
}

public class GlobalSearchResult
{
    public List<SearchResultItem> Items { get; set; } = [];
    public int TotalCount => Items.Count;
}
