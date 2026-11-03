using ChemFlow.Domain.Entities;

namespace ChemFlow.Domain.Repositories;

/// <summary>
/// Repository contract for querying and persisting pure chemical compounds.
/// Defined in the Domain layer (Dependency Inversion Principle) and implemented in Infrastructure.
/// </summary>
public interface ICompoundRepository
{
    IReadOnlyList<ChemicalCompound> GetAll();
    ChemicalCompound? GetById(string id);
    IReadOnlyList<ChemicalCompound> GetByIds(IEnumerable<string> ids);
    void Upsert(ChemicalCompound compound);
}
