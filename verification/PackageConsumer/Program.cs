using System.Buffers;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml.Linq;
using GZipStream = Lokad.GZip.GZipStream;

#if NET11_0_OR_GREATER
const string framework = "net11.0";
#else
const string framework = "net10.0";
#endif

using ZipArchive package = ZipFile.OpenRead(args[0]);
using ZipArchive symbols = ZipFile.OpenRead(args[1]);
string? revision = args.Length > 2 ? args[2] : null;
using (Stream nuspec = Open(package, "Lokad.GZip.nuspec"))
{
    XElement metadata = XDocument.Load(nuspec).Descendants().Single(element => element.Name.LocalName == "metadata");
    XNamespace ns = metadata.Name.Namespace;
    Require((string?)metadata.Element(ns + "id") == "Lokad.GZip", "Unexpected package ID.");
    Require(!metadata.Descendants(ns + "dependency").Any(), "The runtime package has dependencies.");
    if (revision is not null)
    {
        XElement? repository = metadata.Element(ns + "repository");
        Require((string?)repository?.Attribute("commit") == revision, "Package repository commit is incorrect.");
    }
}

foreach (string file in new[] { "README.md", "LICENSE.txt", "CHANGELOG.md", "THIRD-PARTY-NOTICES.txt", $"lib/{framework}/Lokad.GZip.xml" })
    Require(package.GetEntry(file) is not null, $"Missing package file {file}.");

