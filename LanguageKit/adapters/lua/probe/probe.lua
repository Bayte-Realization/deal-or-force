local Probe = GDCLASS()
Probe.extends = RefCounted
Probe.count = export(0)

function Probe:add(left, right)
    return left + right
end

function Probe:increment()
    self.count = self.count + 1
    return self.count
end

return Probe
