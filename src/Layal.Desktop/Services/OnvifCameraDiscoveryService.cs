using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace Layal.Services;

public sealed record OnvifCameraDevice(string DisplayName, string DeviceServiceAddress, string Host, string Scopes);

/// <summary>
/// Discovers ONVIF-advertising cameras on the directly attached local network.
/// Discovery does not authenticate, open media streams, or attempt to guess credentials.
/// </summary>
public sealed class OnvifCameraDiscoveryService
{
    private static readonly IPAddress DiscoveryAddress = IPAddress.Parse("239.255.255.250");
    private const int DiscoveryPort = 3702;
    private static readonly TimeSpan DefaultDiscoveryWindow = TimeSpan.FromSeconds(4);
    private static readonly XNamespace SoapEnvelope = "http://www.w3.org/2003/05/soap-envelope";
    private static readonly XNamespace WsAddressing = "http://schemas.xmlsoap.org/ws/2004/08/addressing";
    private static readonly XNamespace WsSecurity = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
    private static readonly XNamespace WsUtility = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";
    private static readonly XNamespace DeviceNs = "http://www.onvif.org/ver10/device/wsdl";
    private static readonly XNamespace MediaNs = "http://www.onvif.org/ver10/media/wsdl";

    public async Task<IReadOnlyList<OnvifCameraDevice>> DiscoverAsync(
        TimeSpan? discoveryWindow = null,
        CancellationToken cancellationToken = default)
    {
        var duration = discoveryWindow ?? DefaultDiscoveryWindow;
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromSeconds(15))
            throw new ArgumentOutOfRangeException(nameof(discoveryWindow), "Discovery window must be between 0 and 15 seconds.");

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        var probe = Encoding.UTF8.GetBytes(BuildDiscoveryProbe());
        await udp.SendAsync(probe, new IPEndPoint(DiscoveryAddress, DiscoveryPort), cancellationToken);

