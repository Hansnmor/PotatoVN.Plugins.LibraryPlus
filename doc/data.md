# 插件数据读写

软件的API提供了插件数据读写的接口：
```csharp
/// <summary>
/// 读取本插件存储的数据
/// </summary>
/// <returns></returns>
public Task<string?> GetDataAsync();

/// <summary>
/// 保存本插件存储的数据
/// </summary>
/// <param name="data"></param>
/// <returns></returns>
public Task SaveDataAsync(string data);
```

建议你把插件的数据使用各种方式序列化成字符串后读写。

请注意，插件的数据存储是可持久的：用户在关闭/卸载插件时能够自由选择是否保留插件数据，如果选择保留，那么当用户重新安装/启用插件时，之前的数据将会被恢复。 因此，你的插件需要**能够处理数据的版本兼容问题**，以避免在数据结构发生变化时出现错误。建议你在保存的数据中包含一个版本号字段，以便在读取数据时进行兼容性检查和必要的迁移处理。