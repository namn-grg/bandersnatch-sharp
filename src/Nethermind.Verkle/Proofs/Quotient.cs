using System.Buffers.Binary;
using Nethermind.Verkle.Polynomial;
using Nethermind.Verkle.Fields.FrEElement;

namespace Nethermind.Verkle.Proofs;

public static class Quotient
{
    public static FrE[] ComputeQuotientInsideDomain(PreComputedWeights preComp, LagrangeBasis f, byte index)
    {
        int domainSize = f.Evaluations.Length;

        FrE[] inverses = preComp.DomainInv;
        FrE[] aPrimeDomain = preComp.APrimeDomain;
        FrE[] aPrimeDomainInv = preComp.APrimeDomainInv;

        FrE[] q = new FrE[domainSize];
        FrE y = f.Evaluations[index];

        for (int i = 0; i < domainSize; i++)
        {
            if (i == index) continue;

            int firstIndex = (i - index) < 0 ? (inverses.Length + (i - index)) : (i - index);
            int secondIndex = (index - i) < 0 ? (inverses.Length + index - i) : (index - i);

            q[i] = (f.Evaluations[i] - y) * inverses[firstIndex];
            q[index] += (f.Evaluations[i] - y) * inverses[secondIndex] * aPrimeDomain[index] * aPrimeDomainInv[i];
        }

        return q;
    }

    public static void ComputeQuotientInsideDomain(PreComputedWeights preComp, LagrangeBasis f, byte index, Span<FrE> quotient)
    {
        int domainSize = f.Evaluations.Length;

        FrE[] inverses = preComp.DomainInv;
        FrE[] aPrimeDomain = preComp.APrimeDomain;
        FrE[] aPrimeDomainInv = preComp.APrimeDomainInv;

        FrE y = f.Evaluations[index];

        for (int i = 0; i < domainSize; i++)
        {
            if (i == index) continue;

            int firstIndex = (i - index) < 0 ? (inverses.Length + (i - index)) : (i - index);
            int secondIndex = (index - i) < 0 ? (inverses.Length + index - i) : (index - i);

            quotient[i] = (f.Evaluations[i] - y) * inverses[firstIndex];
            quotient[index] += (f.Evaluations[i] - y) * inverses[secondIndex] * aPrimeDomain[index] * aPrimeDomainInv[i];
        }
    }

    public static FrE[] ComputeQuotientOutsideDomain(PreComputedWeights preComp, LagrangeBasis f, FrE z, FrE y)
    {
        FrE[] domain = preComp.Domain;
        int domainSize = domain.Length;

        FrE[] q = new FrE[domainSize];
        for (int i = 0; i < domainSize; i++)
        {
            FrE x = f.Evaluations[i] - y;
            FrE zz = domain[i] - z;
            q[i] = x / zz;
        }

        return q;
    }
}
