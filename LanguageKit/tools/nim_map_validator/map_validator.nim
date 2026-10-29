# SPDX-License-Identifier: GPL-3.0-or-later
	# Nim Map & Regional Data Validator for Deal or Force.
	# Validates province definition rows (definition.csv), claims.csv, and regional state JSONs
	# before launching the game, using deterministic ORC memory management.
	
	import std/[os, strutils, parsecsv, streams, json, tables, sets]
	
	type
	  ValidationReport* = object
		provincesChecked*: int
		statesChecked*: int
		errors*: seq[string]
	
	proc validateDefinitionCsv*(path: string, report: var ValidationReport, seenIds: var HashSet[int]) =
	  if not fileExists(path):
		report.errors.add("Missing definition.csv: " & path)
		return
	  var s = newFileStream(path, fmRead)
	  if s == nil:
		report.errors.add("Cannot open: " & path)
		return
	  var parser: CsvParser
	  open(parser, s, path, separator = ';')
	  var lineNum = 0
	  while readRow(parser):
		inc lineNum
		if parser.row.len == 0: continue
		let first = parser.row[0].strip()
		if first.len == 0 or first.startsWith("#"): continue
		if parser.row.len < 4:
		  report.errors.add(path & ":" & $lineNum & " expected at least 4 columns (id;r;g;b)")
		  continue
		try:
		  let id = parseInt(first)
		  if id < 0 or id > 65535:
			report.errors.add(path & ":" & $lineNum & " province ID out of 16-bit range: " & $id)
		  elif id in seenIds:
			report.errors.add(path & ":" & $lineNum & " duplicate province ID: " & $id)
		  else:
			seenIds.incl(id)
			inc report.provincesChecked
		except ValueError:
		  if lineNum > 1:
			report.errors.add(path & ":" & $lineNum & " invalid province ID: " & first)
	  close(parser)
	
	proc validateStateJsonDir*(dirPath: string, report: var ValidationReport) =
	  if not dirExists(dirPath): return
	  for kind, file in walkDir(dirPath):
		if kind == pcFile and file.endsWith(".json"):
		  try:
			let node = parseFile(file)
			if node.kind == JObject and node.hasKey("states") and node["states"].kind == JArray:
			  for state in node["states"]:
				inc report.statesChecked
				let id = if state.hasKey("id"): state["id"].getStr() else: ""
				let pop = if state.hasKey("population"): state["population"].getBiggestInt() else: 0
				if id.len == 0:
				  report.errors.add(file & ": state entry with empty id")
				if pop < 0:
				  report.errors.add(file & " (" & id & "): negative population " & $pop)
		  except CatchableError as e:
			report.errors.add(file & ": JSON parse error: " & e.msg)
	
	when isMainModule:
	  let root = if paramCount() >= 1: paramStr(1) else: "game"
	  var report = ValidationReport()
	  var seenIds = initHashSet[int]()
	  validateDefinitionCsv(root / "map_data" / "definition.csv", report, seenIds)
	  validateStateJsonDir(root / "history" / "states" / "2024", report)
	  echo "NIM_MAP_VALIDATOR provinces=", report.provincesChecked,
		   " states=", report.statesChecked,
		   " errors=", report.errors.len
	  for err in report.errors:
		echo "  ERROR: ", err
	  quit(if report.errors.len == 0: 0 else: 1)
	