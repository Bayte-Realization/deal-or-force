using System;
using System.Linq;
using DealOrForce.Rules;
using DealOrForce.Simulation;

static class PolicyGoalRulesTests
{
    private static bool Oracle(string goal,float target,float living,float debt,float gdp,
        DebtCondition condition,bool war,float stability)=>goal switch
    {
        "living"=>living>=target,
        "debt_ratio"=>debt/MathF.Max(.01f,gdp)<=target && condition<DebtCondition.Default,
        "peace"=>!war,
        _=>stability>=target
    };
    private static void Check(bool pass,string label)
    {if(!pass)throw new Exception("Policy goal: "+label);Console.WriteLine("PASS policy goal: "+label);}

    public static void Run()
    {
        var values=new[]{float.NaN,float.NegativeInfinity,-float.MaxValue,-1f,-.01f,-0f,0f,
            float.Epsilon,.009f,.01f,.5f,1f,float.MaxValue,float.PositiveInfinity};
        var goals=new[]{null,"","stability","living","debt_ratio","peace","unknown","LIVING"};
        var cases=0;
        foreach(var goal in goals)
        foreach(var target in values)
        foreach(var observed in values)
        foreach(var gdp in values)
        foreach(var condition in Enum.GetValues<DebtCondition>())
        foreach(var war in new[]{false,true})
        {
            var expected=Oracle(goal,target,observed,observed,gdp,condition,war,observed);
            var kind=PoliticalPolicyRules.GoalKind(goal);
            var actual=PoliticalPolicyRules.GoalMet(kind,target,observed,observed,gdp,
                (int)condition,war,observed,(int)DebtCondition.Default);
            if(actual!=expected)throw new Exception($"Goal parity failed: {goal}/{target}/{observed}/{gdp}/{condition}/{war}");
            cases++;
        }
        Check(cases==219520,"219520 F#/C# comparisons include boundaries, NaN, infinities and all debt conditions");
        var c=Campaign.CreateBootstrap(555);var n=c.NationByTag("POL");var other=c.NationByTag("DEU");
        var conflict=new StrategicWar{Id=9000,AttackerId=n.Id,DefenderId=other.Id};
        c.Wars.Clear();c.Wars.Add(conflict);
        foreach(var goal in goals)
        foreach(var ended in new[]{false,true})
        {
            conflict.Ended=ended;n.StandardOfLiving=.7f;n.Gdp=2;n.PublicDebt=1;n.Stability=.6f;
            var agenda=new PoliticalAgenda{Goal=goal,GoalValue=.6f};
            var stamp=CampaignFingerprint.Of(c);
            Check(PoliticalPolicies.GoalMet(c,n,agenda)==Oracle(goal,.6f,.7f,1,2,n.DebtCondition,!ended,.6f),
                "production wrapper matches the oracle for "+(goal??"null")+" ended="+ended);
            Check(stamp==CampaignFingerprint.Of(c),"goal evaluation does not mutate campaign or RNG");
        }
        var irrelevant=new StrategicWar{Id=9001,AttackerId=other.Id,DefenderId=c.NationByTag("FRA").Id};
        c.Wars.Clear();c.Wars.Add(irrelevant);
        Check(PoliticalPolicies.GoalMet(c,n,new PoliticalAgenda{Goal="peace"}),"foreign wars do not invalidate a domestic peace goal");
        Check(PoliticalPolicyRules.GoalMet(2,.6f,.7f,1,2,0,false,.6f)==
              PoliticalPolicyRules.GoalMet(2,.6f,.7f,1,2,0,false,.6f,(int)DebtCondition.Default),
            "original F# method signature remains compatible");
    }
}
