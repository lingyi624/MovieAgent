using MovieAgent.Core.Entities;
using MovieAgent.Infrastructure.Services;
using Xunit;

namespace MovieAgent.Tests;

public class MovieScannerTests
{
    [Fact]
    public void ParseFileName_WithChineseBracket_ShouldExtractChineseTitle()
    {
        // Arrange
        var filePath = @"C:\Movies\[功夫] Kung.Fu.Hustle.2004.BluRay.1080p.x265.mkv";

        // Act
        var result = MovieScannerService.ParseFileName(filePath);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("功夫", result.Title);
        Assert.Equal("Kung Fu Hustle", result.OriginalTitle);
        Assert.Equal(2004, result.ReleaseYear);
        Assert.Equal("1080P", result.Resolution);
        Assert.Equal("X265", result.VideoCodec);
    }

    [Fact]
    public void ParseFileName_WithChineseBracket_NoEnglishTitle()
    {
        // Arrange
        var filePath = @"C:\Movies\[流浪地球2].2023.1080p.WEB-DL.DDP5.1.H264.mkv";

        // Act
        var result = MovieScannerService.ParseFileName(filePath);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("流浪地球2", result.Title);
        Assert.Equal(2023, result.ReleaseYear);
        Assert.Equal("1080P", result.Resolution);
    }

    [Fact]
    public void ParseFileName_WithoutBracket_ShouldParseTitle()
    {
        // Arrange
        var filePath = @"C:\Movies\The.Dark.Knight.2008.1080p.BluRay.x264.mkv";

        // Act
        var result = MovieScannerService.ParseFileName(filePath);

        // Assert
        Assert.NotNull(result);
        Assert.Contains("The Dark Knight", result.Title);
        Assert.Equal(2008, result.ReleaseYear);
        Assert.Equal("1080P", result.Resolution);
        Assert.Equal("X264", result.VideoCodec);
    }

    [Fact]
    public void ParseFileName_InvalidPath_ShouldReturnNull()
    {
        // Arrange
        string filePath = null;

        // Act
        var result = MovieScannerService.ParseFileName(filePath);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ParseFileName_EmptyFileName_ShouldReturnNull()
    {
        // Arrange
        var filePath = @"C:\Movies\.mkv";

        // Act
        var result = MovieScannerService.ParseFileName(filePath);

        // Assert
        Assert.Null(result);
    }
}