        var deadline = DateTimeOffset.UtcNow + duration;
        var discovered = new Dictionary<string, OnvifCameraDevice>(StringComparer.OrdinalIgnoreCase);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = deadline - DateTimeOffset.UtcNow;
            using var receiveCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            receiveCancellation.CancelAfter(remaining);
            UdpReceiveResult packet;
            try
            {
                packet = await udp.ReceiveAsync(receiveCancellation.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var camera = TryParseProbeResponse(Encoding.UTF8.GetString(packet.Buffer), packet.RemoteEndPoint.Address);
            if (camera is not null)
                discovered.TryAdd(camera.DeviceServiceAddress, camera);
        }

        return discovered.Values.OrderBy(camera => camera.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static OnvifCameraDevice? TryParseProbeResponse(string xml, IPAddress senderAddress)
    {
        if (string.IsNullOrWhiteSpace(xml) || xml.Length > 256 * 1024)
            return null;

        try
        {
            var document = ParseSafeXml(xml);
            var probeMatch = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "ProbeMatch");
            if (probeMatch is null) return null;

            var addresses = probeMatch.Elements().FirstOrDefault(element => element.Name.LocalName == "XAddrs")?.Value;
            if (string.IsNullOrWhiteSpace(addresses)) return null;

            var host = NormalizePrivateAddress(senderAddress);
            var scopes = probeMatch.Elements().FirstOrDefault(element => element.Name.LocalName == "Scopes")?.Value ?? string.Empty;
            foreach (var address in addresses.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(8))
            {
                if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
                    uri.Scheme is not ("http" or "https") ||
                    !string.IsNullOrEmpty(uri.UserInfo) ||
                    !IsPrivateHost(uri.Host))
                    continue;

                // ONVIF discovery may return a hostname/IP different from the packet sender.
                // Only accept private literal IPs, and normalize the endpoint to avoid credential/redirect ambiguity.
                var endpointHost = NormalizePrivateAddress(IPAddress.Parse(uri.Host));
                if (!IPAddress.TryParse(uri.Host, out _)) continue;
                var builder = new UriBuilder(uri) { Host = endpointHost, Fragment = string.Empty };
                var safeAddress = builder.Uri.AbsoluteUri;
                var displayName = BuildDisplayName(scopes, endpointHost);
                return new OnvifCameraDevice(displayName, safeAddress, endpointHost, scopes);
            }

            return null;
        }
        catch (Exception ex) when (ex is XmlException or FormatException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    public async Task<Uri> GetAuthenticatedStreamUriAsync(
        OnvifCameraDevice camera,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("A camera username is required.", nameof(username));
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("A camera password is required.", nameof(password));
        if (!IsSafeDeviceServiceUri(camera.DeviceServiceAddress, out var deviceUri))
            throw new InvalidOperationException("The discovered device endpoint is not a private local-network address.");

        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(username, password),
            PreAuthenticate = true
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };

        var servicesXml = await SendSoapAsync(client, deviceUri, "GetServices",
            "<td:GetServices><td:IncludeCapability>false</td:IncludeCapability></td:GetServices>",
            username, password, cancellationToken);
        var mediaAddress = FindMediaServiceAddress(servicesXml);
        if (mediaAddress is null)
        {
            var capabilitiesXml = await SendSoapAsync(client, deviceUri, "GetCapabilities",
                "<td:GetCapabilities><td:Category>All</td:Category></td:GetCapabilities>",
                username, password, cancellationToken);
            mediaAddress = FindMediaCapabilityAddress(capabilitiesXml);
        }

        if (mediaAddress is null || !IsSafeDeviceServiceUri(mediaAddress, out var mediaUri))
            throw new InvalidOperationException("The camera did not report a safe local ONVIF media service endpoint.");

        var profilesXml = await SendSoapAsync(client, mediaUri, "GetProfiles", "<trt:GetProfiles/>",
            username, password, cancellationToken);
        var profile = ParseSafeXml(profilesXml).Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "Profiles" &&
                                       !string.IsNullOrWhiteSpace((string?)element.Attribute("token")));
        var profileToken = (string?)profile?.Attribute("token");
        if (string.IsNullOrWhiteSpace(profileToken))
            throw new InvalidOperationException("The camera returned no usable ONVIF media profile.");

        var profileXml = new System.Xml.Linq.XElement("ProfileToken", profileToken).ToString(SaveOptions.DisableFormatting);
        var streamSetup = "<trt:StreamSetup><tt:Stream>RTP-Unicast</tt:Stream><tt:Transport><tt:Protocol>RTSP</tt:Protocol></tt:Transport></trt:StreamSetup>";
        var escapedToken = System.Security.SecurityElement.Escape(profileToken) ?? string.Empty;
        var streamXml = await SendSoapAsync(client, mediaUri, "GetStreamUri",
            streamSetup + "<trt:ProfileToken>" + escapedToken + "</trt:ProfileToken>",
            username, password, cancellationToken);
        var streamText = ParseSafeXml(streamXml).Descendants().FirstOrDefault(element => element.Name.LocalName == "Uri")?.Value;
        if (!Uri.TryCreate(streamText, UriKind.Absolute, out var streamUri) ||
            streamUri.Scheme is not ("rtsp" or "rtsps") ||
            !IsPrivateHost(streamUri.Host))
            throw new InvalidOperationException("The camera returned no usable RTSP stream URI on the local network.");

        // Return the stream address without embedded credentials. Credentials are applied only in-memory
        // by the camera preview window and are never written to settings, chat, or logs.
        return new UriBuilder(streamUri) { UserName = string.Empty, Password = string.Empty, Fragment = string.Empty }.Uri;
    }

    public static string BuildAuthenticatedStreamAddress(Uri streamUri, string username, string password)
    {
        ArgumentNullException.ThrowIfNull(streamUri);
        if (streamUri.Scheme is not ("rtsp" or "rtsps") || !IsPrivateHost(streamUri.Host))
            throw new ArgumentException("The stream must be RTSP on a private local-network host.", nameof(streamUri));
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("A camera username is required.", nameof(username));
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("A camera password is required.", nameof(password));

        return new UriBuilder(streamUri)
        {
            UserName = username,
            Password = password
        }.Uri.AbsoluteUri;
    }

    public static bool IsPrivateHost(string host)
    {
        if (!IPAddress.TryParse(host, out var address)) return false;
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10 ||
                   (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168) ||
                   (bytes[0] == 169 && bytes[1] == 254);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return address.IsIPv6LinkLocal || (bytes[0] & 0xFE) == 0xFC;
        }

