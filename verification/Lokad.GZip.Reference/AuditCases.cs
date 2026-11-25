using System.IO.Compression;

namespace Lokad.GZip.Verification;

internal enum AuditOperationKind
{
    Flush,
    FlushAsync,
    WriteByte,
    Write,
    WriteAsync,
}

internal readonly record struct AuditOperation(AuditOperationKind Kind, int Offset = 0, int Count = 0);

internal sealed record AuditCase(
    ReferenceCase Fixture,
    IReadOnlyList<AuditOperation> Operations,
    bool DisposeAsync,
    bool RecordWriteLengths);

internal static class AuditCases
{
    private static readonly int[] BoundaryLengths =
    [
        0, 1, 3, 4, 19, 255, 258, 259, 261, 262, 263, 511, 512, 513, 8187, 8191, 8192, 8193,
        16383, 16384, 16385, 32506, 32507, 32767, 32768, 32769, 65519, 65535, 65536, 98317, 131072, 191011,
    ];

    private static readonly int[] ChunkSizes = [7, 261, 997, 8192, 32768, int.MaxValue];
    private static readonly ZLibCompressionStrategy[] Strategies = Enum.GetValues<ZLibCompressionStrategy>();

    internal static IEnumerable<AuditCase> Schedules()
    {
        AuditOperationKind[] actions =
        [
            AuditOperationKind.Write, AuditOperationKind.WriteAsync, AuditOperationKind.Flush,
            AuditOperationKind.FlushAsync, AuditOperationKind.WriteByte,
        ];
        int schedules = actions.Length * actions.Length * actions.Length;
        foreach (int level in new[] { 0, 1, 3, 6, 9 })
        {
            foreach (ZLibCompressionStrategy strategy in Strategies)
            {
                for (int schedule = 0; schedule < schedules; schedule++)
                {
                    AuditOperation[] operations =
                    [
                        new(actions[schedule % actions.Length]),
                        new(actions[schedule / actions.Length % actions.Length]),
                        new(actions[schedule / (actions.Length * actions.Length)]),
                    ];
                    var fixture = new ReferenceCase(
                        $"schedule-{level}-{strategy}-{schedule}", [], NumericLevel: level, Strategy: strategy);
                    yield return new(fixture, operations, DisposeAsync: false, RecordWriteLengths: true);
                    yield return new(fixture with { Name = fixture.Name + "-async-dispose" },
                        operations, DisposeAsync: true, RecordWriteLengths: true);
                }
            }
        }
    }

    internal static IEnumerable<AuditCase> Payloads()
    {
        for (int seed = 0; seed < 1536; seed++)
        {
            var random = new Random(seed);
            int length = seed % 3 == 0 ? random.Next(17, 200003) : BoundaryLengths[random.Next(BoundaryLengths.Length)];
            byte[] input = CreatePayload(random, length, seed);
            int level = random.Next(-1, 10);
            ZLibCompressionStrategy strategy = random.Next(4) == 0
                ? Strategies[random.Next(Strategies.Length)]
                : ZLibCompressionStrategy.Default;
#if NET11_0_OR_GREATER
            int window = random.Next(3) == 0 ? random.Next(8, 16) : -1;
#else
            const int window = -1;
#endif
            int maxChunk = ChunkSizes[random.Next(ChunkSizes.Length)];
            int flushEvery = random.Next(4) == 0 ? random.Next(1, 8) : 0;
            var fixture = new ReferenceCase($"seed-{seed}", input, ChunkSize: maxChunk, FlushEvery: flushEvery,
                NumericLevel: level, Strategy: strategy, WindowLog2: window);
            yield return new(fixture, CreateWriteSchedule(fixture, random, seed), DisposeAsync: seed % 2 == 0,
                RecordWriteLengths: false);
        }
    }

    private static byte[] CreatePayload(Random random, int length, int seed)
    {
        byte[] input = new byte[length];
        random.NextBytes(input);
        int shape = random.Next(8);
        switch (shape)
        {
            case 1:
                int alphabet = random.Next(2, 256);
                for (int index = 0; index < input.Length; index++)
                    input[index] = (byte)(input[index] % alphabet);
                break;
            case 2:
            case 3:
                int stride = random.Next(7, shape == 2 ? 263 : 32769);
                for (int index = stride; index < input.Length; index++)
                    if (random.Next(20) < 18)
                        input[index] = input[index % stride];
                break;
            case 4:
            case 5:
                for (int index = 0; index < input.Length;)
                {
                    int count = Math.Min(input.Length - index, random.Next(1, 1579));
                    if (random.Next(4) != 0)
                        input.AsSpan(index, count).Fill(input[index]);
                    index += count;
                }
                break;
            case 6:
                for (int index = 32768; index < input.Length;)
                {
                    int count = Math.Min(input.Length - index, random.Next(1, 258));
                    int distance = random.Next(24000, 32769);
                    input.AsSpan(index - distance, count).CopyTo(input.AsSpan(index));
                    index += count;
                }
                break;
            case 7:
                input.AsSpan().Fill((byte)seed);
                break;
        }
        return input;
    }

    private static List<AuditOperation> CreateWriteSchedule(ReferenceCase fixture, Random random, int seed)
    {
        var operations = new List<AuditOperation>();
        if (seed % 11 == 0)
            operations.Add(new(AuditOperationKind.FlushAsync));

        int chunk = 0;
        for (int offset = 0; offset < fixture.Input.Length;)
        {
            int limit = fixture.ChunkSize == int.MaxValue ? int.MaxValue : random.Next(1, fixture.ChunkSize + 1);
            int count = Math.Min(fixture.Input.Length - offset, limit);
            AuditOperationKind write = random.Next(2) == 0 ? AuditOperationKind.Write : AuditOperationKind.WriteAsync;
            operations.Add(new(write, offset, count));
            offset += count;
            if (fixture.FlushEvery > 0 && ++chunk % fixture.FlushEvery == 0)
            {
                AuditOperationKind flush = random.Next(2) == 0 ? AuditOperationKind.Flush : AuditOperationKind.FlushAsync;
                operations.Add(new(flush));
            }
        }
        return operations;
    }
}
