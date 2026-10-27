// Content lint: a JVM tool that reads the game's data and reports what a human would only find
// by playing - see README.md. Kotlin because it is a tool, not a tick: it never runs in the game.
plugins {
    kotlin("jvm") version "2.3.20"
    application
}

repositories { mavenCentral() }

kotlin { jvmToolchain(21) }

application { mainClass.set("dealorforce.lint.MainKt") }
