using CherryTranslate.Core;
using System.IO;

namespace CherryTranslate.App;

internal sealed record ProfileLoadResult(
    TranslationProfile? Profile,
    string? Error,
    bool ImportedFromCherry,
    bool LoadedFromStore);

internal sealed class ProfileRuntime
{
    private readonly ProfileStore _store = new();
    private readonly CherryConfigImporter _importer = new();

    public TranslationProfile? Current { get; private set; }

    public void SetCurrent(TranslationProfile profile)
    {
        Current = profile;
    }

    public ProfileLoadResult LoadOrImport()
    {
        try
        {
            var stored = _store.Load();
            if (stored is not null)
            {
                Current = stored;
                return new ProfileLoadResult(stored, null, false, true);
            }
        }
        catch (Exception ex)
        {
            // An unreadable encrypted store should not prevent the tray from opening;
            // importing Cherry's existing settings may still repair the situation.
            var storeError = ToSafeError(ex);
            try
            {
                var importedAfterStoreError = _importer.Import();
                _store.Save(importedAfterStoreError);
                Current = importedAfterStoreError;
                return new ProfileLoadResult(importedAfterStoreError, storeError, true, false);
            }
            catch (Exception importAfterStoreError)
            {
                return new ProfileLoadResult(null, CombineErrors(storeError, importAfterStoreError), false, false);
            }
        }

        try
        {
            var imported = _importer.Import();
            _store.Save(imported);
            Current = imported;
            return new ProfileLoadResult(imported, null, true, false);
        }
        catch (Exception ex)
        {
            return new ProfileLoadResult(null, ToSafeError(ex), false, false);
        }
    }

    public ProfileLoadResult Reimport(string? modelIdOverride = null)
    {
        try
        {
            var imported = string.IsNullOrWhiteSpace(modelIdOverride)
                ? _importer.Import()
                : _importer.Import(null, modelIdOverride);
            _store.Save(imported);
            Current = imported;
            return new ProfileLoadResult(imported, null, true, false);
        }
        catch (Exception ex)
        {
            return new ProfileLoadResult(Current, ToSafeError(ex), false, Current is not null);
        }
    }

    private static string ToSafeError(Exception exception)
    {
        // Do not display exception text: configuration/database errors can include paths,
        // headers, or provider payloads. Keep the user-facing message actionable.
        return exception switch
        {
            UnauthorizedAccessException => "没有权限读取 Cherry 配置或轻译配置。",
            FileNotFoundException => "没有找到 Cherry 配置文件。",
            DirectoryNotFoundException => "没有找到 Cherry 配置目录。",
            InvalidDataException => "Cherry 配置格式无法识别。",
            _ => "读取 Cherry 配置失败。"
        };
    }

    private static string CombineErrors(string first, Exception second)
        => $"{first} {ToSafeError(second)}";
}
