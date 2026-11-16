using System;
using System.Globalization;
using System.Linq;
using System.Text;
using DealOrForce.Simulation;

/// <summary>
/// A capital stands in its capital. CapitalCatalog had the middle of the country for some -
/// Pakistan was governed from central Punjab, Venezuela from its savannah, Myanmar from Mandalay
/// - and the starting capital region was the one nearest that point. A capital is a city of the
/// map now (Nation.CapitalCityId): every country that names one must be governed from where
/// that city is, under that city's name.
/// </summary>
static class CapitalPointTests
{
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL capitals: " + name);
        Console.WriteLine("PASS capitals: " + name);
    }

    static double Kilometres(double latA, double lonA, double latB, double lonB)
    {
        double Rad(double degrees) => degrees * Math.PI / 180.0;
        var h = Math.Pow(Math.Sin(Rad(latB - latA) / 2), 2) +
                Math.Cos(Rad(latA)) * Math.Cos(Rad(latB)) * Math.Pow(Math.Sin(Rad(lonB - lonA) / 2), 2);
        return 6371.0 * 2 * Math.Asin(Math.Sqrt(h));
    }

    public static void Run(Campaign campaign)
    {
        CityCatalog.Reset();
        var compared = 0;
        var far = new StringBuilder();
        foreach (var nation in campaign.Nations)
        {
            // A city does not say whose capital it is - the country does, by the city's key - and
            // a country with no ground has no capital to place.
            if (nation.CapitalProvinceId <= 0 || CapitalCore.City(nation) is not { } city) continue;
            compared++;
            var distance = Kilometres(nation.CapitalLatitude, nation.CapitalLongitude, city.Latitude, city.Longitude);
            if (distance > 30) far.Append($" {nation.Iso} {nation.CapitalName} {distance:0} km;");
            if (nation.CapitalName != city.Name) far.Append($" {nation.Iso} is called {nation.CapitalName}, its city {city.Name};");
        }
        Check(compared > 200, $"{compared} countries are governed from a city of the map");
        Check(far.Length == 0, "every one stands within 30 km of it and goes by its name" + (far.Length > 0 ? ":" + far : ""));

        // And the region it is governed from follows: the capital province is the country's
        // own province nearest the point.
        var topology = campaign.ProvinceMap;
        foreach (var (tag, lat, lon) in new[] { ("PAK", 33.70, 73.16), ("VEN", 10.50, -66.92), ("MMR", 19.77, 96.12) })
        {
            var nation = campaign.NationByTag(tag);
            var centre = topology.CenterOf(nation.CapitalProvinceId);
            var distance = Kilometres(centre.Latitude, centre.Longitude, lat, lon);
            Check(nation.CapitalProvinceId > 0 && distance < 80,
                $"{tag} is governed from a province {distance:0} km from {nation.CapitalName}");
        }
    }
}
