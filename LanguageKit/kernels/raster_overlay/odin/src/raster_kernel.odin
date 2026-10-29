// SPDX-License-Identifier: GPL-3.0-only
	// Dense province overlay packer in Odin (C ABI v1).
	package dof_raster_odin

	MAX_PROVINCES :: 65536
	PALETTE_SIZE  :: 65536
	BUFFER_BYTES  :: 256 * 256 * 4

	STATUS_OK       :: i32(0)
	STATUS_INVALID  :: i32(1)
	STATUS_CAPACITY :: i32(2)

	@(export)
	dof_raster_abi_version :: proc "c" () -> u32 {
		return 1
	}

	@(export)
	dof_pack_province_overlays_v1 :: proc "c" (
		province_count: u32,
		owners: [^]u16,
		painted_owners: [^]u16,
		controllers: [^]u16,
		palette_rgba: [^]u32,
		palette_len: u32,
		occupied_alpha: u8,
		unowned_palette_index: u16,
		fallback_rgba: u32,
		out_control_rgba: [^]u8,
		out_owner_rgba: [^]u8,
		out_owner_ids_rgba: [^]u8,
		out_owners_moved: ^i32,
	) -> i32 #no_bounds_check {
		if province_count == 0 || province_count >= MAX_PROVINCES {
			return STATUS_CAPACITY
		}
		if owners == nil || painted_owners == nil || controllers == nil || palette_rgba == nil ||
		   out_control_rgba == nil || out_owner_rgba == nil || out_owner_ids_rgba == nil || out_owners_moved == nil {
			return STATUS_INVALID
		}
		if palette_len == 0 || palette_len > PALETTE_SIZE {
			return STATUS_INVALID
		}

		for i in 0 ..< BUFFER_BYTES {
			out_control_rgba[i]   = 0
			out_owner_rgba[i]     = 0
			out_owner_ids_rgba[i] = 0
		}

		moved: i32 = 0
		count := int(province_count)
		pal_limit := u32(palette_len)

		for province := 1; province <= count; province += 1 {
			owner := owners[province]
			painted := painted_owners[province]
			write := province * 4

			if painted != owner {
				packed := fallback_rgba
				if u32(owner) < pal_limit {
					c := palette_rgba[owner]
					if c != 0 {
						packed = c
					}
				}
				out_owner_rgba[write]     = u8((packed >> 24) & 0xFF)
				out_owner_rgba[write + 1] = u8((packed >> 16) & 0xFF)
				out_owner_rgba[write + 2] = u8((packed >> 8) & 0xFF)
				out_owner_rgba[write + 3] = 255

				drawn_owner := owner == 0 ? unowned_palette_index : owner
				out_owner_ids_rgba[write]     = u8(drawn_owner & 0xFF)
				out_owner_ids_rgba[write + 1] = u8((drawn_owner >> 8) & 0xFF)
				out_owner_ids_rgba[write + 2] = 255
				out_owner_ids_rgba[write + 3] = 255
				moved = 1
			}

			controller := controllers[province]
			if controller != owner {
				packed := fallback_rgba
				if u32(controller) < pal_limit {
					c := palette_rgba[controller]
					if c != 0 {
						packed = c
					}
				}
				out_control_rgba[write]     = u8((packed >> 24) & 0xFF)
				out_control_rgba[write + 1] = u8((packed >> 16) & 0xFF)
				out_control_rgba[write + 2] = u8((packed >> 8) & 0xFF)
				out_control_rgba[write + 3] = occupied_alpha
			}
		}

		out_owners_moved^ = moved
		return STATUS_OK
	}
