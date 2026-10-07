using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace CherryTranslate.Core;

/// <summary>
/// Reads only the selected Cherry Studio model, its provider, the translation
/// language preferences, and the enabled API key required for a profile.
/// </summary>
public sealed class CherryConfigImporter
{
    private const string SelectedModelPreference = "feature.translate.model_id";
    private const string PreferredLanguagePreference = "feature.translate.action.preferred_lang";
    private const string AlternateLanguagePreference = "feature.translate.action.alter_lang";
    private const string SupportedEndpoint = "openai-chat-completions";

    public TranslationProfile Import(string? databasePath = null, string? modelIdOverride = null)
    {
        var path = ResolveDatabasePath(databasePath);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException("找不到 Cherry Studio 配置数据库。");
        }

        using var database = SqliteDatabase.Open(path);
        var selectedModelReference = ReadPreference(database, SelectedModelPreference);
        var selectedLanguage = ReadPreference(database, PreferredLanguagePreference) ?? "zh-cn";
        var alternateLanguage = ReadPreference(database, AlternateLanguagePreference) ?? "en-us";

        var configuredReference = ParseModelReference(selectedModelReference);
        var overrideReference = ParseModelReference(modelIdOverride);
        if (overrideReference is not null && overrideReference.ProviderHint is null)
        {
            throw new InvalidOperationException("模型覆盖必须使用 provider::model 格式。");
        }
        if (configuredReference is null && overrideReference is null)
        {
            throw new InvalidOperationException("Cherry Studio 中没有配置翻译模型。");
        }

        // A provider-qualified override (for example,
        // deepseek::deepseek-v4-flash) deliberately selects that provider.
        // A plain model override keeps the selected model's provider.
        var providerQualifiedOverride = overrideReference?.ProviderHint is not null;
        var lookupReference = providerQualifiedOverride ? overrideReference : configuredReference;
        var modelRow = lookupReference is null
            ? null
            : FindModel(database, lookupReference);

        var providerId = modelRow?.ProviderId;
        if (string.IsNullOrWhiteSpace(providerId))
        {
            providerId = lookupReference?.ProviderHint;
        }

        if (string.IsNullOrWhiteSpace(providerId))
        {
            throw new InvalidOperationException("无法确定 Cherry Studio 的翻译服务提供商。");
        }

        var provider = FindProvider(database, providerId);
        if (provider is null)
        {
            throw new InvalidOperationException("找不到 Cherry Studio 的翻译服务配置。");
        }

        EnsureEnabled(modelRow?.IsEnabled, "Cherry Studio 中选中的翻译模型已停用。");
        EnsureEnabled(provider.IsEnabled, "Cherry Studio 中选中的翻译服务已停用。");

        var effectiveModelId = modelRow?.ModelId
            ?? overrideReference?.ModelId
            ?? configuredReference?.ModelId;
        if (string.IsNullOrWhiteSpace(effectiveModelId))
        {
            throw new InvalidOperationException("Cherry Studio 中没有配置有效的翻译模型。");
        }

        var endpoint = ResolveEndpoint(provider);
        var apiKey = ReadEnabledApiKey(provider.ApiKeysJson);
        var providerName = string.IsNullOrWhiteSpace(provider.Name)
            ? provider.ProviderId
            : provider.Name!;

