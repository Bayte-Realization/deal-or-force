// A worker mod in Go, protocol 1.
//
// The whole contract: read one JSON request from stdin, write one JSON object to stdout,
// exit 0. Nothing else may go to stdout - one stray print disables this worker for the rest
// of the campaign. See docs/WORKER_MOD_PROTOCOL_RU.md.
package main

import (
	"encoding/json"
	"os"
)

type nation struct {
	Tag        string  `json:"tag"`
	Treasury   float64 `json:"treasury"`
	PublicDebt float64 `json:"public_debt"`
}

type request struct {
	Protocol int      `json:"protocol"`
	Event    string   `json:"event"`
	Day      int      `json:"day"`
	Nations  []nation `json:"nations"`
}

type response struct {
	Protocol int              `json:"protocol"`
	Logs     []string         `json:"logs,omitempty"`
	Commands []map[string]any `json:"commands,omitempty"`
}

// repayShare is the slice of the treasury this government puts against its debt in one visit.
// A token payment, because the point of the example is the protocol, not the economic policy.
const repayShare = 0.02

func main() {
	var req request
	if err := json.NewDecoder(os.Stdin).Decode(&req); err != nil || req.Protocol != 1 {
		// Any non-zero exit disables the worker for this campaign, deliberately.
		os.Exit(2)
	}
	out := response{Protocol: 1}
	if req.Event == "day_advanced" {
		var worst *nation
		for i := range req.Nations {
			n := &req.Nations[i]
			if n.PublicDebt <= 0 || n.Treasury <= 0.1 {
				continue
			}
			if worst == nil || n.PublicDebt > worst.PublicDebt {
				worst = n
			}
		}
		if worst != nil {
			amount := worst.Treasury * repayShare
			if amount > 0.05 {
				amount = 0.05
			}
			out.Commands = append(out.Commands, map[string]any{
				"type":   "manage_debt",
				"issuer": worst.Tag,
				"action": "repay",
				"amount": amount,
			})
			out.Logs = append(out.Logs, "repaying a token amount of "+worst.Tag+"'s debt")
		}
	}
	if err := json.NewEncoder(os.Stdout).Encode(out); err != nil {
		os.Exit(3)
	}
}
