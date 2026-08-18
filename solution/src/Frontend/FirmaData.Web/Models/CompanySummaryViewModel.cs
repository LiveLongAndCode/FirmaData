namespace FirmaData.Web.Models;

// Resultater (plan section 15, screen 2): name, CVR, city, industry -- nothing else.
public sealed record CompanySummaryViewModel(string CvrNumber, string Name, string City, string IndustryDescription);
