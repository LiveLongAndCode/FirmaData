using System.ComponentModel.DataAnnotations;

namespace FirmaData.Cvr;

public sealed class CvrOptions
{
    public const string SectionName = "Cvr";

    [Required]
    public string BaseUrl { get; set; } = "https://apicvr.dk/";
}
