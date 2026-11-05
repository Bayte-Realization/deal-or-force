# Compiled C# mod / Мод на C#

Requires the .NET 10 SDK. Copy this package out of `_templates`, choose a unique
manifest ID, build the DLL, then explicitly enable the package in the launcher.

From the source project, the template references the game's project automatically:

```powershell
dotnet build mods/my_mod/corecode/ExampleCSharpMod.csproj
```

Outside the source checkout, compile against the matching installed game assembly:

```powershell
dotnet build corecode/ExampleCSharpMod.csproj -p:GameAssembly="D:/Games/Deal or Force/data_DealOrForce_windows_x86_64/DealOrForce.dll"
```

Do not distribute a private copy of `DealOrForce.dll` with your mod. The host owns
the shared `IGlobalityMod`/`ModContext` types. The DLL path in the manifest is
`corecode/bin/Debug/net10.0/ExampleCSharpMod.dll`.

`OnLoad` runs when accepted, `OnCampaignCreated` binds a campaign, `OnDayAdvanced`
runs once per advanced day. A paused frame is not a day. `OnFrame` must not change
the simulation. Implement `IDisposable` for subscriptions/native resources you own.
The example's small monthly Korean stability change is demonstrative balance, not
a recommended production rule.

Исходники `.cs` из папки `Scripts` сами по себе не подменяют код игры. Нужна
собранная DLL с реализацией `IGlobalityMod`; совпадение имён классов не является
hot-patch. Для замены существующего поведения используйте оверлеи данных или
проверенные шлюзы подсистем, а затем свой код жизненного цикла. Этот код доверенный,
не изолированная песочница. Проверяйте сохранения и детерминизм отдельно.
