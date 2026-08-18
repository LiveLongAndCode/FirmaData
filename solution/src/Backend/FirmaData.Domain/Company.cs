namespace FirmaData.Domain;

// R3: master data = name, address, industry code, employee count.
public sealed record Company(
    CvrNumber Cvr,
    string Name,
    Address Address,
    IndustryCode IndustryCode,
    string IndustryDescription,
    int? EmployeeCount,
    CompanyStatus Status);
