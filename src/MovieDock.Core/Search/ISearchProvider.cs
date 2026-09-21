using System.Text;
using MovieDock.Core.Models;

namespace MovieDock.Core.Search;

public interface ISearchProvider
{
    string Name { get; }
    Task<(IReadOnlyList<SourceItem> Items, IReadOnlyList<string> Warnings)> SearchAsync(
        SearchRequest req, CancellationToken ct = default);
}
