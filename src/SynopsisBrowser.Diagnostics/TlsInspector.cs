using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using SynopsisBrowser.Core;

namespace SynopsisBrowser.Diagnostics;

public sealed class TlsInspector : ITlsInspector
{
    public async Task<SecuritySnapshot> InspectAsync(Uri uri, IReadOnlyDictionary<string, string>? responseHeaders = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = new SecuritySnapshot
        {
            Url = uri.ToString(),
            IsHttps = uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        };

        if (responseHeaders is not null)
        {
            foreach (var header in responseHeaders) snapshot.Headers[header.Key] = header.Value;
            snapshot.HasHsts = responseHeaders.ContainsKey("Strict-Transport-Security");
            snapshot.HasCsp = responseHeaders.ContainsKey("Content-Security-Policy");
            snapshot.HasXFrameOptions = responseHeaders.ContainsKey("X-Frame-Options");
            snapshot.HasReferrerPolicy = responseHeaders.ContainsKey("Referrer-Policy");
        }

        if (!snapshot.IsHttps)
        {
            snapshot.CertificateValid = null;
            snapshot.CertificateStatus = "HTTP - TLS not enabled";
            return snapshot;
        }

        var port = uri.IsDefaultPort ? 443 : uri.Port;
        using var client = new TcpClient();
        await client.ConnectAsync(uri.Host, port, cancellationToken);

        X509Certificate2? remoteCertificate = null;
        SslPolicyErrors policyErrors = SslPolicyErrors.None;

        using var ssl = new SslStream(client.GetStream(), false, (_, certificate, _, errors) =>
        {
            policyErrors = errors;
            if (certificate is not null) remoteCertificate = new X509Certificate2(certificate);
            return true; // inspection only; this connection never carries application data
        });

        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = uri.Host,
            EnabledSslProtocols = SslProtocols.None,
            CertificateRevocationCheckMode = X509RevocationMode.Online
        }, cancellationToken);

        snapshot.Protocol = ssl.SslProtocol.ToString();
        try { snapshot.CipherSuite = ssl.NegotiatedCipherSuite.ToString(); } catch { }

        if (remoteCertificate is null)
        {
            snapshot.CertificateValid = false;
            snapshot.CertificateStatus = "No server certificate returned";
            return snapshot;
        }

        using (remoteCertificate)
        {
            snapshot.Subject = remoteCertificate.Subject;
            snapshot.Issuer = remoteCertificate.Issuer;
            snapshot.ValidFrom = remoteCertificate.NotBefore;
            snapshot.ValidTo = remoteCertificate.NotAfter;
            snapshot.DaysRemaining = (int)Math.Floor((remoteCertificate.NotAfter.ToUniversalTime() - DateTime.UtcNow).TotalDays);

            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
            chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
            chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(5);
            var chainValid = chain.Build(remoteCertificate);
            snapshot.ChainValid = chainValid;
            snapshot.CertificateValid = policyErrors == SslPolicyErrors.None && chainValid && DateTime.Now >= remoteCertificate.NotBefore && DateTime.Now <= remoteCertificate.NotAfter;
            snapshot.CertificateStatus = snapshot.CertificateValid == true
                ? "Valid"
                : policyErrors == SslPolicyErrors.None ? "Certificate chain validation failed" : policyErrors.ToString();
        }

        return snapshot;
    }
}
