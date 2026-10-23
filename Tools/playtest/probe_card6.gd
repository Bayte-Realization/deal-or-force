extends RefCounted


func run(ctx: Variant) -> void:
	await ctx.wait(0.5)
	await ctx.capture("06-card-small", 900)
	var hud: Node = ctx.get_scene_root()
	var names: Array = ["CountryCard", "CountryFactsScroll", "CountryActionsScroll", "DiplomaticActionList"]
	for wanted in names:
		var found: Node = hud.find_child(str(wanted), true, false)
		if found == null:
			ctx.log(str(wanted) + " absent")
			continue
		var control: Control = found as Control
		if control == null:
			ctx.log(str(wanted) + " not a control")
			continue
		ctx.log(str(wanted) + " pos=" + str(control.global_position) + " size=" + str(control.size))
	ctx.log("probe6 done")
