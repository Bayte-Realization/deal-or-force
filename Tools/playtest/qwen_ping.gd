extends RefCounted

# The smallest possible script: does the runtime channel log at all?


func run(ctx: Variant) -> void:
	ctx.log("ping: script body entered")
	var root: Node = ctx.get_scene_root()
	ctx.log("ping: root resolved", {"root": str(root.name) if root != null else "null"})
	await ctx.capture("ping", 640)
	ctx.log("ping: done")
