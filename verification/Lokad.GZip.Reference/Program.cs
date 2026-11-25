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

if (args is not ["capture", _] and not ["capture", _, "--extended"] and not ["capture-benchmarks", _])
{
    Console.Error.WriteLine("Usage: probe | capture <directory> [--extended] | capture-benchmarks <directory>");
    return 2;
}

#if NET11_0_OR_GREATER
Version required = new(11, 0, 0);
#else
if (args[0] == "capture-benchmarks")
{
    Console.Error.WriteLine("Benchmark capture requires the .NET 11 target.");
    return 2;
}
Version required = new(10, 0, 15);
#endif
if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
    Environment.Version != required)
{
    Console.Error.WriteLine($"Capture requires Microsoft .NET {required} on Linux x64.");
    return 2;
}

string directory = Path.GetFullPath(args[1]);
Directory.CreateDirectory(directory);
Console.WriteLine($"{RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}");
Console.WriteLine(typeof(System.IO.Compression.GZipStream).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
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
