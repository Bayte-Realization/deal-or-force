--!strict
-- SPDX-License-Identifier: GPL-3.0-only
-- BudgetCore.Evaluate for every country, in Luau, through the game's real Luau bridge: the
-- out-of-process worker (Tools/LuauWorker, deal-or-force-luau.exe), which reads this script,
-- compiles it, hands on_hook the request JSON on stdin and writes the returned string to stdout.
--
-- Luau numbers are doubles; the game's arithmetic is float32. Every operation is rounded back
-- to float32 (fr), which is exact: for +, -, *, / of two float32 values a double result rounded
-- once to float32 equals the float32 operation (53 >= 2*24+2). Inputs and outputs travel as the
-- uint32 bit patterns of the floats, so nothing is lost in decimal and NaN survives.
--
-- Request:  {"v":[N, L, tolBits, evaBits, lineFloats(L*12 bits), lineInts(L*4), offsets(L+1),
--                 factorSlots(F), factorWeights(F bits), slots(N*8 bits), nationFloats(N*8 bits),
--                 rates(N*L bits)]}
-- Response: {"us":<kernel microseconds>,"out":[N*8 bits]}

local fr: (number) -> number
if vector and (vector :: any).create then
	local create = (vector :: any).create
	fr = function(x: number): number return create(x, 0, 0).x end
else
	fr = function(x: number): number return (string.unpack("<f", string.pack("<f", x))) end
end

local function fromBits(b: number): number
	return (string.unpack("<f", string.pack("<I4", b)))
end

local function toBits(x: number): number
	return (string.unpack("<I4", string.pack("<f", x)))
end

local function max0(x: number): number
	if x > 0 then return x end
	if x ~= x then return x end
	return 0
end

function on_hook(request: string): string
	local values = table.create(4096)
	local count = 0
	for token in string.gmatch(request, "%-?%d+") do
		count += 1
		values[count] = tonumber(token)
	end
	local at = 0
	local function take(): number at += 1; return values[at] end
	local N = take()
	local L = take()
	local tol = fromBits(take())
	local eva = fromBits(take())
	local lf = table.create(L * 12)
	for i = 1, L * 12 do lf[i] = fromBits(take()) end
	local li = table.create(L * 4)
	for i = 1, L * 4 do li[i] = take() end
	local off = table.create(L + 1)
	for i = 1, L + 1 do off[i] = take() end
	local F = off[L + 1]
	local fs = table.create(F)
	for i = 1, F do fs[i] = take() end
	local fw = table.create(F)
	for i = 1, F do fw[i] = fromBits(take()) end
	local slots = table.create(N * 8)
	for i = 1, N * 8 do slots[i] = fromBits(take()) end
	local nf = table.create(N * 8)
	for i = 1, N * 8 do nf[i] = fromBits(take()) end
	local rates = table.create(N * L)
	for i = 1, N * L do rates[i] = fromBits(take()) end
	local out = table.create(N * 8, 0)

	local started = os.clock()
	local function scaleOf(line: number, slotBase: number): number
		local scale = lf[line * 12 + 3]
		for k = off[line + 1], off[line + 2] - 1 do
			scale = fr(scale + fr(fw[k + 1] * slots[slotBase + fs[k + 1] + 1]))
		end
		return scale
	end
	for c = 0, N - 1 do
		local slotBase = c * 8
		local n = c * 8
		local rateBase = c * L
		local weeklyOutput = nf[n + 1]
		local compliance
		if weeklyOutput <= 0 or eva <= 0 then
			compliance = 1
		else
			local burden = 0
			for line = 0, L - 1 do
				if li[line * 4 + 1] == 0 and li[line * 4 + 2] == 0 then
					local rate = rates[rateBase + line + 1]
					burden = fr(burden + fr(fr(rate * lf[line * 12 + 1]) * scaleOf(line, slotBase)))
				end
			end
			compliance = fr(1 / fr(1 + fr(eva * max0(fr(burden - tol)))))
		end
		local revenue, taxation, expenses = 0, 0, 0
		local growth, living, literacy, radicalism, stability = 0, 0, 0, 0, 0
		for line = 0, L - 1 do
			local f = line * 12
			local side = li[line * 4 + 1]
			local charge = li[line * 4 + 2]
			local basis
			if charge == 1 then basis = nf[n + 2]
			elseif charge == 2 then basis = nf[n + 3]
			elseif charge == 3 then basis = nf[n + 4]
			else basis = weeklyOutput end
			local rate = rates[rateBase + line + 1]
			local lossYield = lf[f + 2]
			local yld = if lossYield == lossYield and basis < 0 then lossYield else lf[f + 1]
			local amount = fr(fr(fr(basis * rate) * yld) * scaleOf(line, slotBase))
			if nf[n + 8] ~= 0 and li[line * 4 + 4] ~= 0 then
				amount = fr(amount + fr(fr(nf[n + 5] * nf[n + 6]) / 52))
			end
			if side == 0 and charge == 0 then amount = fr(amount * fr(compliance * nf[n + 7])) end
			if charge ~= 3 then amount = max0(amount) end
			if side == 0 then
				revenue = fr(revenue + amount)
				if charge == 0 or charge == 1 then taxation = fr(taxation + amount) end
			else
				expenses = fr(expenses + amount)
			end
			if li[line * 4 + 3] ~= 0 then
				local above = fr(rate - lf[f + 4])
				growth = fr(growth + fr(above * lf[f + 5]))
				living = fr(living + fr(above * lf[f + 6]))
				literacy = fr(literacy + fr(above * lf[f + 7]))
				radicalism = fr(radicalism + fr(above * lf[f + 8]))
				stability = fr(stability + fr(above * lf[f + 9]))
				local resentedAbove = lf[f + 10]
				if resentedAbove > 0 then
					local resented = max0(fr(rate - resentedAbove))
					if resented > 0 then
						living = fr(living + fr(resented * lf[f + 11]))
						radicalism = fr(radicalism + fr(resented * lf[f + 12]))
					end
				end
			end
		end
		local o = c * 8
		out[o + 1] = revenue; out[o + 2] = taxation; out[o + 3] = expenses; out[o + 4] = growth
		out[o + 5] = living; out[o + 6] = literacy; out[o + 7] = radicalism; out[o + 8] = stability
	end
	local micros = (os.clock() - started) * 1e6

	local parts = table.create(N * 8)
	for i = 1, N * 8 do parts[i] = tostring(toBits(out[i])) end
	return string.format('{"us":%d,"out":[%s]}', math.floor(micros + 0.5), table.concat(parts, ","))
end
