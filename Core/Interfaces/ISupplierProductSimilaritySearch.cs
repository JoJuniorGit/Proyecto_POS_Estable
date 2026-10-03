using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Interfaces;

public interface ISupplierProductSimilaritySearch
{
    Task<IReadOnlyList<SupplierProductSimilarityCandidate>> FindCandidatesAsync(
        string productName,
        double minimumSimilarity,
        CancellationToken cancellationToken = default);
}

public sealed record SupplierProductSimilarityCandidate(int ProductId, double Similarity);
