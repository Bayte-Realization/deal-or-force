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

uint32_t dof_kernels_abi_version(void);
uint32_t dof_kernels_simd_level(void);

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

#ifdef __cplusplus
}
#endif

#endif /* DOF_KERNELS_H */
