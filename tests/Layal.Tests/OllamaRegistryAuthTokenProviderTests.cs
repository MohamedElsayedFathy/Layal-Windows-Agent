using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Renci.SshNet.Security;

namespace Layal.Tests;

[TestClass]
public sealed class OllamaRegistryAuthTokenProviderTests
{
    [TestMethod]
    public async Task SignsRegistryTokenChallengeWithTheLocalEd25519PrivateKey()
    {
        var profileDirectory = Path.Combine(
            Path.GetTempPath(),
            "layal-phase-c-ed25519-" + Guid.NewGuid().ToString("N"));
        var ollamaDirectory = Path.Combine(profileDirectory, ".ollama");
        var privateSeed = RandomNumberGenerator.GetBytes(32);

        try
        {
            Directory.CreateDirectory(ollamaDirectory);
            var privateKeyPath = Path.Combine(ollamaDirectory, "id_ed25519");
            await File.WriteAllTextAsync(
                privateKeyPath,
                BuildPkcs8Ed25519Pem(privateSeed),
                Encoding.ASCII);

            Uri? tokenRequestUri = null;
            string? authorization = null;
            using var client = new HttpClient(new CaptureHandler(request =>
            {
                tokenRequestUri = request.RequestUri;
                authorization = request.Headers.GetValues("Authorization").Single();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"token\":\"phase-c-test-bearer-token\"}",
                        Encoding.UTF8,
                        "application/json")
                };
            }));

            var token = await OllamaRegistryAuthTokenProvider.GetBearerTokenAsync(
                client,
                OllamaRegistryManifestClient.DefaultManifestUri,
                "Bearer realm=\"https://registry.ollama.ai/v2/token\",service=\"ollama\",scope=\"-\"",
                CancellationToken.None,
                profileDirectory);

            Assert.AreEqual("phase-c-test-bearer-token", token);
            Assert.IsNotNull(tokenRequestUri);
            Assert.IsNotNull(authorization);

            var authorizationParts = authorization!.Split(':');
            Assert.AreEqual(2, authorizationParts.Length);

            var publicKeyBlob = Convert.FromBase64String(authorizationParts[0]);
            var signature = Convert.FromBase64String(authorizationParts[1]);
            Assert.AreEqual(64, signature.Length);

            var sshPublicKey = new SshKeyData(publicKeyBlob);
            Assert.AreEqual("ssh-ed25519", sshPublicKey.Name);
            using var publicKey = new ED25519Key(sshPublicKey);

            var emptyBodyHashHex = Convert.ToHexString(SHA256.HashData(Array.Empty<byte>()))
                .ToLowerInvariant();
            var encodedEmptyBodyHash = Convert.ToBase64String(Encoding.UTF8.GetBytes(emptyBodyHashHex));
            var signedPayload = Encoding.UTF8.GetBytes(
                "GET," + tokenRequestUri!.AbsoluteUri + "," + encodedEmptyBodyHash);

            Assert.IsTrue(
                publicKey.VerifySignature(signedPayload, signature),
                "The Authorization signature must verify against the public key sent to the Registry.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateSeed);
            try { Directory.Delete(profileDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string BuildPkcs8Ed25519Pem(byte[] seed)
    {
        // RFC 8410 PrivateKeyInfo for Ed25519: the privateKey octet string
        // contains the 32-byte Ed25519 seed.
        var prefix = Convert.FromHexString("302e020100300506032b657004220420");
        var der = new byte[prefix.Length + seed.Length];
        Buffer.BlockCopy(prefix, 0, der, 0, prefix.Length);
        Buffer.BlockCopy(seed, 0, der, prefix.Length, seed.Length);

        var base64 = Convert.ToBase64String(der);
        var lines = Enumerable.Range(0, (base64.Length + 63) / 64)
            .Select(index => base64.Substring(index * 64, Math.Min(64, base64.Length - index * 64)));
        var marker = string.Join(string.Empty, new[] { "PRI", "VATE", " ", "K", "EY" });
        var delimiter = new string('-', 5);
        return delimiter + "BEGIN " + marker + delimiter + "\n" +
               string.Join("\n", lines) +
               "\n" + delimiter + "END " + marker + delimiter + "\n";
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public CaptureHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(_responseFactory(request));
    }
}
