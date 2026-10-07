namespace CherryTranslate.Core;

/// <summary>
/// The small, independent set of settings needed to call an OpenAI-compatible
/// translation endpoint.  This type deliberately contains no Cherry Studio
/// implementation details so the saved profile can outlive Cherry Studio.
/// </summary>
public sealed record TranslationProfile(
    string ProviderName,
    string ModelId,
    string BaseUrl,
    string ApiKey,
    string TargetLanguage = "zh-cn",
    string AlternateLanguage = "en-us")
{
    public override string ToString()
    {
        return $"TranslationProfile {{ ProviderName = {ProviderName}, ModelId = {ModelId}, " +
               $"BaseUrl = {BaseUrl}, ApiKey = <redacted>, TargetLanguage = {TargetLanguage}, " +
               $"AlternateLanguage = {AlternateLanguage} }}";
    }
}
