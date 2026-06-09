using MovieAgent.Core.Interfaces;

namespace MovieAgent.Infrastructure.Services;

public class ThemeService : IThemeService
{
    private const string ThemeKey = "MovieAgent_Theme";
    private ThemeMode _currentTheme = ThemeMode.Dark;

    public ThemeMode CurrentTheme => _currentTheme;

    public event Action<ThemeMode>? ThemeChanged;

    public async Task LoadThemeAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                var saved = Environment.GetEnvironmentVariable(ThemeKey);
                if (Enum.TryParse<ThemeMode>(saved, out var theme))
                {
                    _currentTheme = theme;
                }
            }
            catch
            {
                _currentTheme = ThemeMode.Dark;
            }
        });
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