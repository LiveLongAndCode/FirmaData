using System.ComponentModel.DataAnnotations;

namespace FirmaData.Web;

public sealed class ApiOptions
{
    public const string SectionName = "Api";

    [Required]
    public string BaseUrl { get; set; } = "http://localhost:8080/";
}
