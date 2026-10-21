using System;
using System.Linq;
using DealOrForce.Simulation;

static class ContentIdentityAudit
{
    public static void Run()
    {
        var movements = MovementCatalog.All.Where(m => !m.Template).ToArray();
        var factions = Factions.All;
        var formables = FormableNations.All.ToArray();
        var c = Campaign.CreateBootstrap(20261018);
        Console.WriteLine($"CATALOG_COUNTS countries={c.Nations.Count} movements={movements.Length} factions={factions.Count} formables={formables.Length}");
        foreach (var group in movements.GroupBy(m => m.Tag,StringComparer.OrdinalIgnoreCase).Where(g => g.Count()>1))
            Console.WriteLine("DUPLICATE_MOVEMENT_TAG " + group.Key + ": " + string.Join(",",group.Select(m=>m.Id)));
        foreach (var group in movements.GroupBy(m => Normalize(m.Name)).Where(g => g.Count()>1))
            Console.WriteLine("SAME_MOVEMENT_NAME " + group.Key + ": " + string.Join(",",group.Select(m=>m.Id+"@"+m.Headquarters)));
        foreach (var group in formables.GroupBy(f => Normalize(f.Name)).Where(g => g.Count()>1))
            Console.WriteLine("SAME_FORMABLE_NAME " + group.Key + ": " + string.Join(",",group.Select(f=>f.Id)));
        foreach (var group in c.Nations.GroupBy(n=>Normalize(n.Name)).Where(g=>g.Count()>1))
            Console.WriteLine("SAME_COUNTRY_NAME " + group.Key + ": " + string.Join(",",group.Select(n=>n.Tag)));
        foreach (var m in movements)
        {
            var faction = factions.FirstOrDefault(f=> Normalize(f.Name)==Normalize(m.Name) ||
                Normalize(f.Name)==Normalize(m.StateName) && m.StateName.Length>0);
            if (faction is not null)
                Console.WriteLine("MOVEMENT_FACTION_IDENTITY " + m.Id + " -> " + faction.Id + " link=" + m.TerritorialFaction);
            var country = c.Nations.FirstOrDefault(n => Normalize(n.Name)==Normalize(m.Name) ||
                m.StateName.Length>0 && Normalize(n.Name)==Normalize(m.StateName));
            if (country is not null)
                Console.WriteLine("MOVEMENT_COUNTRY_CANDIDATE " + m.Id + " -> " + country.Tag + " state_name=" + m.StateName);
        }
        foreach (var f in formables)
        {
            var same = formables.Where(other=>StringComparer.Ordinal.Compare(other.Id,f.Id)>0 &&
                f.RequiredTags.OrderBy(x=>x).SequenceEqual(other.RequiredTags.OrderBy(x=>x)) &&
                f.RequiredRegions.OrderBy(x=>x).SequenceEqual(other.RequiredRegions.OrderBy(x=>x)) &&
                f.RequiredProvinces.OrderBy(x=>x).SequenceEqual(other.RequiredProvinces.OrderBy(x=>x))).ToArray();
            foreach(var other in same)
                Console.WriteLine("SAME_FORMABLE_TERRITORY " + f.Id + " / " + other.Id +
                    " names=" + f.Name + " / " + other.Name + " restores=" + f.Restores + " / " + other.Restores);
        }
    }
    static string Normalize(string value) => new string((value??"").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}

