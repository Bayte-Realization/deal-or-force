/* SPDX-License-Identifier: GPL-3.0-only */
/* Persistent Native Arena C ABI v1 for Deal or Force */
#ifndef DOF_KERNELS_H
#define DOF_KERNELS_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct DofProvinceGraph DofProvinceGraph;
typedef struct DofGraphScratch DofGraphScratch;
typedef struct DofRasterBake DofRasterBake;
typedef struct DofTradeArena DofTradeArena;

/* SIMD level returned by dof_kernels_simd_level():
 *   0 = scalar/unknown
 *   1 = x86 SSE2
 *   2 = x86 AVX2
 *   3 = ARM64 NEON
 *   4 = ARM64 NEON + DotProd
 */
uint32_t dof_kernels_abi_version(void);
uint32_t dof_kernels_simd_level(void);
uint32_t dof_kernels_capabilities(void);

DofProvinceGraph* dof_graph_create_v1(
    uint32_t province_count,
    const uint32_t* csr_offsets,
    const uint32_t* csr_edges,
    const float* edge_km,
    uint32_t edge_count,
    const uint32_t* land_offsets,
    const uint32_t* land_edges,
    uint32_t land_edge_count,
    const uint8_t* water,
    const uint8_t* coastal,
    const int32_t* harbour_basin,
    const float* prov_lat,
    const float* prov_lon);

void dof_graph_destroy_v1(DofProvinceGraph* graph);

DofGraphScratch* dof_graph_scratch_create_v1(uint32_t province_count);
void dof_graph_scratch_destroy_v1(DofGraphScratch* scratch);

int32_t dof_graph_distance_field_v1(
    const DofProvinceGraph* graph,
    DofGraphScratch* scratch,
    const int32_t* sources,
    uint32_t source_count,
    float bound_km,
    int32_t* out_provinces,
    float* out_distances,
    uint32_t out_capacity,
    uint32_t* out_count);

int32_t dof_graph_solve_supply_v1(
    const DofProvinceGraph* graph,
    DofGraphScratch* scratch,
    const uint16_t* controllers,
    uint32_t controller_count,
    const uint16_t* province_to_nation,
    const uint64_t* passable_bits,
    uint32_t passable_word_count,
    uint16_t own_map_index,
    const uint16_t* province_to_state,
    const int32_t* roots,
    uint32_t root_count,
    const int32_t* territory,
    uint32_t territory_count,
    const uint64_t* barracks_state_bits,
    uint32_t barracks_word_count,
    const uint16_t* land_state_ids,
    const int32_t* land_state_counts,
    uint32_t land_state_len,
    const uint16_t* held_state_ids,
    const int32_t* held_state_counts,
    uint32_t held_state_len,
    uint8_t insurgent,
    float forward_depot_held_share,
    const int32_t* army_provinces,
    uint32_t army_count,
    uint8_t* out_army_flags,
    int32_t* out_army_distance,
    int32_t* out_army_source_state);

int32_t dof_graph_owner_components_v1(
    const DofProvinceGraph* graph,
    DofGraphScratch* scratch,
    const uint16_t* owners,
    uint32_t owner_count,
    int32_t* out_labels,
    int32_t* out_members,
    int32_t* out_component_offsets,
    int32_t* out_component_count,
    int32_t* out_member_count);

DofRasterBake* dof_raster_create_v1(
    uint32_t width,
    uint32_t height,
    const uint16_t* authored_holders,
    const uint16_t* cell_to_province);

void dof_raster_destroy_v1(DofRasterBake* bake);

int32_t dof_raster_bake_frontier_v1(
    DofRasterBake* bake,
    const uint16_t* owners,
    uint32_t owners_len,
    const uint16_t* authored_owners,
    uint32_t authored_owners_len,
    float cap,
    float smoothing,
    uint32_t threads,
    uint8_t* out_bytes,
    uint32_t out_len);

/* Capability bit 6 (64): CPU render-prep overlay packing & political plate painter */
int32_t dof_raster_pack_overlays_v1(
    uint32_t province_count,
    const uint16_t* owners,
    uint32_t owners_len,
    const uint16_t* painted_owners,
    uint32_t painted_owners_len,
    const uint16_t* controllers,
    uint32_t controllers_len,
    const uint32_t* palette_rgba,
    uint32_t palette_len,
    uint8_t occupied_alpha,
    uint16_t unowned_palette_index,
    uint32_t fallback_rgba,
    uint8_t* out_control_rgba,
    uint32_t out_control_len,
    uint8_t* out_owner_rgba,
    uint32_t out_owner_len,
    uint8_t* out_owner_ids_rgba,
    uint32_t out_owner_ids_len,
    int32_t* out_owners_moved);

