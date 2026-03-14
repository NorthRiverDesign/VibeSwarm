namespace VibeSwarm.Shared.Models;

public class PagedResult<T>
{
	public List<T> Items { get; set; } = new();

	public int PageNumber { get; set; } = 1;

	public int PageSize { get; set; }

	public int TotalCount { get; set; }

	public int TotalPages => PageSize <= 0 || TotalCount <= 0
		? 0
		: (int)Math.Ceiling(TotalCount / (double)PageSize);

	public bool HasPreviousPage => PageNumber > 1;

	public bool HasNextPage => PageNumber < TotalPages;

	public int StartItem => TotalCount == 0 ? 0 : ((PageNumber - 1) * PageSize) + 1;

	public int EndItem => TotalCount == 0 ? 0 : Math.Min(PageNumber * PageSize, TotalCount);
}
