--- @class
--- @extends RefCounted
local Events = require("engine_mod/corecode/modules/war_damage.mod")
local Check = {}
local CheckClass = gdclass(Check)

--- @registerMethod
function Check:grid_failure_options(): number
    local proposal = Events.grid_failure_capital({ at_war = false, capital_infrastructure_failure = true })
    assert(Events.grid_failure_capital({ at_war = true }) == nil)
    return #proposal.options
end

return CheckClass
