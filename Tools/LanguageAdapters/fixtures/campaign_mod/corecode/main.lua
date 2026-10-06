--- @class
--- @extends RefCounted
local Fixture = {}
local FixtureClass = gdclass(Fixture)

--- @registerMethod
function Fixture:on_load(api): boolean
    local old = api:Get("feature_enabled"):Call("aviation")
    assert(api:Get("set_feature"):Call("aviation", not old))
    assert(api:Get("feature_enabled"):Call("aviation") == not old)
    assert(api:Get("set_feature"):Call("aviation", old))
    assert(api:Get("feature_enabled"):Call("aviation") == old)
    api:Get("log"):Call("PASS native_luau_api_feature_roundtrip")
    return true
end

--- @registerMethod
function Fixture:on_campaign_created(api): boolean
    assert(api:Get("get_day"):Call() == 0)
    api:Get("log"):Call("PASS native_luau_campaign_created")
    return true
end

--- @registerMethod
function Fixture:on_day_advanced(api): boolean
    if not self.daily_seen then
        assert(api:Get("get_day"):Call() > 0)
        api:Get("log"):Call("PASS native_luau_day_advanced " .. tostring(api:Get("get_day"):Call()))
        self.daily_seen = true
    else
        api:Get("log"):Call("PASS native_luau_negative_hook_fixture")
        return false
    end
    return true
end

return FixtureClass
