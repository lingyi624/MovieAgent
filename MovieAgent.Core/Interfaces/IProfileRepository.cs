using MovieAgent.Core.Entities;

namespace MovieAgent.Core.Interfaces;

public interface IPersonRepository
{
    Task<Person?> GetByIdAsync(int id);
    Task<Person?> GetByTmdbIdAsync(string tmdbId);
    Task<List<Person>> SearchByNameAsync(string name);
    Task<List<Person>> GetAllAsync();
    Task AddAsync(Person person);
    Task UpdateAsync(Person person);
    Task DeleteAsync(int id);
}

public interface ICompanyRepository
{
    Task<Company?> GetByIdAsync(int id);
    Task<Company?> GetByTmdbIdAsync(string tmdbId);
    Task<List<Company>> SearchByNameAsync(string name);
    Task<List<Company>> GetAllAsync();
    Task AddAsync(Company company);
    Task UpdateAsync(Company company);
    Task DeleteAsync(int id);
}