        return new TranslationProfile(
            providerName,
            effectiveModelId,
            endpoint,
            apiKey,
            NormalizeLanguage(selectedLanguage, "zh-cn"),
            NormalizeLanguage(alternateLanguage, "en-us"));
    }

    private static string ResolveDatabasePath(string? databasePath)
    {
        if (!string.IsNullOrWhiteSpace(databasePath))
        {
            return Path.GetFullPath(databasePath);
        }

        var appData = Environment.GetEnvironmentVariable("APPDATA");
        if (string.IsNullOrWhiteSpace(appData))
        {
            appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }

        if (string.IsNullOrWhiteSpace(appData))
        {
            throw new InvalidOperationException("无法确定 Cherry Studio 配置目录。");
        }

        return Path.Combine(appData, "CherryStudio", "Data", "cherrystudio.sqlite");
    }

    private static string? ReadPreference(SqliteDatabase database, string key)
    {
        var row = database.QuerySingle(
            "SELECT value FROM preference WHERE scope = 'default' AND key = ? LIMIT 1",
            key);
        return UnwrapString(row?.GetValueOrDefault("value"));
    }

    private static ModelReference? ParseModelReference(string? value)
    {
        value = UnwrapString(value)?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var separator = value.IndexOf("::", StringComparison.Ordinal);
        if (separator <= 0 || separator >= value.Length - 2)
        {
            return new ModelReference(null, value, value);
        }

        var providerHint = value[..separator].Trim();
        var modelId = value[(separator + 2)..].Trim();
        return string.IsNullOrWhiteSpace(providerHint) || string.IsNullOrWhiteSpace(modelId)
            ? new ModelReference(null, value, value)
            : new ModelReference(providerHint, modelId, value);
    }

    private static ModelRow? FindModel(SqliteDatabase database, ModelReference reference)
    {
        Dictionary<string, string?>? row;
        if (reference.ProviderHint is not null)
        {
            // Cherry Studio may use the provider-qualified model reference as
            // user_model.id.  Prefer that exact identity before matching the
            // pair, because the same model id can exist under two providers.
            row = database.QuerySingle(
                "SELECT provider_id, model_id, is_enabled FROM user_model WHERE id = ? LIMIT 1",
                reference.FullReference);
            row ??= database.QuerySingle(
                "SELECT provider_id, model_id, is_enabled FROM user_model " +
                "WHERE provider_id = ? AND model_id = ? LIMIT 1",
                reference.ProviderHint,
                reference.ModelId);
        }
        else
        {
            var rows = database.QueryMany(
                "SELECT provider_id, model_id, is_enabled FROM user_model WHERE model_id = ?",
                reference.ModelId);
            if (rows.Count > 1)
            {
                throw new InvalidOperationException("Cherry Studio 中存在多个同名翻译模型，无法安全导入。");
            }

            row = rows.Count == 0 ? null : rows[0];
        }

        return row is null
            ? null
            : new ModelRow(
                row.GetValueOrDefault("provider_id"),
                row.GetValueOrDefault("model_id"),
                row.GetValueOrDefault("is_enabled"));
    }

    private static ProviderRow? FindProvider(SqliteDatabase database, string providerId)
    {
        var row = database.QuerySingle(
            "SELECT provider_id, preset_provider_id, name, endpoint_configs, default_chat_endpoint, api_keys, is_enabled " +
            "FROM user_provider WHERE provider_id = ? LIMIT 1",
            providerId);
        return row is null
            ? null
            : new ProviderRow(
                row.GetValueOrDefault("provider_id") ?? providerId,
                row.GetValueOrDefault("preset_provider_id"),
                row.GetValueOrDefault("name"),
                row.GetValueOrDefault("endpoint_configs"),
                row.GetValueOrDefault("default_chat_endpoint"),
                row.GetValueOrDefault("api_keys"),
                row.GetValueOrDefault("is_enabled"));
    }

    private static void EnsureEnabled(string? value, string message)
    {
        if (IsDisabled(value))
        {
            throw new InvalidOperationException(message);
        }
    }

    private static bool IsDisabled(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return string.Equals(value.Trim(), "0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value.Trim(), "false", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value.Trim(), "no", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveEndpoint(ProviderRow provider)
    {
        var configuredEndpoint = UnwrapString(provider.DefaultChatEndpoint)?.Trim();
        string? explicitUrl = null;
        if (!string.IsNullOrWhiteSpace(configuredEndpoint))
        {
            if (IsUrl(configuredEndpoint))
            {
                explicitUrl = configuredEndpoint;
            }
            else if (!string.Equals(configuredEndpoint, SupportedEndpoint, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Cherry Studio 使用了暂不支持的翻译接口类型。");
            }
        }

        var endpointFromConfig = ReadEndpointConfig(provider.EndpointConfigs);
        if (endpointFromConfig.Unsupported)
        {
            throw new InvalidOperationException("Cherry Studio 使用了暂不支持的翻译接口类型。");
        }

        if (!string.IsNullOrWhiteSpace(explicitUrl))
        {
            return explicitUrl;
        }

        if (!string.IsNullOrWhiteSpace(endpointFromConfig.Url))
        {
            return endpointFromConfig.Url;
        }

        if (IsKnownProvider(provider, "silicon") || IsKnownProvider(provider, "siliconflow"))
        {
            return "https://api.siliconflow.cn/v1";
        }

        if (IsKnownProvider(provider, "deepseek"))
        {
            return "https://api.deepseek.com/v1";
        }

        throw new InvalidOperationException("无法确定 Cherry Studio 的翻译服务地址。");
    }

    private static EndpointConfig ReadEndpointConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new EndpointConfig(null, false);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return InspectEndpointNode(document.RootElement);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Cherry Studio 的翻译服务配置格式无效。");
        }
    }

    private static EndpointConfig InspectEndpointNode(JsonElement node)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.String:
            {
                var value = node.GetString()?.Trim();
                return string.IsNullOrWhiteSpace(value)
                    ? new EndpointConfig(null, false)
                    : IsUrl(value)
                        ? new EndpointConfig(value, false)
                        : new EndpointConfig(null, !string.Equals(value, SupportedEndpoint, StringComparison.OrdinalIgnoreCase));
            }
            case JsonValueKind.Object:
            {
                if (TryGetPropertyIgnoreCase(node, SupportedEndpoint, out var supportedEndpoint))
                {
                    return InspectSelectedEndpoint(supportedEndpoint);
                }

                // A non-empty mapping with no supported endpoint means the
                // configured provider cannot be safely guessed.
                return new EndpointConfig(null, node.EnumerateObject().Any());
            }
            default:
                return new EndpointConfig(null, false);
        }
    }

    private static EndpointConfig InspectSelectedEndpoint(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.String)
        {
            var value = node.GetString()?.Trim();
            return string.IsNullOrWhiteSpace(value) || string.Equals(value, SupportedEndpoint, StringComparison.OrdinalIgnoreCase)
                ? new EndpointConfig(null, false)
                : IsUrl(value)
                    ? new EndpointConfig(value, false)
                    : new EndpointConfig(null, true);
        }

        if (node.ValueKind != JsonValueKind.Object)
        {
            return new EndpointConfig(null, false);
        }

        string? url = null;
        foreach (var property in node.EnumerateObject())
        {
            if (IsEndpointTypeProperty(property.Name)
                && property.Value.ValueKind == JsonValueKind.String
                && !string.Equals(property.Value.GetString(), SupportedEndpoint, StringComparison.OrdinalIgnoreCase))
            {
                return new EndpointConfig(null, true);
            }

            if (url is null && IsUrlProperty(property.Name) && property.Value.ValueKind == JsonValueKind.String)
            {
                url = property.Value.GetString()?.Trim();
            }
        }

        return new EndpointConfig(url, false);
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement node, string propertyName, out JsonElement value)
    {
        foreach (var property in node.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool IsKnownProvider(ProviderRow provider, string providerId)
    {
        return string.Equals(provider.ProviderId?.Trim(), providerId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider.PresetProviderId?.Trim(), providerId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEndpointTypeProperty(string name)
    {
        return name.Equals("type", StringComparison.OrdinalIgnoreCase)
            || name.Equals("apiType", StringComparison.OrdinalIgnoreCase)
            || name.Equals("protocol", StringComparison.OrdinalIgnoreCase)
            || name.Equals("endpointType", StringComparison.OrdinalIgnoreCase)
            || name.Equals("chatEndpoint", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUrlProperty(string name)
    {
        return name.Equals("url", StringComparison.OrdinalIgnoreCase)
            || name.Equals("baseUrl", StringComparison.OrdinalIgnoreCase)
            || name.Equals("base_url", StringComparison.OrdinalIgnoreCase)
            || name.Equals("endpoint", StringComparison.OrdinalIgnoreCase)
            || name.Equals("baseURL", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUrl(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadEnabledApiKey(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("Cherry Studio 中没有可用的翻译 API Key。");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("Cherry Studio 的翻译 API Key 配置格式无效。");
            }

            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("key", out var keyProperty)
                    || keyProperty.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var key = keyProperty.GetString();
                if (string.IsNullOrWhiteSpace(key) || IsExplicitlyDisabled(item))
                {
                    continue;
                }

                return key;
            }
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Cherry Studio 的翻译 API Key 配置格式无效。");
        }

        throw new InvalidOperationException("Cherry Studio 中没有启用的翻译 API Key。");
    }

    private static bool IsExplicitlyDisabled(JsonElement item)
    {
        if (!item.TryGetProperty("isEnabled", out var enabledProperty))
        {
            return false;
        }

        return enabledProperty.ValueKind switch
        {
            JsonValueKind.False => true,
            JsonValueKind.Number => enabledProperty.TryGetInt32(out var value) && value == 0,
            JsonValueKind.String => IsDisabled(enabledProperty.GetString()),
            _ => false
        };
    }

    private static string? UnwrapString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        value = value.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            try
            {
                return JsonSerializer.Deserialize<string>(value);
            }
            catch (JsonException)
            {
                return value[1..^1];
            }
        }

        return value;
    }

    private static string NormalizeLanguage(string? value, string fallback)
    {
        value = UnwrapString(value)?.Trim();
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private sealed record ModelReference(string? ProviderHint, string ModelId, string FullReference);

    private sealed record ModelRow(string? ProviderId, string? ModelId, string? IsEnabled);

    private sealed record ProviderRow(
        string ProviderId,
        string? PresetProviderId,
        string? Name,
        string? EndpointConfigs,
        string? DefaultChatEndpoint,
        string? ApiKeysJson,
        string? IsEnabled);

    private sealed record EndpointConfig(string? Url, bool Unsupported);

    /// <summary>
    /// Minimal winsqlite3 binding.  SQL statements are fixed and all values are
    /// bound parameters, so the importer never interpolates model/provider data.
    /// </summary>
    private sealed class SqliteDatabase : IDisposable
    {
        private const int SqliteOk = 0;
        private const int SqliteRow = 100;
        private const int SqliteDone = 101;
        private const int SqliteOpenReadOnly = 0x00000001;
        private static readonly IntPtr SqliteTransient = new(-1);

        private IntPtr _database;
        private bool _disposed;

        private SqliteDatabase(IntPtr database)
        {
            _database = database;
        }

        public static SqliteDatabase Open(string path)
        {
            var pathBytes = Utf8(path);
            var result = sqlite3_open_v2(pathBytes, out var database, SqliteOpenReadOnly, IntPtr.Zero);
            if (result != SqliteOk || database == IntPtr.Zero)
            {
                if (database != IntPtr.Zero)
                {
                    sqlite3_close_v2(database);
                }

                throw new InvalidOperationException("无法打开 Cherry Studio 配置数据库。");
            }

            var wrapper = new SqliteDatabase(database);
            try
            {
                sqlite3_busy_timeout(database, 5_000);
                return wrapper;
            }
            catch
            {
                wrapper.Dispose();
                throw;
            }
        }

        public Dictionary<string, string?>? QuerySingle(string sql, params string[] parameters)
        {
            var rows = Query(sql, parameters);
            return rows.Count == 0 ? null : rows[0];
        }

        public List<Dictionary<string, string?>> QueryMany(string sql, params string[] parameters)
        {
            return Query(sql, parameters);
        }

        private List<Dictionary<string, string?>> Query(string sql, params string[] parameters)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var sqlBytes = Utf8(sql);
            var result = sqlite3_prepare_v2(_database, sqlBytes, sqlBytes.Length - 1, out var statement, IntPtr.Zero);
            if (result != SqliteOk || statement == IntPtr.Zero)
            {
                if (statement != IntPtr.Zero)
                {
                    sqlite3_finalize(statement);
                }

                throw new InvalidOperationException("无法读取 Cherry Studio 配置数据库。");
            }

            try
            {
                for (var index = 0; index < parameters.Length; index++)
                {
                    var valueBytes = Utf8(parameters[index]);
                    result = sqlite3_bind_text(statement, index + 1, valueBytes, valueBytes.Length - 1, SqliteTransient);
                    if (result != SqliteOk)
                    {
                        throw new InvalidOperationException("无法读取 Cherry Studio 配置数据库。");
                    }
                }

                var columnCount = sqlite3_column_count(statement);
                var columnNames = new string[columnCount];
                for (var index = 0; index < columnCount; index++)
                {
                    columnNames[index] = ReadUtf8(sqlite3_column_name(statement, index)) ?? string.Empty;
                }

                var rows = new List<Dictionary<string, string?>>();
                while (true)
                {
                    result = sqlite3_step(statement);
                    if (result == SqliteDone)
                    {
                        return rows;
                    }

                    if (result != SqliteRow)
                    {
                        throw new InvalidOperationException("无法读取 Cherry Studio 配置数据库。");
                    }

                    var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                    for (var index = 0; index < columnCount; index++)
                    {
                        row[columnNames[index]] = ReadUtf8(sqlite3_column_text(statement, index));
                    }

                    rows.Add(row);
                }
            }
            finally
            {
                sqlite3_finalize(statement);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_database != IntPtr.Zero)
            {
                sqlite3_close_v2(_database);
                _database = IntPtr.Zero;
            }
        }

        private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value + "\0");

        private static string? ReadUtf8(IntPtr value)
        {
            return value == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(value);
        }

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_open_v2(
            [In] byte[] filename,
            out IntPtr database,
            int flags,
            IntPtr zvfs);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_close_v2(IntPtr database);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_busy_timeout(IntPtr database, int milliseconds);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_prepare_v2(
            IntPtr database,
            [In] byte[] sql,
            int byteCount,
            out IntPtr statement,
            IntPtr tail);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_bind_text(
            IntPtr statement,
            int parameterIndex,
            [In] byte[] value,
            int byteCount,
            IntPtr destructor);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_step(IntPtr statement);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_finalize(IntPtr statement);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_count(IntPtr statement);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_name(IntPtr statement, int column);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
    }
}