int32_t dof_raster_paint_plate_v1(
    uint32_t width,
    uint32_t height,
    const uint16_t* source_holders,
    const uint16_t* source_provinces,
    const uint8_t* source_geography,
    const uint16_t* owners,
    uint32_t owners_len,
    const uint16_t* authored_owners,
    uint32_t authored_owners_len,
    const uint32_t* palette_rgba,
    uint32_t palette_len,
    float country_shade,
    float region_shade,
    float inland_shade,
    uint32_t threads,
    uint8_t* out_pixels_rgba,
    uint32_t out_pixels_len);

DofTradeArena* dof_trade_create_v1(uint32_t max_slots);
void dof_trade_destroy_v1(DofTradeArena* arena);

int32_t dof_trade_update_distances_v1(
    DofTradeArena* arena,
    const int32_t* nation_ids,
    const float* lats,
    const float* lons,
    uint32_t nation_count,
    const int32_t* dirty_indices,
    uint32_t dirty_count,
    uint32_t full_rebuild,
    uint32_t stride,
    float* out_dist_table,
    uint32_t out_dist_len);

int32_t dof_trade_compute_affinity_v1(
    DofTradeArena* arena,
    const int32_t* buyer_ids,
    const uint16_t* buyer_map_idx,
    const float* buyer_reach,
    uint32_t buyer_count,
    const int32_t* seller_ids,
    const uint16_t* seller_map_idx,
    const float* seller_reach,
    uint32_t seller_count,
    const float* dist_table,
    uint32_t dist_stride,
    uint32_t dist_nation_count,
    const float* pair_opinion,
    const uint8_t* pair_stance,
    const uint8_t* pair_flags,
    uint32_t pair_stride,
    float half_reach_km,
    float customs_inside,
    float customs_outside,
    float* out_affinity,
    uint32_t out_stride);

/* --- Batched Diplomacy / Opinion Scoring Kernels --- */

int32_t dof_dip_pair_score_batch_v1(
    uint32_t pair_count,
    const float* left_econ,
    const float* left_auth,
    const float* right_econ,
    const float* right_auth,
    const float* esteem_ab,
    const float* esteem_ba,
    const float* claim_left,
    const float* claim_right,
    const float* claim_bonus,
    const float* bloc_bias,
    const float* base_reason_sum,
    const float* witness_against,
    const float* witness_for,
    const float* identity_affinity,
    const float* strength_ratio,
    const float* fear_reach,
    const uint8_t* pair_flags,
    float ideology_weight,
    float social_weight,
    float geopolitics_ramp,
    float third_party_floor,
    float third_party_ceiling,
    float* out_politics,
    float* out_social,
    float* out_claims,
    float* out_geopolitics,
    float* out_opinion,
    float* out_fear);

int32_t dof_dip_reason_decay_batch_v1(
    uint32_t reason_count,
    const float* base_opinion,
    const int32_t* stacks,
    const float* scale,
    const int32_t* granted_day,
    const int32_t* expires_day,
    const int32_t* decay_days,
    const uint8_t* flags,
    int32_t today,
    float* out_weight,
    uint8_t* out_prune);

int32_t dof_dip_partner_eval_batch_v1(
    uint32_t partner_count,
    const float* opinion,
    const uint8_t* stance,
    const float* bloc_bias,
    const float* trade_dep,
    const uint8_t* flags,
    float reach,
    float base_trade_th,
    float base_pact_th,
    float base_alliance_th,
    int32_t era_mode,
    float friendship_th,
    float rivalry_th,
    float embargo_reconcile_op,
    uint8_t can_mission_budget,
    uint8_t* out_action_mask,
    float* out_trade_th,
    int32_t* out_best_friend_idx,
    int32_t* out_worst_rival_idx);

/* --- Batched Market Clearing Kernels --- */

