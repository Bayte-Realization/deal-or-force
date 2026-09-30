using System;
using System.Globalization;
using System.Linq;
using System.Text;
using DealOrForce.Simulation;

/// <summary>
/// A capital stands in its capital. CapitalCatalog had the middle of the country for some -
/// Pakistan was governed from central Punjab, Venezuela from its savannah, Myanmar from Mandalay
/// - and the starting capital region is the one nearest that point. Every capital that the map's
/// cities also know by name must be where that city is.
/// </summary>
static class CapitalPointTests
{
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL capitals: " + name);
        Console.WriteLine("PASS capitals: " + name);
    }

    static string Plain(string text)
    {
        var builder = new StringBuilder();
        foreach (var letter in text.Normalize(NormalizationForm.FormD))
            if (char.IsLetter(letter) && CharUnicodeInfo.GetUnicodeCategory(letter) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToLowerInvariant(letter));
        return builder.ToString();
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
        var cities = CityCatalog.All;
        var compared = 0;
        var far = new StringBuilder();
        foreach (var nation in campaign.Nations)
        {
            // Côte d'Ivoire is governed from Abidjan and names Yamoussoukro its capital; the
            // catalogue's point is the government's, which is the one a war is about.
            if (nation.Iso == "CIV" || string.IsNullOrWhiteSpace(nation.CapitalName)) continue;
            var city = cities.FirstOrDefault(candidate => candidate.Country == nation.Iso &&
                                                          candidate.Capital is "national" or "seat" &&
                                                          Plain(candidate.Name) == Plain(nation.CapitalName));
            if (city is null) continue;
            compared++;
            var distance = Kilometres(nation.CapitalLatitude, nation.CapitalLongitude, city.Latitude, city.Longitude);
            if (distance > 30) far.Append($" {nation.Iso} {nation.CapitalName} {distance:0} km;");
        }
        Check(compared > 150, $"{compared} capitals have a city of the same name to compare with");
        Check(far.Length == 0, "every one stands within 30 km of it" + (far.Length > 0 ? ":" + far : ""));

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
