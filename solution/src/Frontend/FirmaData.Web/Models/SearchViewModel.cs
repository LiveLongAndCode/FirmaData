using System.ComponentModel.DataAnnotations;

namespace FirmaData.Web.Models;

// Søg (plan section 15, screen 1): one field for either a CVR number or a company name -- the
// controller auto-detects which (8 digits -> CVR, otherwise a name), so there is nothing here to
// validate beyond "something was entered."
public sealed class SearchViewModel
{
    [Required(ErrorMessage = "Angiv et CVR-nummer eller et firmanavn.")]
    [Display(Name = "CVR-nummer eller firmanavn")]
    public string? Query { get; set; }

    [Display(Name = "År")]
    public int? Year { get; set; }

    public IReadOnlyList<int> AvailableYears { get; set; } = [];

    public string? SearchError { get; set; }
}
