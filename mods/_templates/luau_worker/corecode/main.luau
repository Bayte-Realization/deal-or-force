-- Real Luau syntax and VM, not Lua compatibility emulation.
-- A stateless hook receives protocol JSON and returns protocol JSON.
function on_hook(request_json: string): string
    local day: number = tonumber(string.match(request_json, '"day":(%d+)')) or 0
    local is_day: boolean = string.find(request_json, '"event":"day_advanced"', 1, true) ~= nil
    if is_day and day >= 365 then
        return '{"protocol":1,"logs":["Luau unlocked aviation"],"commands":[{"type":"set_feature","feature":"aviation","enabled":true}]}'
    end
    return '{"protocol":1,"logs":["Luau hook executed"],"commands":[]}'
end
