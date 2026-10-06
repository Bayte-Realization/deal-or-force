--- @class
--- @extends RefCounted
local CampaignMod = {}
local CampaignModClass = gdclass(CampaignMod)

--- @registerMethod
function CampaignMod:on_load(api): boolean
    api:Get("log"):Call("Native Luau campaign mod loaded")
    return true
end

--- @registerMethod
function CampaignMod:on_campaign_created(api): boolean
    -- Read-only dictionary of explicit API capabilities; no Object.Call bypass.
    api:Get("log"):Call("Luau sees campaign day " .. tostring(api:Get("get_day"):Call()))
    return true
end

--- @registerMethod
function CampaignMod:on_day_advanced(api): boolean
    local day: number = api:Get("get_day"):Call()
    if day == 365 then
        api:Get("set_feature"):Call("aviation", true)
    end
    return true
end

return CampaignModClass
