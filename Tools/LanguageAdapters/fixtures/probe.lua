--- @class
--- @extends RefCounted
local Probe = {}
local ProbeClass = gdclass(Probe)

--- @registerMethod
function Probe:add(left: number, right: number): number
    return left + right
end

--- @registerMethod
function Probe:increment(): number
    self.count = (self.count or 0) + 1
    return self.count
end

--- @registerMethod
function Probe:on_load(api): boolean
    api:Get("log"):Call("native probe")
    return true
end

--- @registerMethod
function Probe:check_capabilities(api): boolean
    assert(api:Get("flag"):Call("RefCounted") == true)
    assert(api:Get("flag"):Call("other") == false)
    assert(api:Get("day"):Call() > 0)
    assert(api:IsReadOnly())
    return true
end

return ProbeClass
