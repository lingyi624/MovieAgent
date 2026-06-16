using Moq;
using MovieAgent.Core.Entities;
using MovieAgent.Core.Interfaces;
using MovieAgent.Infrastructure.Services;
using Xunit;

namespace MovieAgent.Tests;

public class TagServiceTests
{
    [Fact]
    public async Task GetRecommendedTags_WithEmptyQuery_ShouldReturnTags()
    {
        // Arrange
        var mockRepo = new Mock<IMovieRepository>();
        var mockAgent = new Mock<IAgentService>();
        var service = new TagService(mockRepo.Object, mockAgent.Object);

        // Act
        var result = await service.GetRecommendedTagsAsync("");

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result);
    }

    [Fact]
    public async Task GetRecommendedTags_WithQuery_ShouldFilterTags()
    {
        // Arrange
        var mockRepo = new Mock<IMovieRepository>();
        var mockAgent = new Mock<IAgentService>();
        var service = new TagService(mockRepo.Object, mockAgent.Object);

        // Act
        var result = await service.GetRecommendedTagsAsync("感人");

        // Assert
        Assert.NotNull(result);
        Assert.Contains("感人", result);
    }

    [Fact]
    public void GetEmotionTags_ShouldContainExpectedTags()
    {
        // Arrange & Act
        var emotionTags = new List<string>
        {
            "感人", "催泪", "温馨", "治愈", "励志", "热血", "震撼", "深刻",
            "压抑", "惊悚", "紧张", "悬疑", "恐怖", "搞笑", "轻松", "浪漫",
            "悲伤", "愤怒", "希望", "绝望", "温暖", "伤感", "悲壮", "温情"
        };

        // Assert
        Assert.Contains("感人", emotionTags);
        Assert.Contains("治愈", emotionTags);
        Assert.Contains("热血", emotionTags);
    }

    [Fact]
    public void GetSceneTags_ShouldContainExpectedTags()
    {
        // Arrange & Act
        var sceneTags = new List<string>
        {
            "太空", "海洋", "森林", "城市", "乡村", "沙漠", "雪山", "草原",
            "战争", "监狱", "校园", "家庭", "职场", "历史", "未来", "古代",
            "科幻", "奇幻", "魔法", "冒险", "动作", "犯罪", "爱情", "友情"
        };

        // Assert
        Assert.Contains("科幻", sceneTags);
        Assert.Contains("爱情", sceneTags);
        Assert.Contains("冒险", sceneTags);
    }

    [Fact]
    public void GetStyleTags_ShouldContainExpectedTags()
    {
        // Arrange & Act
        var styleTags = new List<string>
        {
            "文艺", "商业", "独立", "小众", "经典", "现代", "复古", "先锋",
            "写实", "夸张", "细腻", "粗犷", "唯美", "暗黑", "清新", "厚重"
        };

        // Assert
        Assert.Contains("文艺", styleTags);
        Assert.Contains("经典", styleTags);
        Assert.Contains("唯美", styleTags);
    }
}