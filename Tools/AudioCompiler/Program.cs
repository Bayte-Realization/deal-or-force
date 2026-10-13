// Deterministic continuous Shepard/Risset stem, independent of generated music.
// Usage: dotnet run --project Tools/AudioCompiler -- OUTPUT.wav [seconds]
using System.Globalization;
if (args.Length == 0) throw new ArgumentException("Output WAV path required.");
var seconds = args.Length > 1 ? double.Parse(args[1], CultureInfo.InvariantCulture) : 58.125;
if (!double.IsFinite(seconds) || seconds < 1 || seconds > 300) throw new ArgumentOutOfRangeException(nameof(seconds));
const int rate = 48000;
var count = (int)Math.Round(seconds * rate);
seconds = count / (double)rate;
var samples = new float[count];
var peak = 0d; var square = 0d;
// N is divisible by 2^4: every oscillator has an integer number of integrated
// cycles at the loop boundary. Low/high octave tails are essentially inaudible.
const double cycles = 6144;
var baseFrequency = cycles * Math.Log(2) / seconds;
for (var i = 0; i < count; i++)
{
    var u = i / (double)count; var rise = Math.Pow(2, u);
    var value = 0d; var mass = 0d;
    for (var octave = -4; octave <= 8; octave++)
    {
        var scale = Math.Pow(2, octave);
        var frequency = baseFrequency * scale * rise;
        var window = Math.Exp(-.5 * Math.Pow(Math.Log2(frequency / 420) / 1.15, 2));
        var phase = 2 * Math.PI * cycles * scale * (rise - 1);
        value += Math.Sin(phase) * window; mass += window;
    }
    samples[i] = (float)(.25 * value / mass);
    peak = Math.Max(peak, Math.Abs(samples[i])); square += samples[i] * samples[i];
}
var edgeDelta = samples[0] - samples[^1];
var initialDelta = samples[1] - samples[0];
var mismatch = Math.Abs(edgeDelta - initialDelta);
if (mismatch > .002 || peak > .251 || square <= 0) throw new Exception("Stem continuity/amplitude check failed.");
using (var file = new FileStream(args[0], FileMode.CreateNew))
using (var writer = new BinaryWriter(file))
{
    void Tag(string tag) => writer.Write(System.Text.Encoding.ASCII.GetBytes(tag));
    Tag("RIFF"); writer.Write(36 + count * 4); Tag("WAVE"); Tag("fmt "); writer.Write(16);
    writer.Write((short)3); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 4);
    writer.Write((short)4); writer.Write((short)32); Tag("data"); writer.Write(count * 4);
    foreach (var sample in samples) writer.Write(sample);
}
Console.WriteLine(FormattableString.Invariant($"SHEPARD seconds={seconds:0.000} rate={rate} peak={peak:0.000000} rms={Math.Sqrt(square / count):0.000000} seam_slope_mismatch={mismatch:0.00000000}"));
