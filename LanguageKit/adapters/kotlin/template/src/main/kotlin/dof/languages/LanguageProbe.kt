package dof.languages

import godot.annotation.Register
import godot.annotation.Script
import godot.api.RefCounted

/** A batch-shaped boundary, not one JVM transition per simulated province. */
@Script
class LanguageProbe : RefCounted() {
    @Register
    fun addChecked(left: Long, right: Long): Long = Math.addExact(left, right)

    @Register
    fun backendLabel(): String = "Kotlin/JVM"
}
