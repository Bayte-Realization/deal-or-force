<#
.SYNOPSIS
    Scaffold a new Deal or Force mod package with valid manifest, EN/RU localization, and starter files.
.DESCRIPTION
    Creates a complete mod directory ready for validation and playtesting under mods/ (or -OutDir).
    Supports: data, map, scenario, formable, gdscript, csharp, fsharp, rust_worker.
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Tools\new-mod.ps1 -Id my_nordic_mod -Name "Nordic Union Expanded" -Kind formable -Validate
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Tools\new-mod.ps1 -Id pacific_isles -Kind map -OutDir mods -Validate
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Id,

    [string]$Name = '',

    [string]$Author = 'Deal or Force Modder',

    [ValidateSet('data', 'map', 'scenario', 'formable', 'gdscript', 'csharp', 'fsharp', 'rust_worker')]
    [string]$Kind = 'data',

    [string]$OutDir = 'mods',

    [switch]$Validate,

    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$cleanId = $Id.Trim().ToLowerInvariant() -replace '[^a-z0-9_]', '_' -replace '_+', '_'
$cleanId = $cleanId.Trim('_')
if ($cleanId -notmatch '^[a-z][a-z0-9_]{1,63}$') {
    Write-Error "Mod -Id '$Id' must start with a lowercase ASCII letter and use 2-64 a-z, 0-9, or '_' characters (normalized '$cleanId')."
    exit 2
}
if ($Kind -eq 'map' -and $cleanId.Length -gt 48) {
    $cleanId = $cleanId.Substring(0, 48).TrimEnd('_')
}

