using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Fields.FrEElement;
using Nethermind.Verkle.Polynomial;

// ReSharper disable InconsistentNaming

namespace Nethermind.Verkle.Proofs;

public class MultiProof
{
    private readonly CRS Crs;
    private readonly int DomainSize;
    private readonly PreComputedWeights PreComp;

    public MultiProof(CRS cRs, PreComputedWeights preComp)
    {
        PreComp = preComp;
        Crs = cRs;
        DomainSize = preComp.Domain.Length;
    }

    public VerkleProofStruct MakeMultiProof(Transcript transcript, List<VerkleProverQuery> queries)
    {
        int domainSize = PreComp.Domain.Length;

        // Stopwatch watch = new();
        // watch.Start();


        Banderwagon[] commitPoints = new Banderwagon[queries.Count];
        for (int i = 0; i < queries.Count; i++) commitPoints[i] = queries[i].NodeCommitPoint;
        AffinePoint[] normalizedCommitments = Banderwagon.BatchNormalize(commitPoints);

        transcript.DomainSep("multiproof");
        for (int i = 0; i < queries.Count; i++)
        {
            transcript.AppendPoint(normalizedCommitments[i], "C");
            transcript.AppendScalar(queries[i].ChildIndex, "z");
            transcript.AppendScalar(queries[i].ChildHash, "y");
        }

        FrE r = transcript.ChallengeScalar("r");
        FrE[] powersOfR = new FrE[queries.Count];
        powersOfR[0] = FrE.One;
        for (int i = 1; i < queries.Count; i++) FrE.MultiplyMod(in powersOfR[i - 1], in r, out powersOfR[i]);

        // We aggregate all the polynomials in evaluation form per domain point
        // to avoid work downstream.
        Dictionary<byte, LagrangeBasis> aggregatedPolyMap = new();
        for (int i = 0; i < queries.Count; i++)
        {
            LagrangeBasis f = queries[i].ChildHashPoly;
            byte evaluationPoint = queries[i].ChildIndex;

            LagrangeBasis scaledF = f * powersOfR[i];

            if (!aggregatedPolyMap.TryGetValue(evaluationPoint, out LagrangeBasis? poly))
            {
                aggregatedPolyMap[evaluationPoint] = scaledF;
                continue;
            }

            aggregatedPolyMap[evaluationPoint] = poly + scaledF;
        }


        FrE[] g = new FrE[domainSize];
        Span<FrE> quotient = new FrE[domainSize];
        foreach (KeyValuePair<byte, LagrangeBasis> pointAndPoly in aggregatedPolyMap)
        {
            Quotient.ComputeQuotientInsideDomain(PreComp, pointAndPoly.Value, pointAndPoly.Key, quotient);
            for (int j = 0; j < g.Length; j++)
            {
                g[j] += quotient[j];
                quotient[j] = FrE.Zero;
            }
        }

        Banderwagon d = Crs.Commit(g);
        transcript.AppendPoint(d, "D");

        FrE t = transcript.ChallengeScalar("t");
        // We only will calculate inverses for domain points that are actually queried.
        FrE[] denomInvs = new FrE[domainSize];
        foreach (KeyValuePair<byte, LagrangeBasis> pointAndPoly in aggregatedPolyMap)
            denomInvs[pointAndPoly.Key] = t - PreComp.Domain[pointAndPoly.Key];
        denomInvs = FrE.MultiInverse(denomInvs);

        FrE[] h = new FrE[domainSize];
        foreach (KeyValuePair<byte, LagrangeBasis> pointAndPoly in aggregatedPolyMap)
        {
            LagrangeBasis f = pointAndPoly.Value;
            for (int j = 0; j < f.Evaluations.Length; j++) h[j] += f.Evaluations[j] * denomInvs[pointAndPoly.Key];
        }

        Banderwagon e = Crs.Commit(h);
        transcript.AppendPoint(e, "E");

        FrE[] hMinusG = new FrE[domainSize];
        for (int i = 0; i < domainSize; i++) hMinusG[i] = h[i] - g[i];

        Banderwagon ipaCommitment = e - d;

        FrE[] inputPointVector = PreComp.BarycentricFormulaConstants(t);
        IpaProverQuery pQuery = new(hMinusG, ipaCommitment, t, inputPointVector);
        IpaProofStruct ipaProof = Ipa.MakeIpaProof(Crs, transcript, pQuery, out _);

        return new VerkleProofStruct(ipaProof, d);
    }

    public bool CheckMultiProof(Transcript transcript, VerkleVerifierQuery[] queries, VerkleProofStruct proof)
    {
        transcript.DomainSep("multiproof");
        foreach (VerkleVerifierQuery query in queries)
        {
            transcript.AppendPoint(query.NodeCommitPoint, "C");
            transcript.AppendScalar(query.ChildIndex, "z");
            transcript.AppendScalar(query.ChildHash, "y");
        }

        FrE r = transcript.ChallengeScalar("r");

        FrE[] powersOfR = new FrE[queries.Length];
        powersOfR[0] = FrE.One;
        for (int i = 1; i < queries.Length; i++) powersOfR[i] = powersOfR[i - 1] * r;

        Banderwagon d = proof.D;
        IpaProofStruct ipaProof = proof.IpaProof;
        transcript.AppendPoint(d, "D");
        FrE t = transcript.ChallengeScalar("t");

        // Calculate groupedEvals = r * y_i.
        FrE[] groupedEvals = new FrE[DomainSize];
        for (int i = 0; i < queries.Length; i++)
            groupedEvals[queries[i].ChildIndex] += powersOfR[i] * queries[i].ChildHash;

        // Compute helperScalarsDen = 1 / (t - z_i).
        FrE[] helperScalarDens = new FrE[DomainSize];
        foreach (byte childIndex in queries.Select(x => x.ChildIndex).Distinct())
            helperScalarDens[childIndex] = t - FrE.SetElement(childIndex);
        helperScalarDens = FrE.MultiInverse(helperScalarDens);

        // g2T = SUM [r^i * y_i] * [1 / (t - z_i)]
        FrE g2T = FrE.Zero;
        for (int i = 0; i < DomainSize; i++)
        {
            if (groupedEvals[i].IsZero) continue;
            g2T += groupedEvals[i] * helperScalarDens[i];
        }

        FrE[] helperScalars = new FrE[queries.Length];
        Banderwagon[] commitments = new Banderwagon[queries.Length];
        for (int i = 0; i < queries.Length; i++)
        {
            helperScalars[i] = helperScalarDens[queries[i].ChildIndex] * powersOfR[i];
            commitments[i] = queries[i].NodeCommitPoint;
        }

        Banderwagon g1Comm = Banderwagon.MultiScalarMul(commitments, helperScalars);

        transcript.AppendPoint(g1Comm, "E");

        FrE[] inputPointVector = PreComp.BarycentricFormulaConstants(t);
        Banderwagon ipaCommitment = g1Comm - d;
        IpaVerifierQuery queryX = new(ipaCommitment, t, inputPointVector, g2T, ipaProof);

        return Ipa.CheckIpaProof(Crs, transcript, queryX);
    }
}
