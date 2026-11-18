using System;
using System.Buffers.Binary;
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
using Org.BouncyCastle.Crypto.Parameters;

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
                BuildOpenSshEd25519Pem(privateSeed),
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
            var signatureBlob = Convert.FromBase64String(authorizationParts[1]);
            var signatureOffset = 0;
            var signatureAlgorithm = ReadSshString(signatureBlob, ref signatureOffset);
            Assert.AreEqual("ssh-ed25519", Encoding.ASCII.GetString(signatureAlgorithm));
            var rawSignature = ReadSshString(signatureBlob, ref signatureOffset);
            Assert.AreEqual(64, rawSignature.Length);
            Assert.AreEqual(
                signatureBlob.Length,
                signatureOffset,
                "The SSH signature blob must not contain trailing data.");

            var sshPublicKey = new SshKeyData(publicKeyBlob);
            Assert.AreEqual("ssh-ed25519", sshPublicKey.Name);
            using var publicKey = new ED25519Key(sshPublicKey);

            var emptyBodyHashHex = Convert.ToHexString(SHA256.HashData(Array.Empty<byte>()))
                .ToLowerInvariant();
            var encodedEmptyBodyHash = Convert.ToBase64String(Encoding.UTF8.GetBytes(emptyBodyHashHex));
            var signedPayload = Encoding.UTF8.GetBytes(
                "GET," + tokenRequestUri!.AbsoluteUri + "," + encodedEmptyBodyHash);

            Assert.IsTrue(
                publicKey.VerifySignature(signedPayload, rawSignature),
                "The SSH-encoded Authorization signature must verify against the public key sent to the Registry.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateSeed);
            try { Directory.Delete(profileDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }


    [TestMethod]
    [Timeout(120000)]
    public async Task LiveRegistryManifestAccessAndOptionalOllamaSignatureChallenge()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("LAYAL_RUN_REGISTRY_LIVE_TESTS"),
            "1",
            StringComparison.Ordinal))
        {
            Assert.Inconclusive("Set LAYAL_RUN_REGISTRY_LIVE_TESTS=1 to enable the live Ollama Registry check.");
        }

        var configuredProfile = Environment.GetEnvironmentVariable("LAYAL_OLLAMA_PROFILE_DIRECTORY");
        var ownsProfile = string.IsNullOrWhiteSpace(configuredProfile);
        var profileDirectory = ownsProfile
            ? Path.Combine(
                Path.GetTempPath(),
                "layal-phase-c-registry-live-" + Guid.NewGuid().ToString("N"))
            : Path.GetFullPath(configuredProfile!);
        var privateSeed = ownsProfile ? RandomNumberGenerator.GetBytes(32) : null;
        var ollamaDirectory = Path.Combine(profileDirectory, ".ollama");

        try
        {
            if (ownsProfile)
            {
                Directory.CreateDirectory(ollamaDirectory);
                await File.WriteAllTextAsync(
                    Path.Combine(ollamaDirectory, "id_ed25519"),
                    BuildOpenSshEd25519Pem(privateSeed!),
                    Encoding.ASCII);
            }
            else if (!File.Exists(Path.Combine(ollamaDirectory, "id_ed25519")))
            {
                Assert.Fail(
                    "The requested Ollama profile does not contain .ollama/id_ed25519. Start Ollama once to initialize its keypair.");
            }

            using var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(20),
                PooledConnectionLifetime = TimeSpan.FromMinutes(2)
            };
            using var client = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(110));

            using var initialRequest = CreateManifestRequest();
            using var initialResponse = await client.SendAsync(
                initialRequest,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);

            HttpResponseMessage manifestResponse = initialResponse;
            HttpResponseMessage? authenticatedResponse = null;
            var exercisedSignedChallenge = false;

            if (initialResponse.StatusCode == HttpStatusCode.Unauthorized)
            {
                var challenge = initialResponse.Headers.WwwAuthenticate
                    .FirstOrDefault(item => string.Equals(
                        item.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
                    ?.ToString();
                Assert.IsFalse(
                    string.IsNullOrWhiteSpace(challenge),
                    "A Registry 401 response must contain a Bearer challenge.");

                var bearerToken = await OllamaRegistryAuthTokenProvider.GetBearerTokenAsync(
                    client,
                    OllamaRegistryManifestClient.DefaultManifestUri,
                    challenge!,
                    timeout.Token,
                    profileDirectory);

                using var authenticatedRequest = CreateManifestRequest();
                authenticatedRequest.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
                authenticatedResponse = await client.SendAsync(
                    authenticatedRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token);
                manifestResponse = authenticatedResponse;
                exercisedSignedChallenge = true;
            }
            else
            {
                Assert.AreEqual(
                    HttpStatusCode.OK,
                    initialResponse.StatusCode,
                    "The public Qwen Registry manifest must be available anonymously (200) or require a supported Bearer challenge (401).");
            }

            Assert.AreEqual(
                HttpStatusCode.OK,
                manifestResponse.StatusCode,
                exercisedSignedChallenge
                    ? "The live Registry must accept the signed token request and return the protected Qwen manifest."
                    : "The live public Qwen Registry manifest must be returned successfully without requiring authentication.");

            try
            {
                var body = await manifestResponse.Content.ReadAsByteArrayAsync(timeout.Token);
                Assert.IsTrue(body.Length is > 0 and <= 1024 * 1024);
                using var document = System.Text.Json.JsonDocument.Parse(body);
                Assert.AreEqual(System.Text.Json.JsonValueKind.Object, document.RootElement.ValueKind);
                Assert.IsTrue(
                    document.RootElement.TryGetProperty("schemaVersion", out var schemaVersion) &&
                    schemaVersion.ValueKind == System.Text.Json.JsonValueKind.Number &&
                    schemaVersion.GetInt32() == 2,
                    "The Registry must return a supported schemaVersion 2 manifest.");

                var digest = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
                if (manifestResponse.Headers.TryGetValues("Docker-Content-Digest", out var digestHeaders))
                {
                    var values = digestHeaders.ToArray();
                    Assert.AreEqual(1, values.Length);
                    Assert.AreEqual("sha256:" + digest, values[0], ignoreCase: true);
                }
            }
            finally
            {
                authenticatedResponse?.Dispose();
            }
        }
        finally
        {
            if (privateSeed is not null)
                CryptographicOperations.ZeroMemory(privateSeed);
            if (ownsProfile)
            {
                try { Directory.Delete(profileDirectory, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static HttpRequestMessage CreateManifestRequest()
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get, OllamaRegistryManifestClient.DefaultManifestUri);
        request.Headers.Accept.ParseAdd("application/vnd.ollama.manifest.list.v2+json");
        request.Headers.Accept.ParseAdd("application/vnd.docker.distribution.manifest.v2+json");
        request.Headers.Accept.ParseAdd("application/vnd.oci.image.index.v1+json");
        request.Headers.Accept.ParseAdd("application/vnd.oci.image.manifest.v1+json");
        request.Headers.AcceptEncoding.ParseAdd("identity");
        return request;
    }

    private static byte[] ReadSshString(byte[] blob, ref int offset)
    {
        if (offset < 0 || blob.Length - offset < sizeof(uint))
            throw new InvalidDataException("The SSH signature blob ended before a string length.");

        var length = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(offset, sizeof(uint)));
        offset += sizeof(uint);
        if (length > int.MaxValue || length > (uint)(blob.Length - offset))
            throw new InvalidDataException("The SSH signature blob contains an invalid string length.");

        var value = new byte[(int)length];
        Buffer.BlockCopy(blob, offset, value, 0, value.Length);
        offset += value.Length;
        return value;
    }

    private static string BuildOpenSshEd25519Pem(byte[] seed)
    {
        if (seed.Length != 32)
            throw new ArgumentException("Ed25519 private seed must contain exactly 32 bytes.", nameof(seed));

        var privateKey = new Ed25519PrivateKeyParameters(seed, 0);
        var publicKey = privateKey.GeneratePublicKey().GetEncoded();
        var keyType = Encoding.ASCII.GetBytes("ssh-ed25519");

        using var publicKeyBlob = new MemoryStream();
        WriteSshString(publicKeyBlob, keyType);
        WriteSshString(publicKeyBlob, publicKey);

        using var privateSection = new MemoryStream();
        var checkInteger = RandomNumberGenerator.GetBytes(4);
        privateSection.Write(checkInteger);
        privateSection.Write(checkInteger);
        WriteSshString(privateSection, keyType);
        WriteSshString(privateSection, publicKey);

        var privateKeyAndPublicKey = new byte[seed.Length + publicKey.Length];
        Buffer.BlockCopy(seed, 0, privateKeyAndPublicKey, 0, seed.Length);
        Buffer.BlockCopy(publicKey, 0, privateKeyAndPublicKey, seed.Length, publicKey.Length);
        WriteSshString(privateSection, privateKeyAndPublicKey);
        WriteSshString(privateSection, Encoding.UTF8.GetBytes("layal-phase-c-test"));

        var paddingLength = (int)((8 - (privateSection.Length % 8)) % 8);
        for (var index = 1; index <= paddingLength; index++)
            privateSection.WriteByte((byte)index);

        using var opensshKey = new MemoryStream();
        opensshKey.Write(Encoding.ASCII.GetBytes("openssh-key-v1"));
        opensshKey.WriteByte(0);
        WriteSshString(opensshKey, Encoding.ASCII.GetBytes("none"));
        WriteSshString(opensshKey, Encoding.ASCII.GetBytes("none"));
        WriteSshString(opensshKey, Array.Empty<byte>());
        WriteUInt32BigEndian(opensshKey, 1);
        WriteSshString(opensshKey, publicKeyBlob.ToArray());
        WriteSshString(opensshKey, privateSection.ToArray());

        var base64 = Convert.ToBase64String(opensshKey.ToArray());
        var lines = Enumerable.Range(0, (base64.Length + 69) / 70)
            .Select(index => base64.Substring(index * 70, Math.Min(70, base64.Length - index * 70)));
        var marker = string.Concat("OPEN", "SSH", " PRIVATE KEY");
        var delimiter = new string('-', 5);
        return delimiter + "BEGIN " + marker + delimiter + "\n" +
               string.Join("\n", lines) +
               "\n" + delimiter + "END " + marker + delimiter + "\n";
    }

    private static void WriteSshString(Stream output, byte[] value)
    {
        WriteUInt32BigEndian(output, value.Length);
        output.Write(value);
    }

    private static void WriteUInt32BigEndian(Stream output, int value)
    {
        Span<byte> encoded = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(encoded, checked((uint)value));
        output.Write(encoded);
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
