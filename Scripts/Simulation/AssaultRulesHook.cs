using System;

namespace DealOrForce.Simulation;

/// <summary>
/// The optional pure-rule hook the game installs at startup.
///
/// The simulation layer stays engine- and language-neutral: it knows only that some caller
/// may provide a pure assessor. The game installs the F# implementation from a module
/// initializer; the headless tools, which compile this folder without the game boot file,
/// simply see no hook and use the managed path. A missing hook is not an error, and a hook
/// that throws is disabled once rather than allowed to break a campaign.
/// </summary>
internal delegate bool AssaultRulesAssessor(bool hasCampaign, bool hasArmy,
    bool hasAssaultOrder, bool planActive, bool directOrder, int readyDay, int elapsedDays,
    float organisation, float supply, float minimumOrganisation, float minimumSupply,
    out AssaultAssessment assessment);

internal static class AssaultRulesHook
{
    private static AssaultRulesAssessor _assessor;

    /// <summary>Whether a pure-rule implementation has been installed.</summary>
    internal static bool Installed => _assessor is not null;

    /// <summary>The name the export probe reads: the shipped pack resolves through the pure rule.</summary>
    internal static bool UsingFSharp => Installed;

    /// <summary>Install the pure assessor. The game calls this from its boot initializer.</summary>
    internal static void Install(AssaultRulesAssessor assessor) => _assessor = assessor;

    internal static bool TryAssess(bool hasCampaign, bool hasArmy, bool hasAssaultOrder,
        bool planActive, bool directOrder, int readyDay, int elapsedDays,
        float organisation, float supply, float minimumOrganisation, float minimumSupply,
        out AssaultAssessment assessment)
    {
        assessment = default;
        var assessor = _assessor;
        if (assessor is null) return false;
        try
        {
            return assessor(hasCampaign, hasArmy, hasAssaultOrder, planActive, directOrder,
                readyDay, elapsedDays, organisation, supply, minimumOrganisation, minimumSupply,
                out assessment);
        }
        catch (Exception error)
        {
            _assessor = null;
            assessment = default;
            DiagnosticLogs.Write("simulation",
                $"RULES_BACKEND fallback=managed rule=assault_readiness reason={error.GetType().Name}");
            return false;
        }
    }
}
