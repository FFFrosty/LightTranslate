using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CherryTranslate.Core;

internal static class Program
{
    private static async Task<int> Main()
    {
        try
        {
            TestProfileStore();
            TestTranslationProfileDoesNotExposeKey();
            await TestTranslationPayloadAsync();
            await TestTranslationErrorsAsync();
            await TestCancellationAsync();
            await TestResponseBodyCancellationAsync();
            TestImporter();
            Console.WriteLine("Core tests passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void TestProfileStore()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "profile.bin");
            var profile = new TranslationProfile(
                "SiliconFlow",
                "deepseek-ai/DeepSeek-V4-Flash",
                "https://api.siliconflow.cn/v1",
                "test-secret-key",
                "zh-cn",
                "en-us");
            var store = new ProfileStore(path);
            Assert(store.Load() is null, "missing profile should load as null");
            store.Save(profile);
            var loaded = store.Load();
            Assert(loaded == profile, "DPAPI profile roundtrip failed");
            Assert(File.Exists(path), "profile file was not written");
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    private static void TestTranslationProfileDoesNotExposeKey()
    {
        var profile = new TranslationProfile("provider", "model", "https://example.test/v1", "secret-key");
        Assert(!profile.ToString().Contains("secret-key", StringComparison.Ordinal),
            "TranslationProfile.ToString exposed the API key");
        Assert(profile.ToString().Contains("redacted", StringComparison.OrdinalIgnoreCase),
            "TranslationProfile.ToString should mark the key as redacted");
    }

    private static async Task TestTranslationPayloadAsync()
    {
        string? handlerBody = null;
        Uri? handlerUri = null;
        string? handlerAuthorization = null;
        var handler = new StubHandler(async (request, cancellationToken) =>
        {
            handlerBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            handlerUri = request.RequestUri;
            handlerAuthorization = request.Headers.Authorization?.Parameter;
            return JsonResponse("translated text");
        });

        using var httpClient = new HttpClient(handler);
        using var client = new TranslationClient(httpClient);
        var silicon = new TranslationProfile(
            "SiliconFlow",
            "deepseek-ai/DeepSeek-V4-Flash",
            "https://example.test/v1/",
            "test-api-key");
        var chineseResult = await client.TranslateAsync(silicon, "你好");
        Assert(chineseResult == "translated text", "translation result extraction failed");
        Assert(handlerUri?.ToString() == "https://example.test/v1/chat/completions",
            "base URL normalization added the wrong path");
        Assert(handlerAuthorization == "test-api-key", "authorization header was not sent");

        using (var body = JsonDocument.Parse(handlerBody!))
        {
            var root = body.RootElement;
            Assert(root.GetProperty("model").GetString() == silicon.ModelId, "model id missing from payload");
            Assert(!root.GetProperty("stream").GetBoolean(), "stream must be disabled");
            Assert(root.GetProperty("max_tokens").GetInt32() == 8192, "max_tokens missing from payload");
            Assert(!root.GetProperty("enable_thinking").GetBoolean(), "Silicon thinking flag missing");
            var messages = root.GetProperty("messages");
            Assert(messages[1].GetProperty("content").GetString() == "你好", "selected text was changed");
            Assert(messages[0].GetProperty("content").GetString()!.Contains("请将文本翻译成 en-us。", StringComparison.Ordinal),
                "Chinese text should choose the alternate language");
        }

        await client.TranslateAsync(silicon with { TargetLanguage = "fr-fr", AlternateLanguage = "de-de" }, "hello");
        using (var body = JsonDocument.Parse(handlerBody!))
        {
            var prompt = body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
            Assert(prompt!.Contains("请将文本翻译成 fr-fr。", StringComparison.Ordinal), "target language was not used");
            Assert(!body.RootElement.TryGetProperty("thinking", out _), "Silicon payload contained DeepSeek field");
        }

        await client.TranslateAsync(silicon, "好");
        using (var body = JsonDocument.Parse(handlerBody!))
        {
            var prompt = body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
            Assert(prompt!.Contains("请将文本翻译成 en-us。", StringComparison.Ordinal),
                "a single Chinese character should choose the alternate language");
        }

        await client.TranslateAsync(silicon, "こんにちは");
        using (var body = JsonDocument.Parse(handlerBody!))
        {
            var prompt = body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
            Assert(prompt!.Contains("请将文本翻译成 zh-cn。", StringComparison.Ordinal),
                "Japanese text should not be classified as predominantly Chinese");
        }

        await client.TranslateAsync(
            new TranslationProfile("DeepSeek", "deepseek-v4-flash", "https://api.deepseek.com/v1", "test-api-key"),
            "hello");
        using (var body = JsonDocument.Parse(handlerBody!))
        {
            var root = body.RootElement;
            Assert(root.GetProperty("thinking").GetProperty("type").GetString() == "disabled",
                "DeepSeek thinking payload missing");
            Assert(!root.TryGetProperty("enable_thinking", out _), "DeepSeek payload contained Silicon field");
        }

        Assert(handlerBody!.Length > 0, "fake handler did not receive a request");
    }

    private static async Task TestTranslationErrorsAsync()
    {
        var profile = new TranslationProfile("provider", "model", "https://example.test/v1", "test-key");
        foreach (var (status, expected) in new[]
        {
            (HttpStatusCode.Unauthorized, "认证"),
            (HttpStatusCode.PaymentRequired, "余额"),
            ((HttpStatusCode)429, "频繁"),
            (HttpStatusCode.BadGateway, "不可用")
        })
        {
            var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent("server secret should not be shown")
            }));
            using var httpClient = new HttpClient(handler);
            using var client = new TranslationClient(httpClient);
            var exception = await AssertThrowsAsync<TranslationException>(
                () => client.TranslateAsync(profile, "hello"));
            Assert(exception.Message.Contains(expected, StringComparison.Ordinal),
                $"HTTP {(int)status} did not produce the expected Chinese error");
            Assert(!exception.Message.Contains("server secret", StringComparison.Ordinal),
                "HTTP error exposed the response body");
        }

        foreach (var response in new[]
        {
            "",
            "{\"choices\":[{\"message\":{\"content\":\"\"}}]}",
            "{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"partial\"}}]}",
            "[]"
        })
        {
            var handler = new StubHandler((_, _) => Task.FromResult(JsonResponse(response)));
            using var httpClient = new HttpClient(handler);
            using var client = new TranslationClient(httpClient);
            var exception = await AssertThrowsAsync<TranslationException>(
                () => client.TranslateAsync(profile, "hello"));
            Assert(exception.Message.Contains("结果", StringComparison.Ordinal)
                || exception.Message.Contains("截断", StringComparison.Ordinal)
                || exception.Message.Contains("格式", StringComparison.Ordinal),
                "malformed or empty response did not produce a safe message");
        }

        var tooLong = new string('x', TranslationClient.MaxInputCharacters + 1);
        using (var httpClient = new HttpClient(new StubHandler((_, _) => Task.FromResult(JsonResponse("unused")))))
        using (var client = new TranslationClient(httpClient))
        {
            var exception = await AssertThrowsAsync<TranslationException>(
                () => client.TranslateAsync(profile, tooLong));
            Assert(exception.Message.Contains("太长", StringComparison.Ordinal), "long input was not bounded");
        }
    }

