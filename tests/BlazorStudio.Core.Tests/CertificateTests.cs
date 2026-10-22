using BlazorStudio.Deploy;

namespace BlazorStudio.Core.Tests;

public class CertificateTests
{
    private const string Renewal = """
    {
      "$schema": "https://win-acme.com/schema/renewal.json",
      "Id": "yQ3kd8sP9Eu",
      "LastFriendlyName": "[Manual] shop.intern.example, api.intern.example @ 2026/10/1",
      "History": [
        { "Date": "2026-09-01T09:12:00+02:00", "Success": true,
          "OrderResults": [ { "Name": "Main", "Success": true, "Thumbprints": [ "AABBCCDDEEFF00112233445566778899AABBCCDD" ], "ExpireDate": "2026-10-01T09:12:00+02:00" } ] },
        { "Date": "2026-10-01T09:30:00+02:00", "Success": false, "ErrorMessages": [ "Authorization failed for shop.intern.example" ] }
      ],
      "TargetPluginOptions": { "CommonName": "shop.intern.example", "AlternativeNames": [ "shop.intern.example", "api.intern.example" ], "$type": "PKISharp.WACS.Plugins.TargetPlugins.ManualOptions, wacs.common" },
      "ValidationPluginOptions": { "Port": 80, "$type": "PKISharp.WACS.Plugins.ValidationPlugins.Http.SelfHostingOptions, wacs.common" },
      "StorePluginOptions": [
        { "$type": "PKISharp.WACS.Plugins.StorePlugins.CertificateStoreOptions, wacs.common" },
        { "Path": "C:\\nginx\\certs", "$type": "PKISharp.WACS.Plugins.StorePlugins.PemFilesOptions, wacs.common" },
        { "Path": "D:\\svc\\certs", "PfxPassword": { "Value": "AQAAANCMnd8BFdERjHoAwE" }, "$type": "PKISharp.WACS.Plugins.StorePlugins.PfxFileOptions, wacs.common" }
      ],
      "InstallationPluginOptions": [
        { "Script": "C:\\BlazorStudio\\Agent\\Cert-Hook.ps1", "ScriptParameters": "-Service 'Shop'", "$type": "PKISharp.WACS.Plugins.InstallationPlugins.ScriptOptions, wacs.common" }
      ]
    }
    """;

    [Fact]
    public void Renewal_is_parsed_without_secrets()
    {
        var r = WinAcmeRenewalParser.Parse(Renewal, "x.renewal.json");
        Assert.Equal("yQ3kd8sP9Eu", r.Id);
        Assert.Equal(new[] { "shop.intern.example", "api.intern.example" }, r.Hosts);
        Assert.False(r.LastSuccess);
        Assert.Contains("Authorization failed", r.LastError);
        Assert.Equal(new DateTime(2026, 9, 1, 7, 12, 0, DateTimeKind.Utc), r.LastSuccessAt);
        Assert.Equal(new DateTime(2026, 10, 1, 7, 12, 0, DateTimeKind.Utc), r.Expires);
        Assert.Equal(new[] { "AABBCCDDEEFF00112233445566778899AABBCCDD" }, r.Thumbprints);
        Assert.Equal(new[] { "Zertifikatsspeicher", "PEM: C:\\nginx\\certs", "PFX: D:\\svc\\certs" }, r.Stores);
        Assert.Equal(new[] { "C:\\nginx\\certs", "D:\\svc\\certs" }, r.StorePaths);
        Assert.Equal("Skript: Cert-Hook.ps1 -Service 'Shop'", Assert.Single(r.Installations));
        Assert.Equal("SelfHosting", r.Validation);
        Assert.DoesNotContain(r.Stores, s => s.Contains("AQAAAN"));
    }

    [Fact]
    public void Due_respects_renewal_days_and_minimum_validity()
    {
        var r = new AcmeRenewalInfo { LastSuccessAt = new DateTime(2026, 10, 1), Expires = new DateTime(2026, 10, 31) };
        Assert.Equal(new DateTime(2026, 11, 25), WinAcmeRenewalParser.Due(r, 55, null));
        Assert.Equal(new DateTime(2026, 10, 24), WinAcmeRenewalParser.Due(r, 23, 7));
        Assert.Equal(new DateTime(2026, 10, 24), WinAcmeRenewalParser.Due(r, 55, 7));
    }

