using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>A readable daily combat record. Battles spend organisation and cost people.</summary>
public sealed class LandBattle
{
    [System.Text.Json.Serialization.JsonConverter(typeof(LandBattleRoundConverter))]
    public sealed class Round
    {
        public int Day;
        public float AttackerOrganisation;
        public float DefenderOrganisation;
        public float AttackerDamage;
        public float DefenderDamage;
        public float AttackerLosses;
        public float DefenderLosses;
        public float AttackerPower;
        public float DefenderPower;
        public float AttackerPlanning;
        // Actual weighted loss in this round, after template organisation resistance.
        // Zero in older saves; their damage telemetry remains a conservative fallback.
        public float AttackerOrganisationSpent;
        public float DefenderOrganisationSpent;
        public bool HasOrganisationTelemetry;
        public string Note = "";
    }

    public int Id;
    public int WarId;
    public int ProvinceId;
    public int AttackerId;
    public int DefenderId;
    public int StartedDay;
    public bool Ended;
    public int WinnerId = -1;
    public string Outcome = "Fighting";
    public float AttackerLosses;
    public float DefenderLosses;
    public float AttackerWidth;
    public float DefenderWidth;
    public int AttackerReinforcements;
    public int DefenderReinforcements;
    public float RiverCrossingStrength;
    /// <summary>What each side's long-range forces - guns, aircraft, drones - contributed
    /// to this battle, in raw reach points. Written each round; shown in the report.</summary>
    public float AttackerSupport;
    public float DefenderSupport;
    public readonly List<int> Attackers = new();
    public readonly List<int> Defenders = new();
    public readonly List<int> WithdrawnAttackers = new();
    public readonly List<int> WithdrawnDefenders = new();
    public readonly List<Round> Rounds = new();
}
