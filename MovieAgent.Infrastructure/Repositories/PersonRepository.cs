using Microsoft.EntityFrameworkCore;
using MovieAgent.Core.Entities;
using MovieAgent.Core.Interfaces;
using MovieAgent.Infrastructure.Data;

namespace MovieAgent.Infrastructure.Repositories;

public class PersonRepository : IPersonRepository
{
    private readonly AppDbContext _dbContext;

    public PersonRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<Person?> GetByIdAsync(int id)
    {
        return await _dbContext.Persons.FindAsync(id);
    }

    public async Task<Person?> GetByTmdbIdAsync(string tmdbId)
    {
        return await _dbContext.Persons.FirstOrDefaultAsync(p => p.TmdbId == tmdbId);
    }

    public async Task<List<Person>> SearchByNameAsync(string name)
    {
        return await _dbContext.Persons
            .Where(p => p.Name.Contains(name) || p.OriginalName.Contains(name))
            .OrderByDescending(p => p.Popularity)
            .Take(20)
            .ToListAsync();
    }

    public async Task<List<Person>> GetAllAsync()
    {
        return await _dbContext.Persons.OrderByDescending(p => p.Popularity).ToListAsync();
    }

    public async Task AddAsync(Person person)
    {
        await _dbContext.Persons.AddAsync(person);
        await _dbContext.SaveChangesAsync();
    }

    public async Task UpdateAsync(Person person)
    {
        _dbContext.Persons.Update(person);
        await _dbContext.SaveChangesAsync();
    }

    public async Task DeleteAsync(int id)
    {
        var person = await GetByIdAsync(id);
        if (person != null)
        {
            _dbContext.Persons.Remove(person);
            await _dbContext.SaveChangesAsync();
        }
    }
}