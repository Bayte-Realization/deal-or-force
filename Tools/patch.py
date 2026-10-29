import json
import os

def patch_file(filepath, key, value):
    try:
        with open(filepath, 'r', encoding='utf-8-sig') as f:
            data = json.load(f)

        # Рекурсивный поиск ключа для обновления
        def update_key(obj, target_key, new_val):
            if isinstance(obj, dict):
                for k in obj:
                    if k == target_key:
                        obj[k] = new_val
                        return True
                    if update_key(obj[k], target_key, new_val):
                        return True
            elif isinstance(obj, list):
                for item in obj:
                    if update_key(item, target_key, new_val):
                        return True
            return False

        if update_key(data, key, value):
            with open(filepath, 'w', encoding='utf-8') as f:
                json.dump(data, f, indent=4, ensure_ascii=False)
            print(f"Patched {filepath}")
        else:
            print(f"Key {key} not found in {filepath}")

    except Exception as e:
        print(f"Error patching {filepath}: {e}")

conflicts = {
    "Сделка предложена": ["136_deal_composer.json", "160_deal_negotiations.json"],
    "Военный заговор": ["143_deal_and_crisis_workspace.json", "161_situation_room.json"],
    "Непрерывное критическое давление": ["143_deal_and_crisis_workspace.json", "161_situation_room.json"]
}

base_path = "d:/Godot Projects/deal-or-force-godot-4-8/game/localization/russian/"
for key, files in conflicts.items():
    for f in files:
        patch_file(os.path.join(base_path, f), key, key)
