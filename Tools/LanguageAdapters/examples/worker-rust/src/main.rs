//! A worker mod in Rust, protocol 1.
//!
//! The whole contract: read one JSON request from stdin, write one JSON object to stdout,
//! exit 0. Nothing else may go to stdout - one stray print disables this worker for the rest
//! of the campaign. See `docs/WORKER_MOD_PROTOCOL_RU.md`.

use serde::Deserialize;
use serde_json::json;
use std::cmp::Ordering;
use std::io::{self, Read, Write};

#[derive(Deserialize)]
struct Nation {
    tag: String,
    treasury: f64,
    public_debt: f64,
}

#[derive(Deserialize)]
struct Request {
    protocol: i64,
    event: String,
    nations: Vec<Nation>,
}

/// The slice of the treasury this government puts against its debt in one visit.
const REPAY_SHARE: f64 = 0.02;

fn main() {
    let mut raw = String::new();
    if io::stdin().read_to_string(&mut raw).is_err() {
        // Any non-zero exit disables the worker for this campaign, deliberately.
        std::process::exit(2);
    }
    let request: Request = match serde_json::from_str(&raw) {
        Ok(request) => request,
        Err(_) => std::process::exit(2),
    };
    if request.protocol != 1 {
        std::process::exit(2);
    }

    let mut logs: Vec<String> = Vec::new();
    let mut commands: Vec<serde_json::Value> = Vec::new();
    if request.event == "day_advanced" {
        let worst = request
            .nations
            .iter()
            .filter(|nation| nation.public_debt > 0.0 && nation.treasury > 0.1)
            .max_by(|left, right| {
                left.public_debt
                    .partial_cmp(&right.public_debt)
                    .unwrap_or(Ordering::Equal)
            });
        if let Some(country) = worst {
            let amount = (country.treasury * REPAY_SHARE).min(0.05);
            commands.push(json!({
                "type": "manage_debt",
                "issuer": country.tag,
                "action": "repay",
                "amount": amount
            }));
            logs.push(format!("repaying a token amount of {}'s debt", country.tag));
        }
    }

    let answer = json!({ "protocol": 1, "logs": logs, "commands": commands });
    let stdout = io::stdout();
    let mut out = stdout.lock();
    if writeln!(out, "{}", answer).is_err() {
        std::process::exit(3);
    }
}
