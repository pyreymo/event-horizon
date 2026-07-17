#if DEBUG
namespace EventHorizon.Integration.Debug;

internal enum CorrelationDrawStage
{
    StageA,
    StageC,
}

internal sealed record CorrelationDrawEvidence(
    long Sequence,
    CorrelationDrawStage Stage,
    nint VertexBuffer,
    nint IndexBuffer,
    uint ElementCount,
    uint StartIndex,
    int BaseVertex,
    IReadOnlySet<nint> ShaderResources,
    IReadOnlySet<ulong> ConstantBufferHashes
);

internal sealed record CorrelationDonorEvidence(bool IsValid, IReadOnlySet<nint> TextureResources);

internal sealed record TransparentDrawPairCandidate(
    CorrelationDrawEvidence StageA,
    CorrelationDrawEvidence StageC,
    int Score,
    IReadOnlyList<string> Evidence
);

internal sealed record TransparentDrawMatchResult(IReadOnlyList<TransparentDrawPairCandidate> Candidates, bool IsUnique, string Conclusion);

internal static class TransparentDrawCorrelationMatcher
{
    public static TransparentDrawMatchResult Match(CorrelationDonorEvidence donor, IReadOnlyList<CorrelationDrawEvidence> draws)
    {
        if (!donor.IsValid)
            return new TransparentDrawMatchResult([], false, "donor-invalid");

        var stageA = draws.Where(draw => draw.Stage == CorrelationDrawStage.StageA).ToArray();
        var stageC = draws.Where(draw => draw.Stage == CorrelationDrawStage.StageC).ToArray();
        var candidates = new List<TransparentDrawPairCandidate>();
        foreach (var a in stageA)
        {
            foreach (var c in stageC)
            {
                if (!a.ShaderResources.Overlaps(donor.TextureResources) || !c.ShaderResources.Overlaps(donor.TextureResources))
                {
                    continue;
                }

                var evidence = new List<string>();
                var score = ScoreDonorResources(a, c, donor, evidence);
                score += ScoreGeometry(a, c, evidence);
                score += ScoreConstants(a, c, evidence);
                if (score >= 40)
                    candidates.Add(new TransparentDrawPairCandidate(a, c, score, evidence));
            }
        }

        candidates.Sort(
            (left, right) =>
            {
                var score = right.Score.CompareTo(left.Score);
                return score != 0 ? score : left.StageA.Sequence.CompareTo(right.StageA.Sequence);
            }
        );
        if (candidates.Count == 0)
            return new TransparentDrawMatchResult([], false, "no-candidate");

        var unique = candidates.Count == 1 || candidates[0].Score - candidates[1].Score >= 15;
        return new TransparentDrawMatchResult(candidates.Take(16).ToArray(), unique, unique ? "unique-candidate" : "ambiguous-candidates");
    }

    private static int ScoreDonorResources(
        CorrelationDrawEvidence stageA,
        CorrelationDrawEvidence stageC,
        CorrelationDonorEvidence donor,
        List<string> evidence
    )
    {
        var stageAHits = stageA.ShaderResources.Intersect(donor.TextureResources).Count();
        var stageCHits = stageC.ShaderResources.Intersect(donor.TextureResources).Count();
        var score = Math.Min(stageAHits, 2) * 20 + Math.Min(stageCHits, 2) * 20;
        if (stageAHits != 0)
            evidence.Add($"stage-a-donor-textures:{stageAHits}");
        if (stageCHits != 0)
            evidence.Add($"stage-c-donor-textures:{stageCHits}");

        var sharedResources = stageA.ShaderResources.Intersect(stageC.ShaderResources).Count();
        if (sharedResources != 0)
        {
            score += Math.Min(sharedResources, 2) * 5;
            evidence.Add($"shared-shader-resources:{sharedResources}");
        }
        return score;
    }

    private static int ScoreGeometry(CorrelationDrawEvidence stageA, CorrelationDrawEvidence stageC, List<string> evidence)
    {
        var score = 0;
        if (stageA.VertexBuffer != 0 && stageA.VertexBuffer == stageC.VertexBuffer)
        {
            score += 20;
            evidence.Add("same-vertex-buffer");
        }
        if (stageA.IndexBuffer != 0 && stageA.IndexBuffer == stageC.IndexBuffer)
        {
            score += 20;
            evidence.Add("same-index-buffer");
        }
        if (stageA.ElementCount == stageC.ElementCount && stageA.StartIndex == stageC.StartIndex && stageA.BaseVertex == stageC.BaseVertex)
        {
            score += 15;
            evidence.Add("same-draw-range");
        }
        return score;
    }

    private static int ScoreConstants(CorrelationDrawEvidence stageA, CorrelationDrawEvidence stageC, List<string> evidence)
    {
        var sharedHashes = stageA.ConstantBufferHashes.Intersect(stageC.ConstantBufferHashes).Count();
        if (sharedHashes == 0)
            return 0;

        evidence.Add($"shared-constant-hashes:{sharedHashes}");
        return Math.Min(sharedHashes, 2) * 10;
    }
}
#endif
