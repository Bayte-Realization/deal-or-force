use std::io::{self, Read};
use serde::Deserialize;

#[derive(Deserialize)]
struct Request { protocol: u32, event: String, day: u32 }

fn run() -> Result<(), Box<dyn std::error::Error>> {
    let mut input = String::new();
    io::stdin().take(2 * 1024 * 1024 + 1).read_to_string(&mut input)?;
    if input.len() > 2 * 1024 * 1024 { return Err("snapshot exceeds limit".into()); }
    let req: Request = serde_json::from_str(&input)?;
    if req.protocol != 1 { return Err("unsupported protocol".into()); }
    let commands = if req.event == "day_advanced" && req.day >= 365 {
        serde_json::json!([{"type": "set_feature", "feature": "aviation", "enabled": true}])
    } else { serde_json::json!([]) };
    let response = serde_json::json!({"protocol": 1, "commands": commands,
        "logs": [format!("Rust hook: {} at day {}", req.event, req.day)]});
    println!("{}", response);
    Ok(())
}

fn main() {
    if let Err(error) = run() { eprintln!("{}", error); std::process::exit(1); }
}
