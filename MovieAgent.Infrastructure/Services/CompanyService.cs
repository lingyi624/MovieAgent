using System.Text.Json;
using MovieAgent.Core.Entities;
using MovieAgent.Core.Interfaces;

namespace MovieAgent.Infrastructure.Services;

public class CompanyService : ICompanyService
{
    private readonly ICompanyRepository _companyRepository;
    private readonly ITmdbService _tmdbService;

    public CompanyService(ICompanyRepository companyRepository, ITmdbService tmdbService)
    {
        _companyRepository = companyRepository;
        _tmdbService = tmdbService;
    }

    public async Task<Company?> GetCompanyByIdAsync(int id)
    {
        return await _companyRepository.GetByIdAsync(id);
    }

    public async Task<Company?> GetCompanyByTmdbIdAsync(string tmdbId)
    {
        return await _companyRepository.GetByTmdbIdAsync(tmdbId);
    }

    public async Task<Company?> GetOrCreateCompanyAsync(string tmdbId)
    {
        var existing = await _companyRepository.GetByTmdbIdAsync(tmdbId);
        if (existing != null)
            return existing;

        var tmdbCompany = await ((TmdbService)_tmdbService).GetCompanyAsync(long.Parse(tmdbId));
        if (tmdbCompany == null)
            return null;

        var company = new Company
        {
            TmdbId = tmdbId,
            Name = tmdbCompany.Name,
            Description = tmdbCompany.Description,
            LogoPath = tmdbCompany.LogoPath,
            OriginCountry = tmdbCompany.OriginCountry,
            Headquarters = tmdbCompany.Headquarters,
            Homepage = tmdbCompany.Homepage,
            ParentCompany = tmdbCompany.ParentCompany,
            MovieList = tmdbCompany.MovieList != null ? JsonSerializer.Serialize(tmdbCompany.MovieList) : null,
            PersonList = tmdbCompany.PersonList != null ? JsonSerializer.Serialize(tmdbCompany.PersonList) : null,
            UpdatedAt = DateTime.UtcNow
        };

        await _companyRepository.AddAsync(company);
        return company;
    }

    public async Task<List<Company>> SearchCompaniesAsync(string name)
    {
        return await _companyRepository.SearchByNameAsync(name);
    }

    public async Task<List<Company>> GetAllCompaniesAsync()
    {
        return await _companyRepository.GetAllAsync();
    }

    public async Task UpdateCompanyAsync(Company company)
    {
        company.UpdatedAt = DateTime.UtcNow;
        await _companyRepository.UpdateAsync(company);
    }
}