using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class WeatherServiceTests
{
    [TestMethod]
    public async Task GetCurrentWeatherAsyncUsesGeocodingAndReturnsCurrentConditions()
    {
        using var handler = new QueueResponseHandler(
            """{"results":[{"name":"Cairo","country":"Egypt","latitude":30.0444,"longitude":31.2357,"timezone":"Africa/Cairo"}]}""",
            """{"current":{"time":"2026-10-25T20:00","temperature_2m":27.4,"apparent_temperature":28.1,"relative_humidity_2m":46,"weather_code":1,"wind_speed_10m":13.6}}""");
        using var client = new HttpClient(handler);
        var service = new WeatherService(client);

        var report = await service.GetCurrentWeatherAsync("Cairo, Egypt", isArabic: false);

        Assert.AreEqual("Cairo, Egypt", report.LocationDisplay);
        Assert.AreEqual(27.4, report.TemperatureC, 0.001);
        Assert.AreEqual(28.1, report.ApparentTemperatureC, 0.001);
        Assert.AreEqual(46, report.RelativeHumidityPercent);
        Assert.AreEqual(13.6, report.WindSpeedKmh, 0.001);
        Assert.AreEqual(1, report.WeatherCode);
        Assert.AreEqual(2, handler.RequestedUris.Count);
        StringAssert.Contains(handler.RequestedUris[0].AbsoluteUri, "geocoding-api.open-meteo.com");
        StringAssert.Contains(handler.RequestedUris[0].AbsoluteUri, "name=Cairo%2C%20Egypt");
        StringAssert.Contains(handler.RequestedUris[1].AbsoluteUri, "api.open-meteo.com/v1/forecast");
        StringAssert.Contains(handler.RequestedUris[1].AbsoluteUri, "latitude=30.0444");
        StringAssert.Contains(handler.RequestedUris[1].AbsoluteUri, "longitude=31.2357");
        StringAssert.Contains(handler.RequestedUris[1].AbsoluteUri, "weather_code");
    }

    [TestMethod]
    public async Task GetCurrentWeatherAsyncRejectsUnknownCity()
    {
        using var handler = new QueueResponseHandler("""{"results":[]}""");
        using var client = new HttpClient(handler);
        var service = new WeatherService(client);

        await Assert.ThrowsExactlyAsync<WeatherLocationNotFoundException>(
            () => service.GetCurrentWeatherAsync("NoSuchCity", isArabic: true));

        Assert.AreEqual(1, handler.RequestedUris.Count);
    }

    [TestMethod]
    public async Task GetCurrentWeatherAsyncRejectsEmptyCityWithoutNetworkRequest()
    {
        using var handler = new QueueResponseHandler();
        using var client = new HttpClient(handler);
        var service = new WeatherService(client);

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => service.GetCurrentWeatherAsync("  ", isArabic: true));

        Assert.AreEqual(0, handler.RequestedUris.Count);
    }

    [TestMethod]
    public void WeatherCodeDescriptionsAreLocalized()
    {
        Assert.AreEqual("عاصفة رعدية", WeatherService.GetConditionText(95, isArabic: true));
        Assert.AreEqual("Thunderstorm", WeatherService.GetConditionText(95, isArabic: false));
        Assert.AreEqual("☀️", WeatherService.GetConditionIcon(0));
        Assert.AreEqual("⛈️", WeatherService.GetConditionIcon(95));
    }

    private sealed class QueueResponseHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responseBodies;

        public List<Uri> RequestedUris { get; } = new();

        public QueueResponseHandler(params string[] responseBodies)
        {
            _responseBodies = new Queue<string>(responseBodies);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri is not null)
                RequestedUris.Add(request.RequestUri);

            if (_responseBodies.Count == 0)
                throw new InvalidOperationException("No fake HTTP response remains for this request.");

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseBodies.Dequeue(), Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
