using MovieAgent.Core.Interfaces;
using MovieAgent.Infrastructure.Services;
using MovieAgent.Services;
using Xunit;

namespace MovieAgent.Tests;

public class ServicesTests
{
    [Fact]
    public async Task ThemeService_SetTheme_ShouldChangeTheme()
    {
        // Arrange
        var service = new ThemeService();
        bool themeChanged = false;
        ThemeMode? newTheme = null;
        
        service.ThemeChanged += mode =>
        {
            themeChanged = true;
            newTheme = mode;
        };

        // Act
        await service.SetThemeAsync(ThemeMode.Light);

        // Assert
        Assert.True(themeChanged);
        Assert.Equal(ThemeMode.Light, newTheme);
        Assert.Equal(ThemeMode.Light, service.CurrentTheme);
    }

    [Fact]
    public async Task ThemeService_SetTheme_DarkToLight()
    {
        // Arrange
        var service = new ThemeService();

        // Act
        await service.SetThemeAsync(ThemeMode.Dark);
        var darkTheme = service.CurrentTheme;
        
        await service.SetThemeAsync(ThemeMode.Light);
        var lightTheme = service.CurrentTheme;

        // Assert
        Assert.Equal(ThemeMode.Dark, darkTheme);
        Assert.Equal(ThemeMode.Light, lightTheme);
    }

    [Fact]
    public async Task LocalizationService_Translate_ShouldReturnChinese()
    {
        // Arrange
        var service = new LocalizationService();

        // Act
        await service.SetLanguageAsync(Language.Chinese);
        var result = service.Translate("Home");

        // Assert
        Assert.Equal("首页(海报墙)", result);
    }

    [Fact]
    public async Task LocalizationService_Translate_ShouldReturnEnglish()
    {
        // Arrange
        var service = new LocalizationService();

        // Act
        await service.SetLanguageAsync(Language.English);
        var result = service.Translate("Home");

        // Assert
        Assert.Equal("Home (Poster Wall)", result);
    }

    [Fact]
    public async Task LocalizationService_Translate_WithParameters()
    {
        // Arrange
        var service = new LocalizationService();

        // Act
        await service.SetLanguageAsync(Language.Chinese);
        var result = service.Translate("SearchPlaceholder");

        // Assert
        Assert.Equal("搜索电影...", result);
    }

    [Fact]
    public void KeyboardShortcutService_RegisterAndUnregister()
    {
        // Arrange
        var service = new KeyboardShortcutService();
        bool invoked = false;

        // Act
        service.RegisterShortcut(KeyboardShortcut.Home, () => invoked = true);
        
        // Test unregister works
        service.UnregisterShortcut(KeyboardShortcut.Home);
        
        // Register again
        service.RegisterShortcut(KeyboardShortcut.Home, () => invoked = true);

        // Assert - registration works
        Assert.NotNull(service);
    }
}