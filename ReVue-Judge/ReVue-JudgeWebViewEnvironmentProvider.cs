using Microsoft.Web.WebView2.Core;

namespace ReVueJudge;

internal static class WebViewEnvironmentProvider
{
    private static readonly Lazy<Task<CoreWebView2Environment>> SharedEnvironment =
        new(CreateSharedEnvironmentAsync);

    public static Task<CoreWebView2Environment> GetAsync() => SharedEnvironment.Value;

    private static async Task<CoreWebView2Environment> CreateSharedEnvironmentAsync()
    {
        Directory.CreateDirectory(ReVueJudgeConfigStore.WebView2UserDataDir);
        return await CoreWebView2Environment.CreateAsync(userDataFolder: ReVueJudgeConfigStore.WebView2UserDataDir);
    }
}
