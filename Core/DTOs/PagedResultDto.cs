using System.Collections.Generic;
using System.Linq;

namespace Core.DTOs;

public class PagedResultDto<T>
{
    public IEnumerable<T> Items { get; set; } = Enumerable.Empty<T>();
    public int TotalCount { get; set; }
    public bool HasMore { get; set; }

    public PagedResultDto() { }

    public PagedResultDto(IEnumerable<T> items, int totalCount, bool hasMore = false)
    {
        Items = items;
        TotalCount = totalCount;
        HasMore = hasMore;
    }
}
