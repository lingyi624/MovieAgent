using Microsoft.EntityFrameworkCore;
using MovieAgent.Core.Entities;
using MovieAgent.Core.Interfaces;
using MovieAgent.Infrastructure.Data;

namespace MovieAgent.Infrastructure.Repositories;

public class CompanyRepository : ICompanyRepository
{
    private readonly AppDbContext _dbContext;

    public CompanyRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<Company?> GetByIdAsync(int id)
    {
        return await _dbContext.Companies.FindAsync(id);
    }

    public async Task<Company?> GetByTmdbIdAsync(string tmdbId)
    {
        return await _dbContext.Companies.FirstOrDefaultAsync(c => c.TmdbId == tmdbId);
    }

    public async Task<List<Company>> SearchByNameAsync(string name)
    {
        return await _dbContext.Companies
            .Where(c => c.Name.Contains(name))
            .OrderByDescending(c => c.Name)
            .Take(20)
            .ToListAsync();
    }

    public async Task<List<Company>> GetAllAsync()
    {
        return await _dbContext.Companies.OrderBy(c => c.Name).ToListAsync();
    }

    public async Task AddAsync(Company company)
    {
        await _dbContext.Companies.AddAsync(company);
        await _dbContext.SaveChangesAsync();
    }

    public async Task UpdateAsync(Company company)
    {
        _dbContext.Companies.Update(company);
        await _dbContext.SaveChangesAsync();
    }

    public async Task DeleteAsync(int id)
    {
        var company = await GetByIdAsync(id);
        if (company != null)
        {
            _dbContext.Companies.Remove(company);
            await _dbContext.SaveChangesAsync();
        }
    }
}