namespace FirmaData.Contracts;

// R3: master data = name, address, industry code, employee count.
public sealed record CompanyDto(
    string CvrNumber,
    string Name,
    AddressDto Address,
    string IndustryCode,
    string IndustryDescription,
    int? EmployeeCount);
