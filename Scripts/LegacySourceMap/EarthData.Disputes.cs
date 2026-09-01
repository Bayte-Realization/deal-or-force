namespace Globality;

public static partial class EarthData
{
	/// <summary>Sparse dispute index: varint gap, varint length, varint dispute id.</summary>
	[SkipHotload]
	static readonly string[] PackedDisputesParts =
	{
		"o+7gAQIB+SwBAQQEAfcsDQHzLBABAwIB7CwVAessGgHmLBsB5iwbAQQDAd4sIQHfLCEB4CwgAeAsIQHfLCEB3iwhAd8sIAHgLB8B4iwbAecsGAHoLBkB5y",
		"weAeEsGgHlLBoB5CwcAeQsGwHlLB8B4CwgAeAsIAHgLCAB4CwgAQEBAd0sIgHdLCIB3SwiAdwsJAHcLCQB2iwlAdksGAEECwHWLBoB4ywdAd0sIQHZLCIB",
		"yiwDAQ4kAccsDQEGJgHELBQBAiYBwiw9AcEsQQG9LEIBvSxCAbwsPgECBAG6LDsBCQEBuiw7AcIsOQEDAQHALDkBwyw8AcIsNgEHAQG/LDIBBQEBBwEBvi",
		"wxAcssNAHJLAEBATMByiwwAQEDAcssMAECAgHLLDABAwEBzSwsAQUBAc4sAQEBDAEBAgEBGQEFAQHULAIBCwEBAQMBARMBAwEB3SwDAQkTAe0sFAHtLBMB",
		"6SwBAQEWAeMsAQECGgHjLB4B3ywiAdwsJQHZLCgB1iwrAQkBAQYBAcMsLQEIAQEDCQG+LAMBBCcBBgwBAQEBxyw2AcssNQHNLAEBAjAB0iweAQQMAdIsHQ",
		"EHAwECAgHWLBwB5CwZAeYsGQHmLBMB7iwOAfEsDgHxLA8B8SwNAfYsCQH4LAUB4v0FCgL4LAsC+CwLAvcsDQL2LA0C9iwNAvUsDQLzLBAC8SwOAvQsDAL3",
		"LAcC+iwFAvssBAIeAgMEBQPTLAMCHwwD0ywBAh8PA/AsEAPwLBED7ywRA/AsEAPyLA0D9ywIAw==",
	};

	[SkipHotload]
	static string _packeddisputes;

	public static string PackedDisputes => _packeddisputes ??= string.Concat( PackedDisputesParts );
}
