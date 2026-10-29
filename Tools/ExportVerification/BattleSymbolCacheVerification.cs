using System;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private void VerifyBattleSymbolCache() => GlobePresentation.VerifyBattleSymbolCache(Check);
}

public sealed partial class GlobePresentation
{
    internal static void VerifyBattleSymbolCache(Action<bool,string> check)
    {
        var fixture = new GlobePresentation();
        using var image = Image.CreateEmpty(2,2,false,Image.Format.Rgba8);
        image.Fill(Colors.White);
        using var texture = ImageTexture.CreateFromImage(image);
        var calls = 0;
        Texture2D Load(string path) { calls++; return path == "valid" ? texture : null; }
        try
        {
            for(var frame=0;frame<2000;frame++) fixture.BattleSymbolFor("missing",Load);
            check(calls==1,"missing battle icon is attempted once, not 2000 times");
            check(fixture.BattleSymbolFor("valid",Load)==texture && calls==2,
                "changed mod icon path loads immediately after a cached failure");
            for(var frame=0;frame<2000;frame++) fixture.BattleSymbolFor("valid",Load);
            check(calls==2,"successful battle icon reuses its native texture");
            fixture.InvalidateBattleSymbol();
            check(fixture.BattleSymbolFor("valid",Load)==texture && calls==3,
                "catalogue invalidation permits loading the same icon path again");
            fixture.BattleSymbolFor("missing",Load);fixture.InvalidateBattleSymbol();
            fixture.BattleSymbolFor("missing",Load);
            check(calls==5,"catalogue invalidation also clears a failed icon attempt");
        }
        finally { fixture.Free(); }
    }
}
