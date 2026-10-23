extends RefCounted


func run(ctx: Variant) -> void:
	await ctx.wait(0.5)
	await ctx.capture("07-card-small", 820)
	ctx.log("probe7 done")
