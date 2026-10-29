extends RefCounted
## What the front of the game is made of, and what it looks like.
##
## First probe of a visual pass: it lists every button label on whatever screen is up, so the
## next script can press one by name instead of guessing, and it takes one picture. Run through
## Tools/game-capture.ps1, which is the only way to see a screen a player reaches by clicking.

var _labels: Array[String] = []


func _collect(node: Node) -> void:
	if node is Button or node is OptionButton or node is CheckBox:
		var text := ""
		if node is Button:
			text = String((node as Button).text)
		elif node is OptionButton:
			text = String((node as OptionButton).text)
		if not node.visible:
			text = ""
		if text.strip_edges().length() > 0:
			_labels.append(text.strip_edges())
	for child in node.get_children():
		_collect(child)


func run(ctx: Variant) -> void:
	await ctx.wait(3.0)
	var root: Node = ctx.get_scene_root()
	if root == null:
		ctx.error("no scene root")
		return
	_collect(root)
	ctx.log("BUTTONS %d: %s" % [_labels.size(), " | ".join(PackedStringArray(_labels))])
	await ctx.capture("01-front", 1600)
	ctx.log("captured")
