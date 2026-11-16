-- Pure event-design example, not an attachable Godot class (.mod.lua).
-- This returns proposals only. It is NOT registered with the campaign event system.
-- Each function is defined once: Lua silently replaces earlier duplicate definitions.
local Event = {}

function Event.grid_failure_capital(state)
    if not state.at_war and state.capital_infrastructure_failure then
        return {
            title = "The lights go out in the capital",
            options = {
                { title = "Buy power abroad", effects = { treasury_percent = -0.045, stability = 0.012, standard_of_living = 0.001 } },
                { title = "Ration and repair", effects = { productivity = -0.002, stability = -0.01, radicalism = 0.006 } },
                { title = "Take over operator", effects = { stability = 0.008, radicalism = 0.004, treasury_percent = 0.006 } }
            }
        }
    end
    return nil
end

function Event.city_in_ruins(state)
    if state.at_war and state.worst_war_damage >= 0.45 then
        return {
            title = "A city in ruins",
            options = {
                { title = "Evacuate", effects = { stability = 0.004, gdp_percent = -0.002 } },
                { title = "Symbol", effects = { war_support = 0.02, stability = -0.004 } },
                { title = "Appeal for help", effects = { treasury_percent = 0.02, war_support = -0.006, war_damage = -0.02 } }
            }
        }
    end
    return nil
end

function Event.refugees_come_home(state)
    if not state.at_war and state.recent_war_damage > 0.1 then
        return {
            title = "Refugees come home",
            options = {
                { title = "Housing first", effects = { treasury_percent = -0.03, stability = 0.006 } },
                { title = "Market-led", effects = { standard_of_living = -0.004, radicalism = 0.004 } }
            }
        }
    end
    return nil
end

return Event
