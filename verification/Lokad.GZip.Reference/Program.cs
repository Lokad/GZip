using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Lokad.GZip.Verification;

if (args is ["probe"])
{
    Console.WriteLine($"{RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}");
    foreach (ReferenceCase fixture in ReferenceCases.Goldens().Take(4))
    {
        byte[] encoded = await ReferenceCases.CompressAsync(fixture, ReferenceCases.CreateStockStream, async: false);
        Console.WriteLine($"{fixture.Name} {encoded.Length} {Convert.ToHexString(encoded)}");
        byte[] async = await ReferenceCases.CompressAsync(fixture, ReferenceCases.CreateStockStream, async: true);
        if (!encoded.AsSpan().SequenceEqual(async))
            Console.WriteLine($"{fixture.Name}-async {async.Length} {Convert.ToHexString(async)}");
    }
    return 0;
}

if (args is not ["capture", _] and not ["capture", _, "--extended"] and not ["capture-benchmarks", _]
    and not ["capture-audit", _] and not ["audit", _])
{
    Console.Error.WriteLine("Usage: probe | capture <directory> [--extended] | capture-benchmarks <directory> | capture-audit <manifest> | audit <manifest>");
    return 2;
}

#if NET11_0_OR_GREATER
Version required = new(11, 0, 0);
const string referenceCommit = "297acf930fb0f5c07fd4e51d78e521b4ad2e5d1f";
#else
if (args[0] == "capture-benchmarks")
{
    Console.Error.WriteLine("Benchmark capture requires the .NET 11 target.");
    return 2;
}
Version required = new(10, 0, 15);
const string referenceCommit = "8e0f8ed2312954727f8919d7d1c2f78b483b4ee7";
#endif
string? compressionVersion = typeof(System.IO.Compression.GZipStream).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
bool verifyAudit = args[0] == "audit";
bool linuxX64 = OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64;
if ((!verifyAudit && !linuxX64) || Environment.Version != required || compressionVersion != $"{required}+{referenceCommit}")
{
    string platform = verifyAudit ? "" : " on Linux x64";
    Console.Error.WriteLine($"{args[0]} requires Microsoft .NET {required}{platform} with compression source commit {referenceCommit}.");
    return 2;
}

Console.WriteLine($"{RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}");
Console.WriteLine(compressionVersion);
if (args[0] == "capture-audit")
{
    await DifferentialAudit.CaptureAsync(args[1]);
    return 0;
}
if (verifyAudit)
{
    try
    {
        await DifferentialAudit.VerifyAsync(args[1]);
        return 0;
    }
    catch (InvalidDataException error)
    {
        Console.Error.WriteLine(error.Message);
        return 1;
    }
}

string directory = Path.GetFullPath(args[1]);
Directory.CreateDirectory(directory);
IEnumerable<ReferenceCase> cases = args[0] == "capture-benchmarks" ? BenchmarkWorkloads.All.Select(workload => workload.Generate()) :
    args.Length == 3 ? ReferenceCases.Extended() : ReferenceCases.Goldens();
foreach (ReferenceCase fixture in cases)
{
    byte[] sync = await ReferenceCases.CompressAsync(fixture, ReferenceCases.CreateStockStream, async: false);
    byte[] async = await ReferenceCases.CompressAsync(fixture, ReferenceCases.CreateStockStream, async: true);
    await WriteFixtureAsync(fixture.Name + ".gz", sync);
    if (!sync.AsSpan().SequenceEqual(async))
        await WriteFixtureAsync(fixture.Name + "-async.gz", async);
}
return 0;

async Task WriteFixtureAsync(string name, byte[] data)
{
    await File.WriteAllBytesAsync(Path.Combine(directory, name), data);
    Console.WriteLine($"{Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant()}  {name}");
}
