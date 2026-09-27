using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Desktop.Client.Services;
using Xunit;

namespace CommandCenter.Tests;

public class CertificatePinningPersistenceTests
{
    private static X509Certificate2 CreateCertificate(string subject)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={subject}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private sealed class InMemoryPinStore : ICertificatePinStore
    {
        private readonly Dictionary<string, string> _pins = new(StringComparer.OrdinalIgnoreCase);

        public int SaveCount { get; private set; }

        public IReadOnlyDictionary<string, string> Load()
        {
            return new Dictionary<string, string>(_pins, StringComparer.OrdinalIgnoreCase);
        }

        public void Save(IReadOnlyDictionary<string, string> pins)
        {
            SaveCount++;
            _pins.Clear();
            foreach (var pair in pins)
            {
                _pins[pair.Key] = pair.Value;
            }
        }
    }

    [Fact]
    public void IsTrusted_FirstContactPrivateHost_PinsCertificate()
    {
        var store = new InMemoryPinStore();
        var validator = new CertificatePinValidator(store);
        using var certificate = CreateCertificate("server-a");

        Assert.True(validator.IsTrusted("192.168.1.50", certificate, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.Equal(1, store.SaveCount);
        Assert.True(validator.IsTrusted("192.168.1.50", certificate, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public void IsTrusted_DifferentCertificateAfterPin_IsRejected()
    {
        var store = new InMemoryPinStore();
        var validator = new CertificatePinValidator(store);
        using var trusted = CreateCertificate("server-a");
        using var impostor = CreateCertificate("server-b");

        Assert.True(validator.IsTrusted("192.168.1.50", trusted, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(validator.IsTrusted("192.168.1.50", impostor, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void IsTrusted_ValidChain_IsTrustedWithoutPinning()
    {
        var store = new InMemoryPinStore();
        var validator = new CertificatePinValidator(store);
        using var certificate = CreateCertificate("server-a");

        Assert.True(validator.IsTrusted("192.168.1.50", certificate, SslPolicyErrors.None));
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public void IsTrusted_PublicHostWithInvalidChain_IsRejectedWithoutPinning()
    {
        var store = new InMemoryPinStore();
        var validator = new CertificatePinValidator(store);
        using var certificate = CreateCertificate("server-a");

        Assert.False(validator.IsTrusted("8.8.8.8", certificate, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public void IsTrusted_MissingCertificate_IsRejected()
    {
        var validator = new CertificatePinValidator(new InMemoryPinStore());

        Assert.False(validator.IsTrusted("192.168.1.50", null, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void DpapiCertificatePinStore_PersistsPinsAcrossValidatorInstances()
    {
        string storePath = Path.Combine(Path.GetTempPath(), $"pos-cert-pins-{Guid.NewGuid():N}.dat");

        try
        {
            using var trusted = CreateCertificate("server-a");
            using var impostor = CreateCertificate("server-b");

            var firstValidator = new CertificatePinValidator(new DpapiCertificatePinStore(storePath));
            Assert.True(firstValidator.IsTrusted("192.168.1.50", trusted, SslPolicyErrors.RemoteCertificateChainErrors));
            Assert.True(File.Exists(storePath));

            var secondValidator = new CertificatePinValidator(new DpapiCertificatePinStore(storePath));
            Assert.True(secondValidator.IsTrusted("192.168.1.50", trusted, SslPolicyErrors.RemoteCertificateChainErrors));
            Assert.False(secondValidator.IsTrusted("192.168.1.50", impostor, SslPolicyErrors.RemoteCertificateChainErrors));
        }
        finally
        {
            if (File.Exists(storePath))
            {
                File.Delete(storePath);
            }
        }
    }

    [Fact]
    public void DpapiCertificatePinStore_CorruptFile_LoadsEmptyWithoutThrowing()
    {
        string storePath = Path.Combine(Path.GetTempPath(), $"pos-cert-pins-{Guid.NewGuid():N}.dat");

        try
        {
            File.WriteAllBytes(storePath, new byte[] { 1, 2, 3, 4 });

            var store = new DpapiCertificatePinStore(storePath);

            Assert.Empty(store.Load());
        }
        finally
        {
            if (File.Exists(storePath))
            {
                File.Delete(storePath);
            }
        }
    }
}
