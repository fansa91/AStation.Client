using AStation_Client.Services;

namespace AStation.Client.Tests;

/// <summary>
/// 弹幕服务单元测试
/// </summary>
public class DanmakuServiceTest
{
    [Theory]
    [InlineData(16777215L, "#FFFFFF")] // 白色
    [InlineData(16711680L, "#FF0000")] // 红色
    [InlineData(0L, "#000000")]
    [InlineData(255L, "#0000FF")]
    public void ToHexColor_ShouldConvertDecimalRgbToHex(long decimalColor, string expected)
    {
        Assert.Equal(expected, DanmakuService.ToHexColor(decimalColor));
    }

    /// <summary>
    /// 联调用例：真实打一次弹幕接口，确认整条链路（请求参数 → 反序列化 → 模型映射）是通的。
    /// 需要联网，默认跳过；用 `dotnet test --filter LiveDanmaku` 手动跑。
    /// </summary>
    [Fact(Skip = "需要联网的联调用例；验证时手动去掉 Skip 跑一次")]
    public async Task GetClientDanmakus_RealApi_ShouldReturnItems()
    {
        var service = new DanmakuService(new ApiService());
        var list = await service.GetClientDanmakusAsync("39302173");

        Assert.NotEmpty(list);
        Assert.All(list, d => Assert.False(string.IsNullOrWhiteSpace(d.Text)));
        Assert.All(list, d => Assert.StartsWith("#", d.Color));
    }
}
