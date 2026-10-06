package main

import (
    "bytes"
    "encoding/json"
    "strings"
    "testing"
)

func TestHook(t *testing.T) {
    for _, day := range []int{0, 364, 365} {
        input, _ := json.Marshal(request{Protocol: 1, Event: "day_advanced", Day: day})
        var output bytes.Buffer
        if err := handle(bytes.NewReader(input), &output); err != nil { t.Fatal(err) }
        var res response
        if err := json.Unmarshal(output.Bytes(), &res); err != nil { t.Fatal(err) }
        if res.Protocol != 1 || (len(res.Commands) > 0) != (day >= 365) { t.Fatalf("unexpected response: %+v", res) }
    }
}

func TestRejectsInvalidInput(t *testing.T) {
    for _, input := range []string{`{`, `{"protocol":2}`, strings.Repeat("x", 2*1024*1024+1)} {
        if handle(strings.NewReader(input), &bytes.Buffer{}) == nil { t.Fatal("accepted invalid input") }
    }
}