int32_t dof_market_settle_prices_v1(
    uint32_t good_count,
    uint32_t nation_count,
    uint32_t nation_stride,
    const float* base_price,
    const uint8_t* traded_world_price,
    const float* demand,
    const float* production,
    const float* imports,
    const float* relieved,
    uint8_t adjust_price,
    float import_markup_def,
    float relief_def,
    float price_adjust_speed,
    float* satisfaction,
    float* price,
    float* out_world_scarcity);

int32_t dof_market_clear_nations_v1(
    uint32_t nation_count,
    uint32_t good_count,
    uint32_t good_stride,
    const float* base_price,
    const float* essential_weight,
    const float* tariff_rate,
    const float* production,
    const float* demand,
    const float* imports,
    const float* exports,
    const float* relieved,
    const float* satisfaction,
    const float* price,
    float market_trade_share,
    float gdp_price_share,
    float gdp_price_band,
    float* out_gross_output,
    float* out_real_output,
    float* out_shortage_index,
    float* out_market_access,
    float* out_weekly_trade_balance,
    float* out_weekly_tariff_income);

/* --- Batched Spherical Distance / Range Prefilter Kernels --- */

int32_t dof_distance_haversine_batch_v1(
    float origin_lat,
    float origin_lon,
    const float* target_lats,
    const float* target_lons,
    uint32_t target_count,
    float* out_km);

int32_t dof_distance_within_range_batch_v1(
    float origin_lat,
    float origin_lon,
    const float* target_lats,
    const float* target_lons,
    uint32_t target_count,
    float reach_km,
    int32_t* out_indices,
    float* out_km,
    uint32_t out_capacity,
    uint32_t* out_count);

int32_t dof_distance_pairwise_matrix_v1(
    const float* lats,
    const float* lons,
    uint32_t count,
    uint32_t stride,
    float* out_km);

/* --- Batched Economy & Demography Kernels (Cap bit 7 = 128) --- */

int32_t dof_demo_step_cohorts_batch_v1(
    uint32_t count,
    const float* population,
    const int64_t* review_births,
    const int64_t* review_deaths,
    const float* young_share,
    const float* old_share,
    float maturation_annual,
    float ageing_annual,
    float mortality_old_weight,
    float interval,
    float* out_young,
    float* out_old,
    float* out_working);

int32_t dof_demo_aggregate_nations_v1(
    uint32_t nation_count,
    const uint32_t* state_offsets,
    const int64_t* state_pop,
    const float* state_young,
    const float* state_old,
    const int64_t* state_births,
    const int64_t* state_deaths,
    const int64_t* state_migration,
    uint32_t state_count,
    float default_young,
    float default_old,
    float default_working,
    float ref_old,
    float* out_young,
    float* out_old,
    float* out_working,
    float* out_old_excess,
    int64_t* out_births,
    int64_t* out_deaths,
    int64_t* out_migration);

int32_t dof_econ_household_groups_v1(
    uint32_t group_count,
    const int64_t* sizes,
    const uint8_t* is_upper,
    const float* anchor_wealth,
    float* employment,
    float* wealth,
    float* sol,
    float* radicalism,
    float working_share,
    uint8_t need_employment,
    float building_jobs,
    float non_building_jobs_in,
    float annual_conditions,
    float interval,
    float damage,
    float food,
    float energy,
    float medicine,
    float scarcity_tolerance,
    float employment_rate,
    float wealth_rate,
    float nation_sol,
    float state_rad,
    float* out_non_building_jobs,
    float* out_emp_target,
    float* out_basic_needs,
    float* out_stress,
    double* out_living_sum);

int32_t dof_econ_vital_rates_batch_v1(
    uint32_t count,
    const int64_t* pop,
    const float* war_damage,
    const int32_t* hospitals,
    const float* literacy,
    const float* urbanization,
    const float* sol,
    const float* radicalism,
    const float* young_share,
    const float* old_share,
    const float* welfare_spending,
    const float* inflation,
    const float* war_support,
    const float* nation_stability,
    const float* state_stability,
    const float* capital_lat,
    const float* food,
    const float* energy,
    const float* medicine,
    const uint8_t* flags,
    float scarcity_tolerance,
    float birth_base,
    float low_lit_birth,
    float death_base,
    float shortage_death,
    float ref_old,
    float mort_old_weight,
    float interval,
    float* out_birth,
    float* out_death,
    float* out_old_burden,
    int64_t* out_review_births,
    int64_t* out_review_deaths);

#ifdef __cplusplus
}
#endif

#endif /* DOF_KERNELS_H */
