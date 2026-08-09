# 搜刮器开发指南

如果你需要开发一个新的搜刮器，请你让你的插件主类实现`IParserProvider`接口。

开发时，需要注意以下注意事项：
* 你需要确定一个唯一的ParserId（int），建议你使用随机数生成器生成一个6位数证书，避免和别的插件冲突。
* `IGalInfoPhraser`的GetGalgameInfo要求返回一个**全新**的Galgame对象，而不是在传入的galgame对象上修改后返回。

---
以下为一个搜刮器示例：
```csharp
//using ...
namespace PotatoVN.App.PluginBase;

public class GetChuParser : IGalInfoPhraser
{
    private const int ParserId = 114514;
    
    // 使用静态构造函数来确保 HttpClient 只被初始化一次，并配置好Cookie和Header
    private static readonly HttpClient _client;
    static GetChuParser()
    {
        var cookieContainer = new CookieContainer();
        var handler = new HttpClientHandler
        {
            CookieContainer = cookieContainer,
            AllowAutoRedirect = true,
        };
        _client = new(handler);
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/108.0.0.0 Safari/537.36");
        
        // 确保编码提供程序被注册，以便支持 EUC-JP
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public async Task<Galgame?> GetGalgameInfo(Galgame galgame)
    {
        if (!galgame.IdForPlugins.TryGetValue(ParserId, out var id) || string.IsNullOrEmpty(id)) 
            return null;
        
        // 直接请求带有 &gc=gc 参数的URL以绕过年龄验证
        string targetUrl = $"https://www.getchu.com/soft.phtml?id={id}&gc=gc";
        
        try
        {
            var responseBytes = await _client.GetByteArrayAsync(targetUrl);
            var eucJpEncoding = Encoding.GetEncoding("EUC-JP");
            string htmlContent = eucJpEncoding.GetString(responseBytes);

            // 检查是否仍然收到了验证页面
            if (htmlContent.Contains("R18 年齢認証ページ"))
            {
                Console.WriteLine($"警告：尝试访问 {targetUrl} 时，仍然收到了年龄验证页面。");
                return null;
            }

            // 解析HTML内容
            htmlContent = htmlContent.Replace("charset=EUC-JP", "charset=utf-8", StringComparison.OrdinalIgnoreCase);
            GameData data = await ParseAsync(htmlContent);
            
            // 检查解析结果是否有效，防止因页面大改版导致返回空对象
            if (string.IsNullOrEmpty(data.CoverImageUrl) && string.IsNullOrEmpty(data.Story) && !data.Staff.Any())
            {
                Console.WriteLine($"警告：无法从 {targetUrl} 解析出有效信息。可能是页面结构已发生重大变化。");
                return null;
            }

            // 填充Galgame对象
            Galgame result = new();                                 
            result.Description.Value = data.Story;
            result.ImageUrl = data.CoverImageUrl;
            result.RssType = GetPhraseType();
            result.Id = galgame.Id;

            return result;
        }
        catch (HttpRequestException e)
        {
            Console.WriteLine($"\n网络请求错误: {e.Message} (URL: {targetUrl})");
            Console.WriteLine("请检查您的网络连接或URL是否正确。");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n处理过程中发生错误: {ex.Message}");
        }
        return null;
    }

    public RssType GetPhraseType() => (RssType)ParserId;
    
    /// <summary>
    /// 用于存储解析结果的数据模型
    /// </summary>
    public class GameData
    {
        public string? Story { get; set; }
        public string? CoverImageUrl { get; set; }
        public List<(string Role, List<string> Members)> Staff { get; set; } = new();
    }

    /// <summary>
    /// 异步解析给定的 HTML 文本内容。
    /// </summary>
    public async Task<GameData> ParseAsync(string htmlContent)
    {
        var context = BrowsingContext.New(Configuration.Default);
        var document = await context.OpenAsync(req => req.Content(htmlContent));
        var gameData = new GameData
        {
            Story = ExtractStory(document),
            CoverImageUrl = ExtractCoverImageUrl(document),
            Staff = ExtractStaffAndCVs(document)
        };
        return gameData;
    }

    /// <summary>
    /// 1. 提取故事（ストーリー）块的内容。
    /// </summary>
    private string? ExtractStory(IDocument document)
    {
        var tmp = document.QuerySelectorAll(".tabletitle").Select(t => t.TextContent).ToList();
        // 修改：不再限制为div，查找所有带.tabletitle类的元素
        var storyTitleElement = document.QuerySelectorAll(".tabletitle")
            .FirstOrDefault(el => el.TextContent.Contains("ストーリー"));
        if (storyTitleElement != null)
        {
            var storyBodyDiv = storyTitleElement.NextElementSibling;
            if (storyBodyDiv != null && storyBodyDiv.ClassList.Contains("tablebody"))
            {
                return storyBodyDiv.QuerySelector("span.bootstrap")?.TextContent.Trim();
            }
        }
        return null;
    }


    /// <summary>
    /// 2. 提取游戏封面的 URL。
    /// </summary>
    private string? ExtractCoverImageUrl(IDocument document)
    {
        var imageElement = document.QuerySelector("#soft_table img[src*='package.jpg']");
        var src = imageElement?.GetAttribute("src");

        // 将相对路径转换为绝对路径
        if (src != null && src.StartsWith("./"))
        {
            return "https://www.getchu.com" + src.Substring(1);
        }
        return src;
    }
}

```