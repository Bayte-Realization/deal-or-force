package dof.languages

import godot.annotation.Register
import godot.annotation.Script
import godot.api.RefCounted
import kotlin.math.max
import kotlin.math.min

/** A batch-shaped boundary, not one JVM transition per simulated province. */
@Script
class LanguageProbe : RefCounted() {
    @Register
    fun addChecked(left: Long, right: Long): Long = Math.addExact(left, right)

    @Register
    fun backendLabel(): String = "Kotlin/JVM"

    /** Pure sovereign debt-ratio and fiscal sustainability score (0..1000‰). */
    @Register
    fun evaluateSovereignHealthPermille(treasury: Double, publicDebt: Double, gdp: Double, stability: Double): Long {
        if (!treasury.isFinite() || !publicDebt.isFinite() || !gdp.isFinite() || !stability.isFinite()) return 0L
        val safeGdp = max(0.01, gdp)
        val debtRatio = max(0.0, publicDebt) / safeGdp
        val reserveShare = min(1.0, max(0.0, treasury) / safeGdp)
        val stab = min(1.0, max(0.0, stability))
        val raw = (1.0 - min(1.5, debtRatio) * 0.55 + reserveShare * 0.25) * (0.5 + 0.5 * stab)
        return (min(1.0, max(0.0, raw)) * 1000.0).toLong()
    }
}
