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
/// 文件格式（schema 不匹配时导入直接拒绝，防止误把别的文件灌进去）：
///   { "app": "PotatoVN.Plugins.LibraryPlus", "schema": 1, "exportedAt": "...", "data": { …PluginData… } }
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
                !root.TryGetProperty("schema", out JsonElement schemaEl) || schemaEl.GetInt32() != SchemaVersion ||
                !root.TryGetProperty("data", out JsonElement dataEl))
                throw new InvalidOperationException("缺少本插件的备份标识或版本不匹配");

            PluginData? data = dataEl.Deserialize<PluginData>(
                new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } });
            return data ?? throw new InvalidOperationException("备份内容反序列化失败");
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException("备份的 data 节点结构不符", e);
        }
    }

    /// <summary>WinUI 3 的 picker 必须挂上窗口句柄才能弹出（插件用 HostApi 的主窗口，宿主同款写法）</summary>
    private static bool AttachToMainWindow(object picker)
    {
        Microsoft.UI.Xaml.Window? win = Plugin.HostApi.GetMainWindow();
        if (win is null) return false;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(win));
        return true;
    }
}