    [Fact]
    public void Policy_maps_to_win_acme_settings_and_evaluates()
    {
        var p = new CertificatePolicy { ValidDays = 30, RenewBeforeDays = 7 };
        Assert.Equal((23, 7), p.ForWinAcme());
        var now = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);
        CertificateInfo C(int validDays, int leftDays) => new() { Thumbprint = "AA", NotBefore = now.AddDays(leftDays - validDays), NotAfter = now.AddDays(leftDays) };
        Assert.Equal(CertificateState.Ok, p.Evaluate(C(30, 20), now));
        Assert.Equal(CertificateState.TooLong, p.Evaluate(C(90, 60), now));
        Assert.Equal(CertificateState.ExpiresSoon, p.Evaluate(C(30, 5), now));
        Assert.Equal(CertificateState.Expired, p.Evaluate(C(30, -1), now));
        var stale = C(30, 20);
        stale.Usages.Add(new CertificateUsage { Kind = "nginx", ServedThumbprint = "BB" });
        Assert.Equal(CertificateState.Stale, p.Evaluate(stale, now));
    }

    [Fact]
    public void Create_arguments_cover_iis_pem_pfx_and_hooks()
    {
        var args = WinAcmeArguments.Create(new AcmeOrderRequest
        {
            Hosts = { "Shop.Intern.Example", "api.intern.example" },
            IisSiteId = 2,
            PemPath = @"C:\nginx\certs",
            PfxPath = @"D:\svc\certs",
            PfxPassword = "geheim",
            RestartService = "ShopWorker",
            ReloadNginx = true,
        }, @"C:\Agent\Cert-Hook.ps1", @"C:\nginx\nginx.exe");

        var line = string.Join(" ", args);
        Assert.StartsWith("--source manual --host shop.intern.example,api.intern.example --validation selfhosting", line);
        Assert.Contains("--store certificatestore,pemfiles,pfxfile --pemfilespath C:\\nginx\\certs --pfxfilepath D:\\svc\\certs --pfxpassword geheim", line);
        Assert.Contains("--installation iis,script --installationsiteid 2 --script C:\\Agent\\Cert-Hook.ps1 --scriptparameters -Service 'ShopWorker' -Nginx 'C:\\nginx\\nginx.exe'", line);
        Assert.EndsWith("--accepttos", line);
        Assert.DoesNotContain("geheim", WinAcmeArguments.Display("wacs.exe", args));
    }

    [Theory]
    [InlineData("bad host")]
    [InlineData("-x.example")]
    public void Create_rejects_invalid_hosts(string host) =>
        Assert.Throws<ArgumentException>(() => WinAcmeArguments.Create(new AcmeOrderRequest { Hosts = { host } }, null, null));

    [Fact]
    public void Nginx_servers_includes_and_certificates_are_found()
    {
        var conf = """
        http {
            include       mime.types;
            ssl_certificate C:/nginx/certs/default-chain.pem;  # Standard
            server {
                listen 443 ssl;
                listen [::]:8443 ssl http2;
                server_name shop.intern.example www.shop.intern.example;
                ssl_certificate "C:/nginx/certs/shop.intern.example-chain.pem";
                ssl_certificate_key C:/nginx/certs/shop.intern.example-key.pem;
                location / { proxy_pass http://127.0.0.1:5000; }
            }
            server { listen 80; server_name _; }
            include sites/*.conf;
        }
        """;
        var r = NginxConfigParser.Parse(conf);
        Assert.Equal(new[] { "mime.types", "sites/*.conf" }, r.Includes);
        Assert.Equal(new[] { "C:/nginx/certs/default-chain.pem" }, r.DefaultCertificates);
        var shop = r.Servers.Single(s => s.Certificates.Count > 0);
        Assert.Equal(new[] { "shop.intern.example", "www.shop.intern.example" }, shop.Names);
        Assert.Equal(new[] { "C:/nginx/certs/shop.intern.example-chain.pem" }, shop.Certificates);
        Assert.Equal(new[] { 443, 8443 }, shop.SslPorts);
        Assert.Empty(r.Servers.Single(s => s.Certificates.Count == 0).Names);
    }

    [Fact]
    public void Kestrel_certificates_are_found()
    {
        var json = """
        { "Kestrel": {
            "Certificates": { "Default": { "Path": "certs/app.pfx", "Password": "pw" } },
            "Endpoints": { "Https": { "Url": "https://*:5001", "Certificate": { "Path": "C:\\c\\api.pem", "KeyPath": "C:\\c\\api-key.pem" } } }
        } }
        """;
        var refs = KestrelCertificates.Find(json);
        Assert.Equal(2, refs.Count);
        Assert.Equal(("certs/app.pfx", "pw"), (refs[0].Path, refs[0].Password));
        Assert.Equal("Kestrel:Endpoints:Https:Certificate", refs[1].Section);
        Assert.Equal(@"C:\c\api-key.pem", refs[1].KeyPath);
        Assert.Empty(KestrelCertificates.Find("{ not json"));
    }
}
