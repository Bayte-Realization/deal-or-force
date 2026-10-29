--!strict
	-- Pure Luau formula reference module for Deal or Force scripted_modifiers.
	-- Operates on an immutable NationFormulaSnapshot (ctx) with zero side effects.

	export type NationFormulaSnapshot = {
		score: number,
		stability: number,
		war_support: number,
		treasury: number,
		gdp: number,
		inflation: number,
		public_debt: number,
		debt_ratio: number,
		shortage: number,
		manpower: number,
		political_points: number,
		young_share: number,
		old_share: number,
		working_share: number,
		at_war: number,
	}

	local AiFormulas = {}

	function AiFormulas.fiscal_emergency_score(ctx: NationFormulaSnapshot): number
		return if ctx.debt_ratio > 0.85 or ctx.inflation > 0.12
			then math.clamp(ctx.score + (ctx.debt_ratio * 25.0) + (ctx.inflation * 40.0), 0.0, 100.0)
			else math.clamp(ctx.score * (1.0 - ctx.stability * 0.25), 0.0, 100.0)
	end

	return AiFormulas
