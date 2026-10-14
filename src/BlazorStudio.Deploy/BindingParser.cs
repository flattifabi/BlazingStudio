using System.Globalization;

namespace BlazorStudio.Deploy;

/// <summary>
/// Liest IIS-Bindungen in den gängigen Schreibweisen:
/// "http://*:8080", "http://shop.local", "https://shop.example.com:443/", "*:80:shop.local", "8080".
/// </summary>
public static class BindingParser
{
    public static bool TryParse(string? text, out string protocol, out string bindingInformation)
    {
        protocol = "http";
        bindingInformation = "";
        var s = (text ?? "").Trim();
        if (s.Length == 0) return false;

        // Nur Port
        if (int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var onlyPort))
        {
            if (!ValidPort(onlyPort)) return false;
            bindingInformation = $"*:{onlyPort}:";
            return true;
        }

        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme > 0)
        {
            protocol = s[..scheme].ToLowerInvariant();
            if (protocol is not ("http" or "https")) return false;
            var rest = s[(scheme + 3)..];
            var slash = rest.IndexOf('/');
            if (slash >= 0) rest = rest[..slash];
            if (rest.Length == 0) return false;

            string host;
            int port;
            if (rest.StartsWith('['))
            {
                // IPv6: [::1]:8080
                var end = rest.IndexOf(']');
                if (end < 0) return false;
                host = rest[..(end + 1)];
                var after = rest[(end + 1)..];
                port = after.StartsWith(':') && int.TryParse(after[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var p6) ? p6 : DefaultPort(protocol);
                if (!ValidPort(port)) return false;
                bindingInformation = $"{host}:{port}:";
                return true;
            }

            var colon = rest.LastIndexOf(':');
            if (colon >= 0)
            {
                host = rest[..colon];
                if (!int.TryParse(rest[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out port)) return false;
            }
            else
            {
                host = rest;
                port = DefaultPort(protocol);
            }
            if (!ValidPort(port) || host.Length == 0) return false;

            // "*" oder IP → alle Hostnamen; Name → Host-Header
            if (host == "*" || IsIp(host)) bindingInformation = $"{host}:{port}:";
            else bindingInformation = $"*:{port}:{host.ToLowerInvariant()}";
            return true;
        }

        // IIS-Format IP:Port:Host
        var parts = s.Split(':');
        if (parts.Length == 3 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var p) && ValidPort(p))
        {
            var ip = parts[0].Length == 0 ? "*" : parts[0];
            bindingInformation = $"{ip}:{p}:{parts[2]}";
            return true;
        }
        return false;
    }

    /// <summary>Aufrufbare Adresse einer Bindung ("*" und leere Hosts werden zu "localhost").</summary>
    public static string ToUrl(string protocol, string bindingInformation)
    {
        var (ip, port, host) = Split(bindingInformation);
        var name = host.Length > 0 ? host : ip is "*" or "" or "0.0.0.0" ? "localhost" : ip;
        var standard = (protocol == "http" && port == 80) || (protocol == "https" && port == 443);
        return standard ? $"{protocol}://{name}/" : $"{protocol}://{name}:{port}/";
    }

    public static (string Ip, int Port, string Host) Split(string bindingInformation)
    {
        // IPv6-Adressen enthalten selbst Doppelpunkte: von hinten trennen
        var last = bindingInformation.LastIndexOf(':');
        if (last < 0) return ("*", 0, "");
        var host = bindingInformation[(last + 1)..];
        var head = bindingInformation[..last];
        var mid = head.LastIndexOf(':');
        if (mid < 0) return ("*", 0, host);
        _ = int.TryParse(head[(mid + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port);
        return (head[..mid], port, host);
    }

    private static int DefaultPort(string protocol) => protocol == "https" ? 443 : 80;

    private static bool ValidPort(int port) => port is > 0 and <= 65535;

    private static bool IsIp(string host) => System.Net.IPAddress.TryParse(host.Trim('[', ']'), out _);
}