    private static async Task TestCancellationAsync()
    {
        var handler = new StubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse("unreachable");
        });
        using var httpClient = new HttpClient(handler);
        using var client = new TranslationClient(httpClient);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await AssertThrowsAsync<OperationCanceledException>(
            () => client.TranslateAsync(
                new TranslationProfile("provider", "model", "https://example.test/v1", "key"),
                "hello",
            cancellation.Token));
    }

    private static async Task TestResponseBodyCancellationAsync()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new DelayedContent()
        }));
        using var httpClient = new HttpClient(handler);
        using var client = new TranslationClient(httpClient);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await AssertThrowsAsync<OperationCanceledException>(
            () => client.TranslateAsync(
                new TranslationProfile("provider", "model", "https://example.test/v1", "key"),
                "hello",
                cancellation.Token));
    }

    private static void TestImporter()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "cherrystudio.sqlite");
            NativeSqliteFixture.Create(path, @"
CREATE TABLE preference(scope TEXT,key TEXT,value TEXT);
CREATE TABLE user_model(id TEXT,provider_id TEXT,model_id TEXT,is_enabled INTEGER);
CREATE TABLE user_provider(provider_id TEXT,preset_provider_id TEXT,name TEXT,endpoint_configs TEXT,default_chat_endpoint TEXT,api_keys TEXT,auth_config TEXT,is_enabled INTEGER);
INSERT INTO preference VALUES ('session','feature.translate.model_id','wrong::wrong-model');
INSERT INTO preference VALUES ('default','feature.translate.model_id','silicon::deepseek-ai/DeepSeek-V4-Flash');
INSERT INTO preference VALUES ('default','feature.translate.action.preferred_lang','zh-cn');
INSERT INTO preference VALUES ('default','feature.translate.action.alter_lang','en-us');
INSERT INTO user_model VALUES ('silicon::deepseek-ai/DeepSeek-V4-Flash','silicon-user','deepseek-ai/DeepSeek-V4-Flash',1);
INSERT INTO user_model VALUES ('duplicate-model','deepseek-user','deepseek-ai/DeepSeek-V4-Flash',1);
INSERT INTO user_model VALUES ('deepseek::deepseek-v4-flash','deepseek-user','deepseek-v4-flash',1);
INSERT INTO user_provider VALUES ('silicon-user','silicon','SiliconFlow',NULL,NULL,'[{""id"":""a"",""key"":""disabled"",""isEnabled"":false},{""id"":""b"",""key"":""silicon-test-key"",""isEnabled"":true}]',NULL,1);
INSERT INTO user_provider VALUES ('deepseek-user','deepseek','DeepSeek',NULL,NULL,'[{""id"":""a"",""key"":""deepseek-test-key"",""isEnabled"":true}]',NULL,1);
");

            var importer = new CherryConfigImporter();
            var profile = importer.Import(path);
            Assert(profile.ProviderName == "SiliconFlow", "importer selected the wrong provider");
            Assert(profile.ModelId == "deepseek-ai/DeepSeek-V4-Flash", "importer selected the wrong model");
            Assert(profile.BaseUrl == "https://api.siliconflow.cn/v1", "Silicon preset endpoint mismatch");
            Assert(profile.ApiKey == "silicon-test-key", "importer selected a disabled API key");

            var deepSeek = importer.Import(path, "deepseek::deepseek-v4-flash");
            Assert(deepSeek.ProviderName == "DeepSeek", "qualified model override selected wrong provider");
            Assert(deepSeek.ModelId == "deepseek-v4-flash", "qualified model override selected wrong model");
            Assert(deepSeek.BaseUrl == "https://api.deepseek.com/v1", "DeepSeek preset endpoint mismatch");
            var unqualifiedOverride = AssertThrows<InvalidOperationException>(
                () => importer.Import(path, "deepseek-v4-flash"));
            Assert(unqualifiedOverride.Message.Contains("provider::model", StringComparison.Ordinal),
                "unqualified model override was accepted ambiguously");

            var customPath = Path.Combine(root, "custom.sqlite");
            NativeSqliteFixture.Create(customPath, @"
CREATE TABLE preference(scope TEXT,key TEXT,value TEXT);
CREATE TABLE user_model(id TEXT,provider_id TEXT,model_id TEXT,is_enabled INTEGER);
CREATE TABLE user_provider(provider_id TEXT,preset_provider_id TEXT,name TEXT,endpoint_configs TEXT,default_chat_endpoint TEXT,api_keys TEXT,auth_config TEXT,is_enabled INTEGER);
INSERT INTO preference VALUES ('default','feature.translate.model_id','custom::custom-model');
INSERT INTO user_model VALUES ('custom::custom-model','custom-user','custom-model',1);
INSERT INTO user_provider VALUES ('custom-user','custom','Custom', '{""openai-chat-completions"":{""url"":""https://custom.example/v1""},""anthropic-messages"":{""url"":""https://anthropic.example""}}',NULL,'[{""key"":""custom-key"",""isEnabled"":true}]',NULL,1);
");
            var custom = importer.Import(customPath);
            Assert(custom.BaseUrl == "https://custom.example/v1", "supported custom endpoint was not preserved");

            var unsupportedPath = Path.Combine(root, "unsupported.sqlite");
            NativeSqliteFixture.Create(unsupportedPath, @"
CREATE TABLE preference(scope TEXT,key TEXT,value TEXT);
CREATE TABLE user_model(id TEXT,provider_id TEXT,model_id TEXT,is_enabled INTEGER);
CREATE TABLE user_provider(provider_id TEXT,preset_provider_id TEXT,name TEXT,endpoint_configs TEXT,default_chat_endpoint TEXT,api_keys TEXT,auth_config TEXT,is_enabled INTEGER);
INSERT INTO preference VALUES ('default','feature.translate.model_id','custom::custom-model');
INSERT INTO user_model VALUES ('custom::custom-model','custom-user','custom-model',1);
INSERT INTO user_provider VALUES ('custom-user','custom','Custom', '{""anthropic-messages"":{""url"":""https://anthropic.example""}}',NULL,'[{""key"":""custom-key"",""isEnabled"":true}]',NULL,1);
");
            var unsupported = AssertThrows<InvalidOperationException>(() => importer.Import(unsupportedPath));
            Assert(unsupported.Message.Contains("不支持", StringComparison.Ordinal),
                "unsupported endpoint mapping was silently guessed");
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    private static HttpResponseMessage JsonResponse(string content)
    {
        var body = content.StartsWith("{", StringComparison.Ordinal)
            || content.StartsWith("[", StringComparison.Ordinal)
            ? content
            : $"{{\"choices\":[{{\"message\":{{\"content\":{JsonSerializer.Serialize(content)}}},\"finish_reason\":\"stop\"}}]}}";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private static async Task<TException> AssertThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static TException AssertThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static string CreateTempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "CherryTranslate.CoreTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTempDirectory(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return _handler(request, cancellationToken);
        }
    }

    private sealed class DelayedContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            return Task.Delay(Timeout.InfiniteTimeSpan);
        }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }

    private static class NativeSqliteFixture
    {
        private const int OpenReadWrite = 0x00000002;
        private const int OpenCreate = 0x00000004;

        public static void Create(string path, string sql)
        {
            var result = sqlite3_open_v2(Utf8(path), out var database, OpenReadWrite | OpenCreate, IntPtr.Zero);
            if (result != 0 || database == IntPtr.Zero)
            {
                throw new InvalidOperationException("Could not create SQLite fixture.");
            }

            try
            {
                var error = IntPtr.Zero;
                result = sqlite3_exec(database, Utf8(sql), IntPtr.Zero, IntPtr.Zero, out error);
                if (result != 0)
                {
                    var message = error == IntPtr.Zero ? "unknown SQLite error" : Marshal.PtrToStringUTF8(error);
                    if (error != IntPtr.Zero)
                    {
                        sqlite3_free(error);
                    }

                    throw new InvalidOperationException(message);
                }
            }
            finally
            {
                sqlite3_close_v2(database);
            }
        }

        private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value + "\0");

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_open_v2(
            [In] byte[] filename,
            out IntPtr database,
            int flags,
            IntPtr zvfs);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_exec(
            IntPtr database,
            [In] byte[] sql,
            IntPtr callback,
            IntPtr argument,
            out IntPtr errorMessage);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void sqlite3_free(IntPtr pointer);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_close_v2(IntPtr database);
    }
}
