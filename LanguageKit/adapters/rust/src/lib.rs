use godot::classes::RefCounted;
use godot::prelude::*;

/// Tiny opt-in kernel: a reproducible native boundary, not authoritative game state.
#[derive(GodotClass)]
#[class(init, base=RefCounted)]
struct DofRustKernel {
    base: Base<RefCounted>,
}

#[godot_api]
impl DofRustKernel {
    #[func]
    fn weighted_sum(&self, values: PackedInt32Array) -> i64 {
        values.as_slice().iter().enumerate().map(|(i, value)| (i as i64 + 1) * i64::from(*value)).sum()
    }

    #[func]
    fn language_name(&self) -> GString {
        "Rust".into()
    }
}

struct DofRustExtension;

#[gdextension]
unsafe impl ExtensionLibrary for DofRustExtension {}
