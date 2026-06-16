using MovieAgent.Core.Entities;
using MovieAgent.Core.Interfaces;

namespace MovieAgent.Infrastructure.Services;

public interface ITransactionService
{
    Task<TResult> ExecuteWithTransactionAsync<TResult>(Func<Task<TResult>> operation);
    Task ExecuteWithTransactionAsync(Func<Task> operation);
    Task AddMovieWithVectorAsync(Movie movie, float[] vector);
    Task RemoveMovieWithVectorAsync(int movieId);
}

public class TransactionService : ITransactionService
{
    private readonly IMovieRepository _movieRepo;
    private readonly IVectorDatabaseService _vectorDb;

    public TransactionService(IMovieRepository movieRepo, IVectorDatabaseService vectorDb)
    {
        _movieRepo = movieRepo;
        _vectorDb = vectorDb;
    }

    public async Task<TResult> ExecuteWithTransactionAsync<TResult>(Func<Task<TResult>> operation)
    {
        try
        {
            return await operation();
        }
        catch
        {
            throw;
        }
    }

    public async Task ExecuteWithTransactionAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch
        {
            throw;
        }
    }

    public async Task AddMovieWithVectorAsync(Movie movie, float[] vector)
    {
        bool movieAdded = false;
        bool vectorAdded = false;

        try
        {
            await _movieRepo.AddAsync(movie);
            movieAdded = true;

            if (movie.Id > 0 && vector != null && vector.Length > 0)
            {
                await _vectorDb.AddMovieAsync(movie.Id, vector, movie.Title, movie.Overview);
                vectorAdded = true;
            }
        }
        catch
        {
            if (movieAdded && movie.Id > 0)
            {
                try { await _movieRepo.DeleteAsync(movie.Id); } catch { }
            }
            if (vectorAdded && movie.Id > 0)
            {
                try { await _vectorDb.RemoveMovieAsync(movie.Id); } catch { }
            }
            throw;
        }
    }

    public async Task RemoveMovieWithVectorAsync(int movieId)
    {
        bool movieRemoved = false;

        try
        {
            await _movieRepo.DeleteAsync(movieId);
            movieRemoved = true;

            await _vectorDb.RemoveMovieAsync(movieId);
        }
        catch
        {
            throw;
        }
    }
}