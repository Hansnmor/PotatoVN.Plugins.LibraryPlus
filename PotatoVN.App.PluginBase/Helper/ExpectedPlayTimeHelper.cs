using System;
using System.Globalization;
using System.Text.RegularExpressions;
using GalgameManager.Models;

namespace PotatoVN.App.PluginBase.Helper;

/// <summary>
/// 预计时长解析工具：把字符串（VNDB 搜刮的 "1h30m" / "45m"，以及 very short 等类别）
/// 解析为分钟数。排序比较器、时长区间筛选、统计面板共用。
/// </summary>
public static class ExpectedPlayTimeHelper
{
    /// <summary>
    /// 解析预计时长字符串为分钟数，无法解析返回 null。
    /// </summary>
    public static long? ParseMinutes(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == Galgame.DefaultString) return null;

        switch (value.Trim().ToLowerInvariant())
        {
            case "very short": return 60;
            case "short": return 5 * 60;
            case "medium": return 15 * 60;
            case "long": return 30 * 60;
            case "very long": return 50 * 60;
        }

        bool any = false;
        long minutes = 0;
        Match m = Regex.Match(value, @"(\d+(?:\.\d+)?)\s*h", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            minutes += (long)(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 60);
            any = true;
        }
        m = Regex.Match(value, @"(\d+(?:\.\d+)?)\s*m", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            minutes += (long)double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            any = true;
        }
        return any ? minutes : null;
    }

    /// <summary>分钟数格式化为小时（四舍五入到整数），null 返回 "未知"</summary>
    public static string FormatHours(long? minutes) =>
        minutes is null ? "未知" : $"{Math.Round(minutes.Value / 60.0)} 小时";
}