        return false;
    }

    private static string BuildDiscoveryProbe() =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <e:Envelope xmlns:e="http://www.w3.org/2003/05/soap-envelope"
          xmlns:w="http://schemas.xmlsoap.org/ws/2004/08/addressing"
          xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery"
          xmlns:td="http://www.onvif.org/ver10/device/wsdl">
          <e:Header>
            <w:MessageID>urn:uuid:{Guid.NewGuid():D}</w:MessageID>
            <w:To>urn:schemas-xmlsoap-org:ws:2005:04:discovery</w:To>
            <w:Action>http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</w:Action>
          </e:Header>
          <e:Body><d:Probe><d:Types>td:NetworkVideoTransmitter</d:Types></d:Probe></e:Body>
        </e:Envelope>
        """;

    private static async Task<string> SendSoapAsync(
        HttpClient client, Uri endpoint, string action, string body, string username, string password, CancellationToken cancellationToken)
    {
        var created = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
        var nonce = RandomNumberGenerator.GetBytes(20);
        var digestInput = new byte[nonce.Length + Encoding.UTF8.GetByteCount(created) + Encoding.UTF8.GetByteCount(password)];
        var offset = 0;
        nonce.CopyTo(digestInput, offset); offset += nonce.Length;
        offset += Encoding.UTF8.GetBytes(created, 0, created.Length, digestInput, offset);
        Encoding.UTF8.GetBytes(password, 0, password.Length, digestInput, offset);
        var digest = Convert.ToBase64String(SHA1.HashData(digestInput));
        var nonceText = Convert.ToBase64String(nonce);
        var safeUser = System.Security.SecurityElement.Escape(username) ?? string.Empty;
        var soap = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"
                        xmlns:td="http://www.onvif.org/ver10/device/wsdl"
                        xmlns:trt="http://www.onvif.org/ver10/media/wsdl"
                        xmlns:tt="http://www.onvif.org/ver10/schema"
                        xmlns:wsse="{WsSecurity}"
                        xmlns:wsu="{WsUtility}">
              <s:Header>
                <wsse:Security s:mustUnderstand="1">
                  <wsse:UsernameToken>
                    <wsse:Username>{safeUser}</wsse:Username>
                    <wsse:Password Type="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest">{digest}</wsse:Password>
                    <wsse:Nonce EncodingType="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary">{nonceText}</wsse:Nonce>
                    <wsu:Created>{created}</wsu:Created>
                  </wsse:UsernameToken>
                </wsse:Security>
              </s:Header>
              <s:Body>{body}</s:Body>
            </s:Envelope>
            """;

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = new StringContent(soap, Encoding.UTF8, "application/soap+xml");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"The camera rejected the authenticated ONVIF request (HTTP {(int)response.StatusCode}). Verify the camera credentials and ONVIF permissions.");
        var document = ParseSafeXml(text);
        var fault = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "Fault");
        if (fault is not null)
            throw new InvalidOperationException("The camera returned an ONVIF SOAP fault. Verify credentials, ONVIF permissions, and device compatibility.");
        return text;
    }

    private static string? FindMediaServiceAddress(string xml)
    {
        var document = ParseSafeXml(xml);
        foreach (var service in document.Descendants().Where(element => element.Name.LocalName == "Service"))
        {
            var ns = service.Elements().FirstOrDefault(element => element.Name.LocalName == "Namespace")?.Value ?? string.Empty;
            if (!ns.Contains("/media/", StringComparison.OrdinalIgnoreCase)) continue;
            return service.Elements().FirstOrDefault(element => element.Name.LocalName == "XAddr")?.Value;
        }
        return null;
    }

    private static string? FindMediaCapabilityAddress(string xml)
    {
        var document = ParseSafeXml(xml);
        var media = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "Media");
        return media?.Descendants().FirstOrDefault(element => element.Name.LocalName == "XAddr")?.Value;
    }

    private static XDocument ParseSafeXml(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 256 * 1024,
            IgnoreComments = true
        });
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static bool IsSafeDeviceServiceUri(string value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out uri!) &&
            uri.Scheme is "http" or "https" &&
            string.IsNullOrEmpty(uri.UserInfo) &&
            IsPrivateHost(uri.Host))
        {
            return true;
        }
        uri = null!;
        return false;
    }

    private static string NormalizePrivateAddress(IPAddress address)
    {
        if (!IsPrivateHost(address.ToString()))
            throw new ArgumentException("The ONVIF endpoint is not a private address.");
        return address.ToString();
    }

    private static string BuildDisplayName(string scopes, string host)
    {
        var label = scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(scope => Uri.UnescapeDataString(scope))
            .FirstOrDefault(scope => scope.Contains("name/", StringComparison.OrdinalIgnoreCase));
        if (label is not null)
        {
            var name = label[(label.LastIndexOf("name/", StringComparison.OrdinalIgnoreCase) + 5)..];
            if (!string.IsNullOrWhiteSpace(name)) return name + " (" + host + ")";
        }
        return "ONVIF camera (" + host + ")";
    }
}
