using System.Net;
using Microsoft.Win32;

namespace Glance.Core;

public static class ProxyResolver
{
    public static HttpClientHandler CreateHandler(TranslatorSettings settings, bool allowAutoRedirect = true)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = allowAutoRedirect };
        var proxy = Resolve(settings);
        if (string.IsNullOrEmpty(proxy))
        {
            handler.UseProxy = false;
            return handler;
        }

        handler.UseProxy = true;
        handler.Proxy = new WebProxy(proxy);
        return handler;
    }

    public static string? Resolve(TranslatorSettings settings) => settings.ProxyMode switch
    {
        ProxyMode.None => null,
        ProxyMode.Custom => Normalize(settings.CustomProxy),
        _ => SystemProxyUrl(),
    };

    public static string? Normalize(string? raw)
    {
        var t = raw?.Trim() ?? "";
        if (string.IsNullOrEmpty(t)) return null;
        return t.Contains("://", StringComparison.Ordinal) ? t : "http://" + t;
    }

    public static string? SystemProxyUrl()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key is null) return null;
            if (Convert.ToInt32(key.GetValue("ProxyEnable", 0)) == 0) return null;
            var server = key.GetValue("ProxyServer") as string;
            if (string.IsNullOrWhiteSpace(server)) return null;
            if (!server.Contains('=')) return "http://" + server;
            string? http = null;
            foreach (var part in server.Split(';'))
            {
                var kv = part.Split('=', 2);
                if (kv.Length != 2 || string.IsNullOrWhiteSpace(kv[1])) continue;
                if (kv[0].Equals("https", StringComparison.OrdinalIgnoreCase))
                    return "http://" + kv[1].Trim();
                if (kv[0].Equals("http", StringComparison.OrdinalIgnoreCase))
                    http = "http://" + kv[1].Trim();
            }
            return http;
        }
        catch
        {
            return null;
        }
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
        var key = (ProxyResolver.Resolve(settings) ?? "") + (allowAutoRedirect ? "|1" : "|0");
        if (_http is not null && _key == key)
            return _http;

        _http?.Dispose();
        _http = new HttpClient(ProxyResolver.CreateHandler(settings, allowAutoRedirect));
        configure?.Invoke(_http);
        _key = key;
        return _http;
    }
}
