using FirmaData.Domain;

namespace FirmaData.Application;

public interface ICompanyDirectory
{
    Task<Result<Company>> GetByCvrAsync(CvrNumber cvr, CancellationToken ct);

    Task<Result<IReadOnlyList<Company>>> SearchByNameAsync(string name, CancellationToken ct);
}
