using MovieAgent.Core.Entities;

namespace MovieAgent.Core.Interfaces;

public interface IPersonService
{
    Task<Person?> GetPersonByIdAsync(int id);
    Task<Person?> GetPersonByTmdbIdAsync(string tmdbId);
    Task<Person?> GetOrCreatePersonAsync(string tmdbId);
    Task<List<Person>> SearchPersonsAsync(string name);
    Task<List<Person>> GetAllPersonsAsync();
    Task UpdatePersonAsync(Person person);
}

public interface ICompanyService
{
    Task<Company?> GetCompanyByIdAsync(int id);
    Task<Company?> GetCompanyByTmdbIdAsync(string tmdbId);
    Task<Company?> GetOrCreateCompanyAsync(string tmdbId);
    Task<List<Company>> SearchCompaniesAsync(string name);
    Task<List<Company>> GetAllCompaniesAsync();
    Task UpdateCompanyAsync(Company company);
}