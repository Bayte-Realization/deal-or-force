import os
import json

base_file = r"d:\Godot Projects\deal-or-force-godot-4-8\game\localization\russian\00_russian.json"
loc_dir = r"d:\Godot Projects\deal-or-force-godot-4-8\game\localization\russian"

def strip_comments(text):
    out = []
    in_string = False
    escape = False
    i = 0
    n = len(text)
    while i < n:
        c = text[i]
        if escape:
            out.append(c)
            escape = False
            i += 1
            continue
        if c == '\\':
            out.append(c)
            if in_string:
                escape = True
            i += 1
            continue
        if c == '"':
            in_string = not in_string
            out.append(c)
            i += 1
            continue
        if not in_string and i + 1 < n and c == '/' and text[i+1] == '/':
            # Skip until newline
            while i < n and text[i] != '\n':
                i += 1
            continue
        out.append(c)
        i += 1
    return "".join(out)

print(f"Reading base keys from {base_file}...")
with open(base_file, "r", encoding="utf-8") as f:
    base_data = json.loads(strip_comments(f.read()), strict=False)

base_keys = set(base_data.keys())
print(f"Found {len(base_keys)} base keys.")

total_removed = 0

for filename in sorted(os.listdir(loc_dir)):
    if not filename.endswith(".json") or filename == "00_russian.json":
        continue

    filepath = os.path.join(loc_dir, filename)
    try:
        with open(filepath, "r", encoding="utf-8") as f:
            content = f.read()

        data = json.loads(strip_comments(content), strict=False)

        # Find keys to remove
        keys_to_remove = [k for k in data.keys() if k in base_keys]

        if keys_to_remove:
            print(f"Removing {len(keys_to_remove)} duplicate keys from {filename}...")
            for k in keys_to_remove:
                del data[k]
                total_removed += 1

            # Write back
            with open(filepath, "w", encoding="utf-8") as f:
                json.dump(data, f, ensure_ascii=False, indent=2)

    except Exception as e:
        print(f"Error processing {filename}: {e}")

print(f"Complete! Removed a total of {total_removed} duplicate keys.")
