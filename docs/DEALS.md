# Bilateral deals

Deals exchange existing assets and diplomatic commitments without an ultimatum. The simulation is engine-neutral: `Scripts/Simulation/Deals.cs` contains the rules; `Scripts/ui/DealComposerUI.cs` presents the same assessment. The public types remain under `Ultimatum` for compatibility with the original implementation. A deal does not use the permission to declare war.

## What can be exchanged

Each `Ultimatum.Side` contains `Regions` (region map indices), `Cash` (billions of dollars), `LiftEmbargo`, `NonAggression`, `TradeAgreement`, and `ReleaseRegions`. `Deal.From` is what the proposer gives; `Deal.To` is what the recipient gives. Cash is a one-time payment, not reparations or a weekly commitment. Ordinary cessions transfer permanent ownership and control through `StatePartition.Annex(..., asConquest: false)`; consensual cessions do not record conquest notoriety.

With `ReleaseRegions`, selected connected, populated whole regions become **one new independent country**, not the recipient's territory. Only one side may release a country per package, and the parent must retain land. A positive treasury balance after the cash exchange, public debt, arrears, GDP and reserves are divided by population share; a negative treasury remains with the parent, default is not erased and no free army is created. Voluntary release renounces the parent's rights to the selected original region families. Independence uses the existing population-based naming and capital pipeline and a ten-year parent/new-country truce. See the [territory and release guide (Russian)](TERRITORY_AND_RELEASE_RU.md) for limits and written-peace rules.

A trade agreement or non-aggression pact is bilateral and may appear on only one side of the table. Embargoes are directional: both countries may lift their own embargo in the same package. Terms already in force cannot be sold again. An empty package, including two cash payments that cancel with no other change, is invalid.

## Peaceful access, funding and consent

`CanOfferDeal` requires an attached territorial map, two distinct live campaign nations, owned land, enabled deals and any configured command gate. Opponents in an active war use the peace conference instead. A shared border, fleet, alliance, truce or existing pact is **not** required and does not block an unrelated peaceful exchange.

Human-controlled recipients are not automatically consented. This version has no pending human offer, counteroffer, negotiating deadline or player-to-player acceptance workflow. The proposer consents by submitting the package; the AI recipient accepts only if `AssessDeal` finds its final value non-negative. `ResolveDeal` also performs that check, so calling it directly cannot bypass consent.

Funding rules are checked again at command application, not just in the preview:

- Cash and both treasury balances must be finite; cash cannot be negative.
- The proposer must already fund its entire outgoing payment from its own treasury. Incoming cash cannot fund an otherwise unfunded gross offer.
- The recipient's outgoing payment must fit its existing treasury and `DealMaxTreasuryShare`.
- Resulting treasury balances must remain representable. Diplomatic scores never enter the treasury.
- Every selected region must have mapped provinces, all owned and controlled by its giver. Occupied or partly foreign regions invalidate the entire package.
- Each giver must retain actual owned ground. A stale region record with no provinces does not satisfy this rule.

Invalid or refused packages move no money, land or relation state. Both promised province sets and the summary are captured before any transfer: the first cession may reunify split regions and remove the second region's former ID. Saves retain the resulting ownership, treaties, truce and treasury; attaching the authored map on load does not undo the agreement.

## Diplomatic points are not money

`DealValuation` separates `Cash`, `Land`, `Treaties` and `Borders`; `Total` is their sum in **diplomatic utility points**. These are deterministic bargaining heuristics, not a currency exchange rate, a probability, guaranteed profit or a complete real-world appraisal.

- Cash uses the net transfer, weighted by the recipient's existing liquidity. Equal opposite payments create no cash value. The weight ranges from one to three; the actual payment always remains its face amount.
- A region's base value is `6 + 12 * clamp(population / 2,000,000, 0, 3)`. A primary-culture majority applies `DealKinRegionWeight`; regaining homeland/claimed ground applies `DealHomelandRegionWeight`/`DealClaimRegionWeight`. Capitals cannot be sold. Independence gives the recipient 25% of a cession's value without granting ownership. Mineral reserves are not separately priced.
- `Borders` penalises new disconnected components, detached regions and narrow salients after the complete swap. Existing islands and legitimate homeland restoration are not treated as arbitrary foreign enclaves. This is a strong preference, not a ban on every complex boundary.
- Receiving a pact adds 10 points and a trade agreement 8, discounted to 50–100% by the promiser's notoriety. Giving either costs 10 or 8 respectively. Lifting an embargo adds or costs 5 depending on which side makes the concession.
- If the pair's opinion is at or below `DealGrudgeOpinion`, the recipient's score loses `DealGrudgePenalty`. A sufficiently good package can overcome this penalty.

`FromValue` is the proposer's subjective balance. `ToValue` is the recipient's final balance after the grudge penalty. For a valid package, `ToValue >= 0` drives AI consent; the player may knowingly choose a costly deal. `CashToBalance` and `MaxCashRequest` calculate affordable adjustments from the same appraisal; utility points remain separate from currency. The composer previews actual region contours, gold for outgoing, green for incoming and hatching for independence. Previewing does not create a relation, change the random stream or mutate campaign state.

