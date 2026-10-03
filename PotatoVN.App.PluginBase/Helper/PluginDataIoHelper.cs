using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using PotatoVN.App.PluginBase.Models;
using Windows.Storage.Pickers;

namespace PotatoVN.App.PluginBase.Helper;

/// <summary>
/// 插件数据导出/导入：把 PluginData（设置+手动覆盖+搜刮/评分缓存）打包成带标识头的 JSON 文件备份。
/// 动机：宿主卸载时勾选"删除数据"只清一条库记录、不可恢复——先导出才有后悔药。
/// 文件格式：
///   { "app": "PotatoVN.Plugins.LibraryPlus", "schema": 1, "exportedAt": "...", "data": { …PluginData… } }
/// 版本策略（2026-10-03 修正）：只拒收 **比当前更新** 的备份（那是新插件写的，老插件读不懂）；
/// 等于或更旧的备份一律接受并做一次就地迁移——旧写法是 `!= SchemaVersion`，一旦日后升版本号，
/// 用户所有老备份都会被"版本不匹配"整体拒收。
/// </summary>
internal static class PluginDataIoHelper
{
    private const string AppTag = "PotatoVN.Plugins.LibraryPlus";
    private const int SchemaVersion = 1;

    private static FileSavePicker CreateSavePicker()
    {
        var picker = new FileSavePicker
        {
            SuggestedFileName = $"LibraryPlus数据备份_{DateTime.Now:yyyyMMdd_HHmm}",
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeChoices.Add("LibraryPlus 数据备份", new List<string> { ".json" }); // 不配会被 PickSaveFileAsync 拒绝
        return picker;
    }

    /// <summary>弹保存对话框并写出备份；用户取消返回 null，成功返回文件路径</summary>
    public static async Task<string?> ExportAsync()
    {
        FileSavePicker picker = CreateSavePicker();
        if (!AttachToMainWindow(picker)) return null;
        Windows.Storage.StorageFile? file = await picker.PickSaveFileAsync();
        if (file is null) return null;

        var payload = new
        {
            app = AppTag,
            schema = SchemaVersion,
            exportedAt = DateTime.Now,
            data = Plugin.Data,
        };
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };
        await Windows.Storage.FileIO.WriteTextAsync(file, JsonSerializer.Serialize(payload, options));
        return file.Path;
    }

    /// <summary>
    /// 弹打开对话框读取并校验备份；成功返回反序列化好的 PluginData（未写回），用户取消返回 null，
    /// 文件内容不是本插件有效备份时抛 InvalidOperationException（调用方据此给出明确提示）。
    /// </summary>
    public static async Task<PluginData?> ImportAsync()
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            ViewMode = PickerViewMode.List,
        };
        picker.FileTypeFilter.Add(".json");
        if (!AttachToMainWindow(picker)) return null;
        Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null) return null;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(await Windows.Storage.FileIO.ReadTextAsync(file));
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException("文件不是合法的 JSON", e);
        }

        try
        {
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("app", out JsonElement appEl) || appEl.GetString() != AppTag ||
                !root.TryGetProperty("schema", out JsonElement schemaEl) ||
                !root.TryGetProperty("data", out JsonElement dataEl))
                throw new InvalidOperationException("缺少本插件的备份标识");

            int schema = schemaEl.GetInt32();
            if (schema > SchemaVersion)
                throw new InvalidOperationException(
                    $"备份由更新版本的插件导出（备份 schema={schema}，当前支持到 {SchemaVersion}），请先更新插件再导入");

            PluginData? data = dataEl.Deserialize<PluginData>(
                new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } });
            if (data is null) throw new InvalidOperationException("备份内容反序列化失败");

            // schema < SchemaVersion 时在此处补老格式迁移（目前只有 schema 1，暂无历史分支）
            Normalize(data);
            return data;
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException("备份的 data 节点结构不符", e);
        }
    }

    /// <summary>
    /// 导入后的兜底修复：JSON 里显式为 null 的集合会反序列化成 null，而插件各处都假定这些集合非 null
    /// （直接 TryGetValue / foreach）→ 这里统一补空集合，避免"结构不全的备份"变成后续随机空引用。
    /// </summary>
    private static void Normalize(PluginData data)
    {
        if (data.KungalData is null) data.KungalData = new();
        if (data.BgmData is null) data.BgmData = new();
        if (data.RatingCache is null) data.RatingCache = new();
        if (data.UserCategory is null) data.UserCategory = new();
        if (data.UserForm is null) data.UserForm = new();
        if (data.IncludedPlayTypes is null) data.IncludedPlayTypes = new();
        if (data.VolumeNormalizedGames is null) data.VolumeNormalizedGames = new();
        if (data.VolumeNormalizedPaths is null) data.VolumeNormalizedPaths = new();
    }

    /// <summary>导入结果摘要（给 InfoBar 用）：让用户看到实际导进了多少数据，而不是只收到一句"完成"。</summary>
    public static string Describe(PluginData data) =>
        $"评分缓存 {data.RatingCache.Count} 条 · 搜刮缓存 {data.KungalData.Count} 条 · " +
        $"Bangumi 标签缓存 {data.BgmData.Count} 条 · 手动分类覆盖 {data.UserCategory.Count} 条";

    /// <summary>WinUI 3 的 picker 必须挂上窗口句柄才能弹出（插件用 HostApi 的主窗口，宿主同款写法）</summary>
    private static bool AttachToMainWindow(object picker)
    {
        Microsoft.UI.Xaml.Window? win = Plugin.HostApi.GetMainWindow();
        if (win is null) return false;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(win));
        return true;
    }
}
