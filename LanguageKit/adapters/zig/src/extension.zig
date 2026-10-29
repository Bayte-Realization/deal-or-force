const godot = @import("godot");
const Kernel = @import("DofZigKernel.zig");
pub fn register(r: *godot.extension.Registry) void { r.addModule(Kernel); }
pub fn unregister(r: *godot.extension.Registry) void { r.removeModule(Kernel); }
