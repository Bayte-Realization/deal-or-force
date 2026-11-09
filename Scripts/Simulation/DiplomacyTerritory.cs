using System.Collections.Generic;

namespace DealOrForce.Simulation;

public sealed partial class Campaign
{
    private int _diplomaticLandRevision = -1;
    private ProvinceTopology _diplomaticLandMap;
    private readonly HashSet<int> _diplomaticLandNations = new();

    public bool HasOwnedLand(Nation nation)
    {
        if (nation is null) return false;
        if (ProvinceMap is null) return nation.Population > 0;
        // The set answers "does this country hold a province", which only ProvinceToNation
        // decides: MapRevision fires on every province a battle takes while the owners these
        // lists are built from stay exactly as they were.
        if (_diplomaticLandRevision != OwnershipRevision || _diplomaticLandMap != ProvinceMap)
        {
            _diplomaticLandRevision = OwnershipRevision;
            _diplomaticLandMap = ProvinceMap;
            _diplomaticLandNations.Clear();
            for (var p = 1; p <= ProvinceMap.ProvinceCount; p++)
                if (ProvinceMap.ProvinceToNation[p] > 0) _diplomaticLandNations.Add(ProvinceMap.ProvinceToNation[p]);
        }
        return _diplomaticLandNations.Contains(nation.MapIndex);
    }
}
