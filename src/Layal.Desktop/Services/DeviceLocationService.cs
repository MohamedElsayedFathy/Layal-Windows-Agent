using System;
using System.Threading.Tasks;
using Windows.Devices.Geolocation;

namespace Layal.Services;

public sealed record DeviceCoordinates(double Latitude, double Longitude);

public sealed class DeviceLocationAccessException : Exception
{
    public GeolocationAccessStatus Status { get; }

    public DeviceLocationAccessException(
        GeolocationAccessStatus status,
        Exception? innerException = null)
        : base($"Windows location access is not available ({status}).", innerException)
    {
        Status = status;
    }
}

/// <summary>
/// Reads one device position through the Windows location service. No IP-based location
/// estimate is used. Call from the foreground UI thread so Windows can request permission.
/// </summary>
public sealed class DeviceLocationService
{
    public async Task<DeviceCoordinates> GetCurrentLocationAsync()
    {
        var accessStatus = await Geolocator.RequestAccessAsync();
        if (accessStatus != GeolocationAccessStatus.Allowed)
            throw new DeviceLocationAccessException(accessStatus);

        try
        {
            var geolocator = new Geolocator
            {
                // Weather only needs an approximate position; do not request meter-level precision.
                DesiredAccuracyInMeters = 1000
            };
            var position = await geolocator.GetGeopositionAsync(
                TimeSpan.FromMinutes(5),
                TimeSpan.FromSeconds(12));
            var point = position.Coordinate.Point.Position;
            if (!double.IsFinite(point.Latitude) || point.Latitude < -90 || point.Latitude > 90 ||
                !double.IsFinite(point.Longitude) || point.Longitude < -180 || point.Longitude > 180)
                throw new InvalidOperationException("Windows returned invalid geographic coordinates.");
            return new DeviceCoordinates(point.Latitude, point.Longitude);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new DeviceLocationAccessException(GeolocationAccessStatus.Denied, exception);
        }
    }
}