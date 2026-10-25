using System.Net;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class OnvifCameraDiscoveryServiceTests
{
    [TestMethod]
    public void TryParseProbeResponseAcceptsPrivateLocalOnvifEndpoint()
    {
        const string xml = """
            <e:Envelope xmlns:e="http://www.w3.org/2003/05/soap-envelope"
                        xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery">
              <e:Body><d:ProbeMatches><d:ProbeMatch>
                <d:XAddrs>http://192.168.1.44/onvif/device_service</d:XAddrs>
                <d:Scopes>onvif://www.onvif.org/name/Front-Door</d:Scopes>
              </d:ProbeMatch></d:ProbeMatches></e:Body>
            </e:Envelope>
            """;

        var result = OnvifCameraDiscoveryService.TryParseProbeResponse(xml, IPAddress.Parse("192.168.1.44"));

        Assert.IsNotNull(result);
        Assert.AreEqual("192.168.1.44", result.Host);
        Assert.AreEqual("http://192.168.1.44/onvif/device_service", result.DeviceServiceAddress);
        StringAssert.Contains(result.DisplayName, "Front-Door");
    }

    [TestMethod]
    public void TryParseProbeResponseRejectsPublicEndpointEvenWhenReplyComesFromLan()
    {
        const string xml = """
            <Envelope><ProbeMatch>
              <XAddrs>http://203.0.113.8/onvif/device_service</XAddrs>
            </ProbeMatch></Envelope>
            """;

        var result = OnvifCameraDiscoveryService.TryParseProbeResponse(xml, IPAddress.Parse("192.168.1.44"));

        Assert.IsNull(result);
    }

    [TestMethod]
    public void TryParseProbeResponseRejectsLoopbackDeviceEndpoints()
    {
        const string xml = """
            <Envelope><ProbeMatch>
              <XAddrs>http://127.0.0.1/onvif/device_service</XAddrs>
            </ProbeMatch></Envelope>
            """;

        Assert.IsNull(OnvifCameraDiscoveryService.TryParseProbeResponse(xml, IPAddress.Parse("192.168.1.44")));
    }

    [TestMethod]
    public void TryParseProbeResponseRejectsRemoteOrMalformedXml()
    {
        const string valid = """
            <Envelope><ProbeMatch><XAddrs>http://192.168.1.44/onvif/device_service</XAddrs></ProbeMatch></Envelope>
            """;

        Assert.IsNull(OnvifCameraDiscoveryService.TryParseProbeResponse(valid, IPAddress.Parse("8.8.8.8")));
        Assert.IsNull(OnvifCameraDiscoveryService.TryParseProbeResponse("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///c:/windows/win.ini'>]><x>&e;</x>", IPAddress.Parse("192.168.1.44")));
        Assert.IsNull(OnvifCameraDiscoveryService.TryParseProbeResponse("not xml", IPAddress.Parse("192.168.1.44")));
    }

    [TestMethod]
    public void BuildAuthenticatedStreamAddressEscapesCredentialsWithoutChangingHost()
    {
        var address = OnvifCameraDiscoveryService.BuildAuthenticatedStreamAddress(
            new Uri("rtsp://192.168.1.44:554/live/ch00_0"),
            "camera user",
            "p@ss:word");

        Assert.IsTrue(address.StartsWith("rtsp://", StringComparison.Ordinal));
        Assert.IsTrue(address.Contains("192.168.1.44:554/live/ch00_0", StringComparison.Ordinal));
        Assert.IsTrue(address.Contains("camera%20user", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(address.Contains("p%40ss%3Aword", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void BuildAuthenticatedStreamAddressRejectsNonLocalStreamsAndMissingCredentials()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            OnvifCameraDiscoveryService.BuildAuthenticatedStreamAddress(
                new Uri("rtsp://example.com/live"), "user", "password"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            OnvifCameraDiscoveryService.BuildAuthenticatedStreamAddress(
                new Uri("https://192.168.1.44/live"), "user", "password"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            OnvifCameraDiscoveryService.BuildAuthenticatedStreamAddress(
                new Uri("rtsp://192.168.1.44/live"), "", "password"));
    }

    [TestMethod]
    public void IsPrivateHostOnlyAllowsLoopbackAndLocalPrivateAddressRanges()
    {
        Assert.IsTrue(OnvifCameraDiscoveryService.IsPrivateHost("127.0.0.1"));
        Assert.IsTrue(OnvifCameraDiscoveryService.IsPrivateHost("10.0.0.8"));
        Assert.IsTrue(OnvifCameraDiscoveryService.IsPrivateHost("172.16.0.2"));
        Assert.IsTrue(OnvifCameraDiscoveryService.IsPrivateHost("192.168.0.2"));
        Assert.IsTrue(OnvifCameraDiscoveryService.IsPrivateHost("169.254.1.10"));
        Assert.IsFalse(OnvifCameraDiscoveryService.IsPrivateHost("8.8.8.8"));
        Assert.IsFalse(OnvifCameraDiscoveryService.IsPrivateHost("camera.example.com"));
    }
}
