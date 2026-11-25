using System.Reflection;

namespace Lokad.GZip.Verification;

internal static class ReferenceDigests
{
    internal static Dictionary<string, string> Read(Assembly assembly, string resourceName)
    {
        using Stream source = assembly.GetManifestResourceStream(resourceName) ??
            throw new InvalidOperationException($"The Linux reference manifest {resourceName} is missing.");
        using var reader = new StreamReader(source);
        return Read(reader, resourceName);
    }

    internal static Dictionary<string, string> Read(TextReader reader, string name)
    {
        var digests = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.ReadLine() is string line)
        {
            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 2 || fields[0].Length != 64)
                throw new InvalidDataException($"Invalid SHA-256 entry in {name}.");
            digests.Add(fields[1], fields[0]);
        }
        return digests;
    }
}
