using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Serilog;

namespace NatSelect.Config;

public static class ConfigLoader
{
    /// <summary>
    /// 加载配置文件并转换为强类型对象
    /// </summary>
    public static T Load<T>(string filePath) where T : class, new()
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Configuration file not found: {filePath}");
        }

        var yamlContent = File.ReadAllText(filePath);

        // 配置反序列化器
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)   // 自动匹配 camelCase
            .IgnoreUnmatchedProperties()                                // 忽略 YAML 中多余的字段，防止报错
            .Build();

        try
        {
            var config = deserializer.Deserialize<T>(yamlContent);
            Log.Information("Configuration loaded successfully: {Path}", filePath);
            return config;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Failed to parse configuration file!");
            throw;
        }
    }

    /// <summary>
    /// 加载配置文件为动态对象 (适合数值策划表)
    /// </summary>
    public static dynamic LoadDynamic(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Configuration file not found: {filePath}");
        }

        var yamlContent = File.ReadAllText(filePath);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        var obj = deserializer.Deserialize(yamlContent);
        return (dynamic)obj;
    }
}