if ([string]::IsNullOrWhiteSpace($Name)) {
    $words = ($cleanId -split '_') | Where-Object { $_ } | ForEach-Object { $_.Substring(0, 1).ToUpperInvariant() + $_.Substring(1) }
    $Name = $words -join ' '
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$resolvedOut = if ([System.IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $repoRoot $OutDir }
$modRoot = Join-Path $resolvedOut $cleanId

if ((Test-Path -LiteralPath $modRoot) -and -not $Force) {
    $existing = Get-ChildItem -LiteralPath $modRoot -Force -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Error "Target mod directory '$modRoot' already exists and is not empty. Pass -Force to overwrite starter files."
        exit 2
    }
}

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
function Write-ModFile([string]$relPath, [string]$content) {
    $target = Join-Path $modRoot $relPath
    $parent = Split-Path -Parent $target
    if (-not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    [System.IO.File]::WriteAllText($target, ($content -replace "`r`n", "`n").TrimEnd("`r", "`n") + "`n", $utf8NoBom)
}

function ConvertTo-Pascal([string]$snake) {
    (($snake -split '_') | Where-Object { $_ } | ForEach-Object { $_.Substring(0, 1).ToUpperInvariant() + $_.Substring(1) }) -join ''
}

$manifestObj = [ordered]@{
    id = $cleanId
    name = $Name
    version = '1.0.0'
    supported_game_version = '0.8.*'
    author = $Author
    enabled = $true
    load_order = 150
    dependencies = @()
    gdscript_entry = if ($Kind -eq 'gdscript') { 'scripts/mod_main.gd' } else { '' }
    csharp_assembly = ''
}
if ($Kind -eq 'rust_worker') {
    $manifestObj['worker_interval_days'] = 7
    $manifestObj['worker_timeout_ms'] = 500
}

Write-ModFile 'manifest.json' ($manifestObj | ConvertTo-Json -Depth 6)

$enLoc = [ordered]@{
    "mod.$cleanId.title" = $Name
    "mod.$cleanId.desc" = "Custom $Kind mod package by $Author."
}
$ruLoc = [ordered]@{
    "mod.$cleanId.title" = $Name
    "mod.$cleanId.desc" = "Пользовательский мод ($Kind) от $Author."
}

$starterNote = switch ($Kind) {
    'data' {
        $decId = "${cleanId}_initiative"
        $enLoc["studio.dec.$decId.name"] = "$Name Initiative"
        $ruLoc["studio.dec.$decId.name"] = "Инициатива: $Name"
        $enLoc["studio.dec.$decId.desc"] = "Launch a national initiative enabled by $Name."
        $ruLoc["studio.dec.$decId.desc"] = "Запустить национальную инициативу из мода $Name."
        $enLoc["studio.dec.$decId.unavail"] = "Requirements not currently met."
        $ruLoc["studio.dec.$decId.unavail"] = "Условия сейчас не выполнены."

        $decisions = @{
            $decId = [ordered]@{
                name = "studio.dec.$decId.name"
                description = "studio.dec.$decId.desc"
                unavailable = "studio.dec.$decId.unavail"
                resource_costs = @{ political_points = 15 }
                cooldown_days = 365
                trigger = @{ min_stability = 0.35 }
                effects = @{ stability = 0.025; prestige = 5 }
            }
        }
        Write-ModFile "common/decisions/00_${cleanId}.json" ($decisions | ConvertTo-Json -Depth 8)

        $evId = "${cleanId}_opening_debate"
        $enLoc["studio.ev.$evId.title"] = "${Name}: Policy Debate"
        $ruLoc["studio.ev.$evId.title"] = "${Name}: Дебаты о курсе"
        $enLoc["studio.ev.$evId.desc"] = "Ministers review priorities introduced by $Name."
        $ruLoc["studio.ev.$evId.desc"] = "Министры обсуждают приоритеты, добавленные модом $Name."
        $enLoc["studio.ev.$evId.opt1"] = "Adopt reform package"
        $ruLoc["studio.ev.$evId.opt1"] = "Принять пакет реформ"
        $enLoc["studio.ev.$evId.con1"] = "Improves stability at a modest fiscal outlay."
        $ruLoc["studio.ev.$evId.con1"] = "Повышает стабильность при умеренных расходах."

        $events = @{
            $evId = [ordered]@{
                title = "studio.ev.$evId.title"
                description = "studio.ev.$evId.desc"
                trigger = @{ max_stability = 0.90 }
                weight = 1.0
                options = @(
                    [ordered]@{
                        title = "studio.ev.$evId.opt1"
                        consequence = "studio.ev.$evId.con1"
                        effects = [ordered]@{ income_share = -0.02; stability = 0.02; prestige = 4 }
                    }
                )
            }
        }
        Write-ModFile "events/00_${cleanId}.json" ($events | ConvertTo-Json -Depth 8)
        "common/decisions/00_${cleanId}.json & events/00_${cleanId}.json"
    }
    'map' {
        $enLoc["studio.map.$cleanId.name"] = $Name
        $ruLoc["studio.map.$cleanId.name"] = $Name
        $enLoc["studio.map.$cleanId.desc"] = "Custom map pack by $Author."
        $ruLoc["studio.map.$cleanId.desc"] = "Пользовательский пакет карты от $Author."
        $mapObj = [ordered]@{
            id = $cleanId
            name = $Name
            description = "Custom map pack generated by Tools/new-mod.ps1"
            author = $Author
            cells_per_province = 40
            disputed_cells_per_province = 36
            siberia_multiplier = 1.35
            polar_multiplier = 2.4
            inherit_base_hydrology = $true
            inherits_base_hydrology = $true
        }
        Write-ModFile "maps/$cleanId/map.json" ($mapObj | ConvertTo-Json -Depth 6)
        "maps/$cleanId/map.json"
    }
    'scenario' {
        $enLoc["scenario.$cleanId.name"] = $Name
        $ruLoc["scenario.$cleanId.name"] = $Name
        $enLoc["scenario.$cleanId.desc"] = "Standalone scenario starting on 2024-01-01 using base world geometry."
        $ruLoc["scenario.$cleanId.desc"] = "Отдельный сценарий с началом 2024-01-01 на базовой карте мира."
        $scenObj = [ordered]@{
            id = $cleanId
            name = $Name
            description = "Standalone scenario starting on 2024-01-01."
            start_date = '2024-01-01'
            player_country = 'GBR'
            base_world = $true
        }
        Write-ModFile "scenarios/$cleanId/scenario.json" ($scenObj | ConvertTo-Json -Depth 6)
        "scenarios/$cleanId/scenario.json"
    }
    'formable' {
        $formId = "${cleanId}_union"
        $enLoc["studio.form.$formId.name"] = $Name
        $ruLoc["studio.form.$formId.name"] = $Name
        $enLoc["studio.form.$formId.desc"] = "Unite historic territories under $Name."
        $ruLoc["studio.form.$formId.desc"] = "Объедините исторические земли под флагом $Name."
        $formObj = @{
            $formId = [ordered]@{
                name = $Name
                description = "Unite historic territories under $Name."
                eligible_tags = @('NOR', 'SWE')
                required_regions = @('no_osl', 'se_sto')
                eligible_cultures = @('norwegian', 'swedish')
                minimum_stability = 0.65
                gdp_cost_share = 0.02
                governance_gain = 0.04
                productivity_gain = 0.02
            }
        }
        Write-ModFile "common/formable_nations/00_${cleanId}.json" ($formObj | ConvertTo-Json -Depth 8)
        "common/formable_nations/00_${cleanId}.json"
    }
    'gdscript' {
        Write-ModFile 'scripts/mod_main.gd' @"
extends RefCounted

func on_load(api) -> void:
	api.log("[$cleanId] GDScript mod loaded; map=" + api.get_map_id())

func on_campaign_created(api) -> void:
	var tags: PackedStringArray = api.get_all_tags()
	api.log("[$cleanId] Active countries: " + str(tags.size()))

func on_day_advanced(api) -> void:
	if api.get_day() % 30 == 0:
		var wars: Array = api.get_wars("")
		if wars.size() > 0:
			api.log("[$cleanId] Monthly active wars: " + str(wars.size()))
"@
        'scripts/mod_main.gd'
    }
    'csharp' {
        $asm = ConvertTo-Pascal $cleanId
        Write-ModFile "corecode/$asm.csproj" @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>disable</Nullable>
    <AssemblyName>$asm</AssemblyName>
  </PropertyGroup>
  <ItemGroup Condition="'`$(GameAssembly)' == ''">
    <ProjectReference Include="../../../../DealOrForce.csproj" />
  </ItemGroup>
  <ItemGroup Condition="'`$(GameAssembly)' != ''">
    <Reference Include="DealOrForce">
      <HintPath>`$(GameAssembly)</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
"@
        Write-ModFile 'corecode/ModEntry.cs' @"
using DealOrForce.Modding;
using DealOrForce.Simulation;

namespace $asm;

public sealed class ModEntry : IGlobalityMod
{
    public void OnLoad(ModContext context) =>
        context.Log("$Name C# module loaded");

    public void OnCampaignCreated(ModContext context) =>
        context.Log($"$Name active on map: {context.Campaign?.MapId}");

    public void OnDayAdvanced(ModContext context) { }
}
"@
        "corecode/$asm.csproj & corecode/ModEntry.cs"
    }
    'fsharp' {
        $asm = ConvertTo-Pascal $cleanId
        Write-ModFile "corecode/$asm.fsproj" @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>$asm</AssemblyName>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="ModEntry.fs" />
  </ItemGroup>
  <ItemGroup Condition="'`$(GameAssembly)' == ''">
    <ProjectReference Include="../../../../DealOrForce.csproj" />
  </ItemGroup>
</Project>
"@
        Write-ModFile 'corecode/ModEntry.fs' @"
namespace $asm

open DealOrForce.Modding

type ModEntry() =
    interface IGlobalityMod with
        member _.OnLoad(context) = context.Log("$Name F# module loaded")
        member _.OnCampaignCreated(_) = ()
        member _.OnDayAdvanced(_) = ()
"@
        "corecode/$asm.fsproj & corecode/ModEntry.fs"
    }
    'rust_worker' {
        $pkgName = $cleanId -replace '_', '-'
        Write-ModFile 'corecode/Cargo.toml' @"
[package]
name = "$pkgName"
version = "0.1.0"
edition = "2021"

[dependencies]
serde = { version = "1", features = ["derive"] }
serde_json = "1"
"@
        Write-ModFile 'corecode/src/main.rs' @"
use std::io::{self, Read};
use serde::Deserialize;

#[derive(Deserialize)]
struct Request { protocol: u32, event: String, day: u32 }

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let mut input = String::new();
    io::stdin().take(2 * 1024 * 1024 + 1).read_to_string(&mut input)?;
    let req: Request = serde_json::from_str(&input)?;
    if req.protocol != 1 { return Err("unsupported protocol".into()); }
    let out = serde_json::json!({
        "protocol": 1,
        "commands": [],
        "logs": [format!("$cleanId worker tick {} at day {}", req.event, req.day)]
    });
    println!("{}", out);
    Ok(())
}
"@
        'corecode/Cargo.toml & corecode/src/main.rs'
    }
}

Write-ModFile "localization/english/00_${cleanId}.json" ($enLoc | ConvertTo-Json -Depth 4)
Write-ModFile "localization/russian/00_${cleanId}.json" ($ruLoc | ConvertTo-Json -Depth 4)

Write-ModFile 'README.md' @"
# $Name

- **Mod ID:** ``$cleanId``
- **Kind:** ``$Kind``
- **Author:** $Author
- **Starter Entry:** ``$starterNote``

## Validate

``````powershell
powershell -ExecutionPolicy Bypass -File Tools\validate-mod.ps1 -ModPath "$modRoot" -Strict
``````
"@

Write-Host "Scaffolded mod '$cleanId' ($Kind) at: $modRoot"

if ($Validate) {
    $validator = Join-Path $PSScriptRoot 'validate-mod.ps1'
    & $validator -ModPath $modRoot -Strict
    exit $LASTEXITCODE
}
