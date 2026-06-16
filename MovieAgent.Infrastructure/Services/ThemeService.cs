using MovieAgent.Core.Interfaces;

namespace MovieAgent.Infrastructure.Services;

public class ThemeService : IThemeService
{
    private const string ThemeKey = "MovieAgent_Theme";
    private ThemeMode _currentTheme = ThemeMode.Dark;

    public ThemeMode CurrentTheme => _currentTheme;

    public event Action<ThemeMode>? ThemeChanged;

    public ThemeService()
    {
        LoadTheme();
    }

    public void LoadTheme()
    {
        try
        {
            var saved = Environment.GetEnvironmentVariable(ThemeKey, EnvironmentVariableTarget.User);
            if (!string.IsNullOrEmpty(saved) && Enum.TryParse<ThemeMode>(saved, out var theme))
            {
                _currentTheme = theme;
            }
            else
            {
                _currentTheme = ThemeMode.Dark;
                Environment.SetEnvironmentVariable(ThemeKey, ThemeMode.Dark.ToString(), EnvironmentVariableTarget.User);
            }
        }
        catch
        {
            _currentTheme = ThemeMode.Dark;
        }
    }

    public async Task LoadThemeAsync()
    {
        await Task.Run(LoadTheme);
    }

    public async Task SetThemeAsync(ThemeMode theme)
    {
        _currentTheme = theme;
        await Task.Run(() =>
        {
            try
            {
                Environment.SetEnvironmentVariable(ThemeKey, theme.ToString(), EnvironmentVariableTarget.User);
            }
            catch { }
        });
        ThemeChanged?.Invoke(theme);
    }
}