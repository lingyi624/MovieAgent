using System.Text.Json;
using MovieAgent.Core.Entities;
using MovieAgent.Core.Interfaces;

namespace MovieAgent.Infrastructure.Services;

public class PersonService : IPersonService
{
    private readonly IPersonRepository _personRepository;
    private readonly ITmdbService _tmdbService;

    public PersonService(IPersonRepository personRepository, ITmdbService tmdbService)
    {
        _personRepository = personRepository;
        _tmdbService = tmdbService;
    }

    public async Task<Person?> GetPersonByIdAsync(int id)
    {
        return await _personRepository.GetByIdAsync(id);
    }

    public async Task<Person?> GetPersonByTmdbIdAsync(string tmdbId)
    {
        return await _personRepository.GetByTmdbIdAsync(tmdbId);
    }

    public async Task<Person?> GetOrCreatePersonAsync(string tmdbId)
    {
        var existing = await _personRepository.GetByTmdbIdAsync(tmdbId);
        if (existing != null)
            return existing;

        var tmdbPerson = await ((TmdbService)_tmdbService).GetPersonAsync(long.Parse(tmdbId));
        if (tmdbPerson == null)
            return null;

        var person = new Person
        {
            TmdbId = tmdbId,
            Name = tmdbPerson.Name,
            OriginalName = tmdbPerson.OriginalName,
            Biography = tmdbPerson.Biography,
            ProfilePath = tmdbPerson.ProfilePath,
            Birthday = tmdbPerson.Birthday,
            Deathday = tmdbPerson.Deathday,
            PlaceOfBirth = tmdbPerson.PlaceOfBirth,
            Gender = tmdbPerson.Gender == 1 ? "女性" : tmdbPerson.Gender == 2 ? "男性" : "未知",
            KnownForDepartment = tmdbPerson.KnownForDepartment,
            Popularity = tmdbPerson.Popularity,
            AlsoKnownAs = tmdbPerson.AlsoKnownAs != null ? JsonSerializer.Serialize(tmdbPerson.AlsoKnownAs) : null,
            KnownForTitles = tmdbPerson.KnownForTitles != null ? JsonSerializer.Serialize(tmdbPerson.KnownForTitles) : null,
            Credits = tmdbPerson.Credits != null ? JsonSerializer.Serialize(tmdbPerson.Credits) : null,
            UpdatedAt = DateTime.UtcNow
        };

        await _personRepository.AddAsync(person);
        return person;
    }

    public async Task<List<Person>> SearchPersonsAsync(string name)
    {
        return await _personRepository.SearchByNameAsync(name);
    }

    public async Task<List<Person>> GetAllPersonsAsync()
    {
        return await _personRepository.GetAllAsync();
    }

    public async Task UpdatePersonAsync(Person person)
    {
        person.UpdatedAt = DateTime.UtcNow;
        await _personRepository.UpdateAsync(person);
    }
}