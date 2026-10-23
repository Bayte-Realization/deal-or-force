/* SPDX-License-Identifier: GPL-3.0-only. See ../../LICENSE. */
#ifndef DOF_OWNERSHIP_KERNEL_H
#define DOF_OWNERSHIP_KERNEL_H
#include <stdint.h>
#if defined(_WIN32)
#define DOF_GRAPH_CALL __cdecl
#else
#define DOF_GRAPH_CALL
#endif
#ifdef __cplusplus
extern "C" {
#endif

uint32_t DOF_GRAPH_CALL dof_graph_abi_version(void);

/* V includes sentinel vertex 0. See README.md for memory and order contracts.
 * Status: 0 success, 1 invalid input, 2 insufficient capacity, 3 caught panic.
 * On status 1/2 all outputs remain untouched. Discard all outputs on status 3.
 */
int32_t DOF_GRAPH_CALL dof_owner_components_v1(
    const int32_t *offsets, uint32_t offsets_count,
    const int32_t *neighbors, uint32_t neighbor_count,
    const uint16_t *owners, uint32_t vertex_count,
    const uint8_t *water, uint32_t water_count,
    int32_t *labels, uint32_t labels_capacity,
    int32_t *member_order, uint32_t member_capacity,
    int32_t *component_offsets, uint32_t component_offsets_capacity,
    int32_t *out_component_count, int32_t *out_member_count);

#ifdef __cplusplus
}
#endif
#endif