## Public API

| API | Purpose |
| --- | --- |
| `CanOfferDeal(c, from, to, out reason)` | Peaceful target and feature gate; independent of military access. |
| `ValidateDeal(c, from, to, deal, out reason)` | Whole-package legality and funding; no settlement mutation. |
| `AssessDeal(c, from, to, deal)` | One source for validity, refusal reason, values, AI consent and post-sign treasury previews. |
| `DealAcceptable(c, from, to, deal)` | Convenience projection of `AssessDeal(...).Acceptable`. |
| `DealValue(c, recipient, counterparty, receive, give)` | Raw side value without the pair's grudge penalty; use `AssessDeal` for a signing decision. |
| `ResolveDeal(c, from, to, deal)` | Revalidates and settles only an accepted package; returns `DealOutcome`. |
| `DealSummary(c, from, to, deal)` | Read-only summary with giver/recipient directions. |
| `ProposeDealCommand` | Authoritative queued/replay command; application returns its actual `Outcome`. |

`DealAssessment` exposes `Valid`, `Reason`, `Acceptable`, `FromValuation`, `ToValuation`, `GrudgePenalty`, `FromValue`, `ToValue`, `FromTreasuryAfter` and `ToTreasuryAfter`. The two valuation records expose `Cash`, `Land`, `Treaties`, `Borders` and `Total`. Invalid assessments return unchanged treasury previews and zero valuations. See the [Russian guide](DEALS.ru.md) for the cultural and geographic defines.

```csharp
var deal = new Ultimatum.Deal(
    new Ultimatum.Side { Cash = 5 },
    new Ultimatum.Side { TradeAgreement = true });
var assessment = Ultimatum.AssessDeal(campaign, player, partner, deal);
if (assessment.Acceptable)
    campaign.Commands.Enqueue(new ProposeDealCommand {
        IssuerId = player.Id, ToId = partner.Id,
        FromCash = 5, ToTradeAgreement = true
    });
// No mutation at Enqueue. The command is checked again at ApplyAll/the campaign boundary.
```

Command fields mirror both halves: `FromRegions`, `FromCash`, `FromLiftEmbargo`, `FromNonAggression`, `FromTradeAgreement`, `FromReleaseRegions`, and the corresponding `To...` fields, plus `IssuerId` and `ToId`. The existing `NetworkCommandCodec` carries this shape. Deterministic local/decoded-peer execution and snapshots are tested; that is not a claim that a live multi-client multiplayer session was exercised.

Commands do not have a deal-specific idempotency key. Submitting a new cash-only proposal twice means two proposals if both remain funded and acceptable. Loading a signed save restores balances rather than resubmitting commands. UI/network callers must not mistake a transport retry for a new proposal.

## Modding

Defaults live in `game/common/defines/58_deals.json`; later JSON, YAML or YML overlays use the existing structured-data loader.

| Define | Default | Rule |
| --- | --- | --- |
| `Features.deals` | `true` | Disable new deals without erasing existing signed treaties. |
| `DealMaxRegions` | `8` | Per-side region limit, clamped to 1–100. |
| `DealMaxTreasuryShare` | `1.0` | Recipient's maximum requested outgoing share, clamped to 0–1; never permits an overdraft. |
| `DealGrudgeOpinion` | `-25` | Pair opinion at or below which the penalty applies. |
| `DealGrudgePenalty` | `15` | Non-negative penalty in diplomatic points. |
| `DealSettlementTruceDays` | `180` | Protected peace after signing, clamped to 0–3,650 days; never shortens an existing truce. |

```yaml
# Your mod: common/defines/90_deals.yaml
DealMaxRegions: 4
DealMaxTreasuryShare: 0.6
DealGrudgePenalty: 20
DealSettlementTruceDays: 90
```

`FeatureCommands.ProposeDealCommand` can bind proposals to another existing custom feature; the preview obeys the same gate. Older saves missing the additive `deals` switch retain the default enabled mechanic. Ordinary game commands and existing C# engine-neutral APIs are available; this document does not promise a new Rust, Go, Luau, F# or GDScript-specific deal adapter.

The AI's `BuyPeace` uses an affordable slice of the weaker country's own treasury for a pact, not the seller's wealth as an offer cap. A broke counterparty can receive payment. The strong side does not buy peace from a weaker country through this rule. This is one AI use of the deal system, not a general autonomous land market.

## Verification

```powershell
dotnet run --project Tools/FormableTests -- --deals-only
dotnet run --project Tools/FormableTests -- --ultimatum-only
dotnet run --project Tools/FormableTests -- --deals-bench
```

The dedicated synthetic-map regressions cover peaceful access, consent, finite funding, read-only previews, simultaneous region swaps, directional embargoes, stale queued proposals, feature/mod gates, wire determinism, saved borders and Russian labels. They test the real simulation without requiring the Godot renderer.

The optional benchmark runs 1,000 read-only cash previews and 1,000 funded settlements on a synthetic 5,379-province map. Cash/treaty-only sides skip territorial scans; legality and treaty validation still run. Its timings are a focused diagnostic, not a game FPS measurement or a cross-machine performance guarantee.
