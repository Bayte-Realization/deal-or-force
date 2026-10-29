const Self = @This();
const std = @import("std");
const godot = @import("godot");
const RefCounted = godot.class.RefCounted;
const Object = godot.class.Object;

allocator: std.mem.Allocator,
base: *RefCounted,

pub fn register(r: *godot.extension.Registry) void {
    const class = r.createClass(Self, r.allocator, .auto);
    class.addMethod("weighted_sum", .auto);
}

pub fn unregister(r: *godot.extension.Registry) void {
    r.removeClass(Self);
}

pub fn create(allocator: *std.mem.Allocator) !*Self {
    const self = try allocator.create(Self);
    self.* = .{ .allocator = allocator.*, .base = .init() };
    self.base.setInstance(Self, self);
    return self;
}

pub fn recreate(allocator: *std.mem.Allocator, obj: *Object) *Self {
    const self = allocator.create(Self) catch @panic("OOM");
    self.* = .{ .allocator = allocator.*, .base = @ptrCast(obj) };
    self.base.setInstance(Self, self);
    return self;
}

pub fn destroy(self: *Self, allocator: *std.mem.Allocator) void {
    self.base.destroy();
    allocator.destroy(self);
}

// A bounded scalar API makes signed/zero/large-number ABI checks easy to audit.
pub fn weightedSum(_: *Self, a: i64, b: i64, c: i64) i64 {
    return a + 2 * b + 3 * c;
}
