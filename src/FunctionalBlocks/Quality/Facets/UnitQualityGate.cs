using Nvm.Contracts.Queries;
using Nvm.Quality.Entities;

namespace Nvm.Quality.Facets;

/// <summary>Luật facet: trạng thái nào của Quality chặn unit ở mọi FB khác.</summary>
public static class UnitQualityGate
{
    /// <summary>Held và Scrapped chặn xử lý; Rework vẫn đi tiếp theo route rework của MRB.</summary>
    public static UnitQualityFacet Describe(QualityState state) => state switch
    {
        QualityState.Held => new(state.ToString(), QualityReasonCodes.QualityHold),
        QualityState.Scrapped => new(state.ToString(), QualityReasonCodes.Scrapped),
        _ => new(state.ToString(), null)
    };

    /// <summary>Không hạ Scrapped về Held: loại bỏ là quyết định cuối.</summary>
    public static bool CanQuarantine(QualityState current) => current != QualityState.Scrapped;
}
