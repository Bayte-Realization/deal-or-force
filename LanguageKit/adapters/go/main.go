package main

import (
	"graphics.gd/classdb"
	"graphics.gd/classdb/Node"
	"graphics.gd/startup"
)

// This registers one opt-in class, not a second campaign simulation.
type DofGoKernel struct {
	Node.Extension[DofGoKernel] `gd:"DofGoKernel"`
}

func (k *DofGoKernel) WeightedSum(a, b, c int64) int64 { return a + 2*b + 3*c }

func main() {
	classdb.Register[DofGoKernel]()
	startup.Scene()
}
