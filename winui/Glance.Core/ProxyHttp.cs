using System.Net;

namespace Glance.Core;

public static class ProxyResolver
{
    public static HttpClientHandler CreateHandler(TranslatorSettings settings, bool allowAutoRedirect = true)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = allowAutoRedirect };
        switch (settings.ProxyMode)
        {
            case ProxyMode.None:
                handler.UseProxy = false;
                break;
            case ProxyMode.Custom:
            {
                var proxy = Normalize(settings.CustomProxy);
                if (proxy is null)
                {
                    handler.UseProxy = false;
                }
                else
                {
                    handler.UseProxy = true;
                    handler.Proxy = new WebProxy(proxy);
                }
                break;
            }
            default:
                // System proxy: leave Proxy unset so HttpClientHandler uses OS defaults.
                handler.UseProxy = true;
                break;
        }
        return handler;
    }

    public static string? Resolve(TranslatorSettings settings) => settings.ProxyMode switch
    {
        ProxyMode.None => null,
        ProxyMode.Custom => Normalize(settings.CustomProxy),
        _ => "", // sentinel: system proxy (handler uses OS defaults)
    };

    public static string? Normalize(string? raw)
    {
        var t = raw?.Trim() ?? "";
        if (string.IsNullOrEmpty(t)) return null;
        return t.Contains("://", StringComparison.Ordinal) ? t : "http://" + t;
    }
}

/// <summary>One cached HttpClient per proxy setting.</summary>
public sealed class ProxiedHttp
{
    private HttpClient? _http;
    private string _key = "\0";

    public HttpClient Get(
        TranslatorSettings settings,
        bool allowAutoRedirect = true,
        Action<HttpClient>? configure = null)
    {
        var key = (ProxyResolver.Resolve(settings) ?? "\0") + (allowAutoRedirect ? "|1" : "|0");
        if (_http is not null && _key == key)
            return _http;

        _http?.Dispose();
        _http = new HttpClient(ProxyResolver.CreateHandler(settings, allowAutoRedirect));
        configure?.Invoke(_http);
        _key = key;
        return _http;
    }
}
