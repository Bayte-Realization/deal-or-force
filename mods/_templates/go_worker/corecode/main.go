package main

import (
    "encoding/json"
    "fmt"
    "io"
    "os"
)

type request struct {
    Protocol int `json:"protocol"`
    Event string `json:"event"`
    Day int `json:"day"`
}

type featureCommand struct {
    Type string `json:"type"`
    Feature string `json:"feature"`
    Enabled bool `json:"enabled"`
}

type response struct {
    Protocol int `json:"protocol"`
    Logs []string `json:"logs"`
    Commands []featureCommand `json:"commands"`
}

// The request is an independent snapshot, not a shared simulation object.
func handle(input io.Reader, output io.Writer) error {
    data, err := io.ReadAll(io.LimitReader(input, 2*1024*1024+1))
    if err != nil { return fmt.Errorf("read snapshot: %w", err) }
    if len(data) > 2*1024*1024 { return fmt.Errorf("snapshot exceeds limit") }
    var req request
    if err := json.Unmarshal(data, &req); err != nil { return fmt.Errorf("decode snapshot: %w", err) }
    if req.Protocol != 1 { return fmt.Errorf("unsupported protocol: %d", req.Protocol) }
    res := response{Protocol: 1, Logs: []string{"Go hook: " + req.Event}, Commands: []featureCommand{}}
    // Optional example: unlock aviation after the scenario reaches a date.
    // Disabled templates never affect the base game.
    if req.Event == "day_advanced" && req.Day >= 365 {
        res.Commands = append(res.Commands, featureCommand{Type: "set_feature", Feature: "aviation", Enabled: true})
    }
    if err := json.NewEncoder(output).Encode(res); err != nil { return fmt.Errorf("encode response: %w", err) }
    return nil
}

func main() {
    if err := handle(os.Stdin, os.Stdout); err != nil { fmt.Fprintln(os.Stderr, err); os.Exit(1) }
}
