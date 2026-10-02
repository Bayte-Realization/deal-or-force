namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    public int UnionAiReviewDays { get; set; } = 90;
    public int UnionAiEarliestDay { get; set; } = 180;
    public float UnionAiMinimumOwnedShare { get; set; } = .35f;
    public float UnionAiTreasuryReserveShare { get; set; } = .12f;
    public int UnionConsentDays { get; set; } = 730;
    public int UnionInvitationCooldownDays { get; set; } = 365;
    public int UnionInvitationExpiryDays { get; set; } = 60;
    public float UnionMinimumOpinion { get; set; } = 50f;
    public float UnionAiAcceptance { get; set; } = 80f;
    public float UnionMinimumPartnerStability { get; set; } = .55f;
    public float UnionMaximumSubjectLiberty { get; set; } = .25f;
    public float UnionCultureWeight { get; set; } = 30f;
    public float UnionOpinionWeight { get; set; } = .45f;
    public float UnionAllianceWeight { get; set; } = 20f;
    public float UnionTradeAgreementWeight { get; set; } = 10f;
    public float UnionGovernmentWeight { get; set; } = 8f;
    public float UnionIdeologyWeight { get; set; } = 7f;
    public float UnionSubjectLoyaltyWeight { get; set; } = 20f;
    public float UnionStabilityWeight { get; set; } = 30f;
    public float UnionStabilityBaseline { get; set; } = .65f;
}
