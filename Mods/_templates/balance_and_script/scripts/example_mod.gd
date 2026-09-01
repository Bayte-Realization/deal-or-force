extends RefCounted

func on_load(api) -> void:
	api.log("Example GDScript mod loaded")

func on_campaign_created(api) -> void:
	var korea: Dictionary = api.get_nation("KOR")
	api.log("Campaign ready; Korea population = %s" % korea.get("population", 0))

func on_day_advanced(api) -> void:
	if api.get_day() % 30 == 0:
		var korea: Dictionary = api.get_nation("KOR")
		api.set_nation_number("KOR", "treasury", korea.get("treasury", 0.0) + 1000000.0)
