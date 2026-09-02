namespace Nvm.Kernel.Identity;

/// <summary>
/// Loại production unit được serialize, mã hóa thành một ký tự trong một <see cref="SerialNumber"/>.
/// </summary>
/// <remarks>
/// Sản phẩm NV-C100-LFP-CTP không có tier module, nên một plant có thể không bao giờ sản xuất một
/// <see cref="Module"/>. Genealogy graph quyết định tier nào tồn tại, không phải enum này.
/// </remarks>
public enum ProductionUnitKind
{
    /// <summary>Unit serialize nhỏ nhất. Code khắc mang ký tự 'C'.</summary>
    Cell,

    /// <summary>Nhóm cell với một frame và busbar. Code khắc mang ký tự 'M'.</summary>
    Module,

    /// <summary>Cụm lắp ráp giao hàng chứa module hoặc, với cell-to-pack, chứa cell. Code khắc mang ký tự 'P'.</summary>
    Pack,
}
