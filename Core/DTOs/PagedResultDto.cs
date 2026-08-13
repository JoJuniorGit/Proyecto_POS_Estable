namespace Core.DTOs;

public class PagedResultDto<T>
{
    public IEnumerable<T> Items { get; set; } = Enumerable.Empty<T>();
    public int TotalCount { get; set; }
    public bool HasMore { get; set; }
}
