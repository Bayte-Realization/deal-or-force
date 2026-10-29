// Deal or Force language probe: Go (graphics.gd).
//
// The Go integration is a program, not a class library: main() takes the scene
// tree, makes something with the engine and reads it back. This probe creates a
// label, sets its text, reads the property back and refuses to continue if the
// round trip changed a letter - the same bar the Rust and Nim probes hold.
package main

import (
	"fmt"
	"os"

	"graphics.gd/classdb/GUI"
	"graphics.gd/classdb/Label"
	"graphics.gd/classdb/SceneTree"
	"graphics.gd/startup"
)

func main() {
	startup.LoadingScene()
	hello := Label.New()
	hello.SetHorizontalAlignment(GUI.HorizontalAlignmentCenter)
	hello.SetText("Deal or Force language probe: Go")
	if text := hello.Text(); text != "Deal or Force language probe: Go" {
		fmt.Fprintf(os.Stderr, "label text round-trip failed: %q", text)
		os.Exit(1)
	}
	SceneTree.Add(hello)
	startup.Scene()
}
