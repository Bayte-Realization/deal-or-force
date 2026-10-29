// SPDX-License-Identifier: GPL-3.0-or-later
	package dof.tools.content

	/**
	 * Type-safe Kotlin Content DSL & Validator for Deal or Force events, decisions, and modifiers.
	 * Generates clean, validated JSON content and checks localization key parity without touching BTA.
	 */
	data class EventOptionSpec(
		val title: String,
		val consequence: String,
		val aiChance: Double = 1.0,
		val effects: Map<String, Double> = emptyMap()
	)

	data class CampaignEventSpec(
		val id: String,
		val title: String,
		val description: String,
		val triggerTag: String = "",
		val options: List<EventOptionSpec> = emptyList()
	)

	class EventBuilder(private val id: String) {
		var title: String = ""
		var description: String = ""
		var triggerTag: String = ""
		private val options = mutableListOf<EventOptionSpec>()

		fun option(title: String, consequence: String, aiChance: Double = 1.0, effects: MutableMap<String, Double>.() -> Unit = {}) {
			val map = linkedMapOf<String, Double>()
			map.effects()
			options.add(EventOptionSpec(title, consequence, aiChance, map))
		}

		fun build(): CampaignEventSpec {
			require(id.isNotBlank()) { "Event id must not be blank" }
			require(title.isNotBlank()) { "Event '$id' must have a title" }
			require(options.isNotEmpty()) { "Event '$id' must declare at least one option" }
			return CampaignEventSpec(id, title, description, triggerTag, options.toList())
		}
	}

	fun campaignEvent(id: String, block: EventBuilder.() -> Unit): CampaignEventSpec =
		EventBuilder(id).apply(block).build()
