using System.ComponentModel.DataAnnotations;

namespace FirmaData.Statbank;

public sealed class StatbankOptions
{
    public const string SectionName = "Statbank";

    [Required]
    public string BaseUrl { get; set; } = "https://api.statbank.dk/";

    // Used only when GetAvailableYearsAsync's live call to /v1/tableinfo fails -- ERHV1's
    // actual published range is otherwise discovered dynamically (plan section 4.2).
    public int FallbackYear { get; set; } = 2022;
}
