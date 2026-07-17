using EventHorizon.Integration.Debug;

Run("shared geometry is separated by donor material", SharedGeometryDifferentMaterial);
Run("A/C order does not affect matching", DrawOrderChanges);
Run("missing optional fields remain matchable", MissingOptionalFields);
Run("similar candidates remain ambiguous", AmbiguousCandidates);
Run("invalid donor stops matching", InvalidDonor);
if (Environment.ExitCode == 0)
    Console.WriteLine("All TransparentDrawCorrelation matcher tests passed.");

static void SharedGeometryDifferentMaterial()
{
    var donorTexture = (nint)0xA0;
    var otherTexture = (nint)0xB0;
    var draws = new[]
    {
        Draw(1, CorrelationDrawStage.StageA, 0x10, 0x20, Set(otherTexture)),
        Draw(2, CorrelationDrawStage.StageA, 0x10, 0x20, Set(donorTexture)),
        Draw(3, CorrelationDrawStage.StageC, 0x10, 0x20, Set(donorTexture)),
    };

    var result = TransparentDrawCorrelationMatcher.Match(Donor(donorTexture), draws);
    Equal(2L, result.Candidates[0].StageA.Sequence);
    True(result.IsUnique);
}

static void DrawOrderChanges()
{
    var texture = (nint)0xA0;
    var draws = new[]
    {
        Draw(10, CorrelationDrawStage.StageC, 0x10, 0x20, Set(texture)),
        Draw(20, CorrelationDrawStage.StageA, 0x10, 0x20, Set(texture)),
    };

    var result = TransparentDrawCorrelationMatcher.Match(Donor(texture), draws);
    Equal("unique-candidate", result.Conclusion);
}

static void MissingOptionalFields()
{
    var texture = (nint)0xA0;
    var draws = new[]
    {
        Draw(1, CorrelationDrawStage.StageA, 0, 0, Set(texture), Set<ulong>()),
        Draw(2, CorrelationDrawStage.StageC, 0, 0, Set(texture), Set<ulong>()),
    };

    var result = TransparentDrawCorrelationMatcher.Match(Donor(texture), draws);
    Equal(1, result.Candidates.Count);
}

static void AmbiguousCandidates()
{
    var texture = (nint)0xA0;
    var draws = new[]
    {
        Draw(1, CorrelationDrawStage.StageA, 0x10, 0x20, Set(texture)),
        Draw(2, CorrelationDrawStage.StageA, 0x10, 0x20, Set(texture)),
        Draw(3, CorrelationDrawStage.StageC, 0x10, 0x20, Set(texture)),
    };

    var result = TransparentDrawCorrelationMatcher.Match(Donor(texture), draws);
    True(!result.IsUnique);
    Equal("ambiguous-candidates", result.Conclusion);
}

static void InvalidDonor()
{
    var result = TransparentDrawCorrelationMatcher.Match(
        new CorrelationDonorEvidence(false, new HashSet<nint>()),
        [Draw(1, CorrelationDrawStage.StageA, 0x10, 0x20, Set<nint>())]
    );
    Equal("donor-invalid", result.Conclusion);
}

static CorrelationDonorEvidence Donor(nint texture) => new(true, new HashSet<nint> { texture });

static IReadOnlySet<T> Set<T>(params T[] values)
    where T : notnull => new HashSet<T>(values);

static CorrelationDrawEvidence Draw(
    long sequence,
    CorrelationDrawStage stage,
    nint vertexBuffer,
    nint indexBuffer,
    IReadOnlySet<nint> resources,
    IReadOnlySet<ulong>? hashes = null
) => new(sequence, stage, vertexBuffer, indexBuffer, 12, 4, 2, resources, hashes ?? new HashSet<ulong> { 0x1234 });

static void Run(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
        Environment.ExitCode = 1;
    }
}

static void True(bool value)
{
    if (!value)
        throw new InvalidOperationException("expected true");
}

static void Equal<T>(T expected, T actual)
    where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"expected {expected}, got {actual}");
}