string[] libraries = package.Entries.Select(entry => entry.FullName)
    .Where(name => name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
    .OrderBy(name => name, StringComparer.Ordinal).ToArray();
Require(libraries.SequenceEqual(new[] { "lib/net10.0/Lokad.GZip.dll", "lib/net11.0/Lokad.GZip.dll" }),
    "The package must contain exactly one library per target framework.");
Require(!package.Entries.Any(entry => entry.FullName.StartsWith("runtimes/", StringComparison.OrdinalIgnoreCase)),
    "The package has runtime-specific assets.");

string dllPath = $"lib/{framework}/Lokad.GZip.dll";
using MemoryStream library = ReadEntry(package, dllPath);
byte[] loaded = File.ReadAllBytes(typeof(GZipStream).Assembly.Location);
Require(library.GetBuffer().AsSpan(0, (int)library.Length).SequenceEqual(loaded), "The consumer loaded a different library.");
using var pe = new PEReader(library);
Require(pe.PEHeaders.CorHeader?.Flags.HasFlag(CorFlags.ILOnly) == true, "The library must contain managed IL only.");
MetadataReader assembly = pe.GetMetadataReader();
Require(!assembly.MethodDefinitions.Any(handle => assembly.GetMethodDefinition(handle).Attributes.HasFlag(MethodAttributes.PinvokeImpl)),
    "The library declares a native import.");
Require(!assembly.TypeReferences.Any(handle =>
{
    TypeReference type = assembly.GetTypeReference(handle);
    return assembly.GetString(type.Namespace) == "System.IO.Compression" &&
        assembly.GetString(type.Name) is "DeflateStream" or "GZipStream" or "ZLibStream";
}), "The library references a stock compression stream.");
DebugDirectoryEntry codeViewEntry = pe.ReadDebugDirectory().Single(entry => entry.Type == DebugDirectoryEntryType.CodeView);
CodeViewDebugDirectoryData codeView = pe.ReadCodeViewDebugDirectoryData(codeViewEntry);

using MemoryStream pdb = ReadEntry(symbols, $"lib/{framework}/Lokad.GZip.pdb");
using var provider = MetadataReaderProvider.FromPortablePdbStream(pdb);
MetadataReader reader = provider.GetMetadataReader();
DebugMetadataHeader header = reader.DebugMetadataHeader ?? throw new InvalidDataException("Missing portable symbol header.");
var id = new BlobContentId(header.Id);
Require(codeView.Guid == id.Guid && codeViewEntry.Stamp == id.Stamp, "The symbol package and library have different build IDs.");

if (revision is not null)
{
    var sourceLinkKind = new Guid("cc110556-a091-4d38-9fec-25ab9a351a6a");
    CustomDebugInformation sourceLink = reader.CustomDebugInformation
        .Select(reader.GetCustomDebugInformation)
        .Single(info => reader.GetGuid(info.Kind) == sourceLinkKind);
    using JsonDocument mapping = JsonDocument.Parse(reader.GetBlobBytes(sourceLink.Value));
    JsonProperty[] documents = mapping.RootElement.GetProperty("documents").EnumerateObject().ToArray();
    Require(documents.Length > 0 && documents.All(document =>
        document.Value.GetString()?.Contains($"/{revision}/", StringComparison.Ordinal) == true),
        "Source Link does not point to the expected revision.");
}

using var buffer = new MemoryStream();
await using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
    await gzip.WriteAsync("Lokad.GZip\n"u8.ToArray());
const string expected = "1F8B0800000000000003F3C9CF4E4CD1738FCA2CE002000DDD9ED10B000000";
Require(Convert.ToHexString(buffer.ToArray()) == expected, "Packed compression differs from the Linux reference.");
buffer.Position = 0;
using var source = new GZipStream(buffer, CompressionMode.Decompress);
using var output = new MemoryStream();
await source.CopyToAsync(output);
Require(output.ToArray().AsSpan().SequenceEqual("Lokad.GZip\n"u8), "Packed decompression differs from the input.");
VerifyCallerBuffers("Lokad.GZip\n"u8, Convert.FromHexString(expected));
Console.WriteLine($"{framework} {RuntimeInformation.FrameworkDescription} {RuntimeInformation.OSDescription} package consumer passed.");

static void VerifyCallerBuffers(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> reference)
{
    byte[] member = new byte[reference.Length + 1];
    Span<byte> encoded = member.AsSpan(0, reference.Length);
    using var encoder = GZipStream.CreateSpanEncoder();
    OperationStatus status = encoder.Write(payload, encoded, out int consumed, out int encodedBytes);
    Require(status == OperationStatus.Done && consumed == payload.Length, "Packed span encoder did not accept the payload.");
    do
    {
        Span<byte> destination = encoded[encodedBytes..];
        status = encoder.Finish(destination[..Math.Min(destination.Length, 3)], out int written);
        Require(status == OperationStatus.Done || (status == OperationStatus.DestinationTooSmall && written > 0),
            "Packed span encoder did not complete the member.");
        encodedBytes += written;
    } while (status != OperationStatus.Done);
    Require(encodedBytes == reference.Length && encoded.SequenceEqual(reference),
        "Packed span compression differs from the Linux reference.");

    member[^1] = 0xA5;
    Span<byte> decoded = stackalloc byte[payload.Length];
    using var decoder = GZipStream.CreateSpanDecoder();
    status = decoder.Read(member, decoded[..2], out consumed, out int decodedBytes, isFinalInput: true);
    Require(status == OperationStatus.DestinationTooSmall && decodedBytes == 2,
        "Packed span decoder did not request more output space.");
    status = decoder.Read(member.AsSpan(consumed), decoded[decodedBytes..], out int consumedTail, out int decodedTail,
        isFinalInput: true);
    Require(status == OperationStatus.Done && consumed + consumedTail == reference.Length &&
        decodedBytes + decodedTail == decoded.Length && decoded.SequenceEqual(payload),
        "Packed span decoder did not recover the payload and preserve the member boundary.");
}

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidDataException(message);
}

static Stream Open(ZipArchive archive, string path)
    => archive.GetEntry(path)?.Open() ?? throw new InvalidDataException($"Missing package entry {path}.");

static MemoryStream ReadEntry(ZipArchive archive, string path)
{
    using Stream entry = Open(archive, path);
    var bytes = new MemoryStream();
    entry.CopyTo(bytes);
    bytes.Position = 0;
    return bytes;
}
