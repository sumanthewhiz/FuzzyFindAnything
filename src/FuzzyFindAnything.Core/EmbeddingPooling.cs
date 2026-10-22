namespace FuzzyFindAnything.Core;

public static class EmbeddingPooling
{
    public static float[][] Normalize(ReadOnlySpan<float> values, ReadOnlySpan<long> mask,
        int batch, int sequence, int dimensions, string strategy)
    {
        if (batch < 1 || sequence < 1 || dimensions < 1 || mask.Length != checked(batch * sequence) ||
            strategy is not ("mean" or "cls" or "last-token" or "pooled") ||
            values.Length != checked(batch * dimensions * (strategy == "pooled" ? 1 : sequence)))
            throw new InvalidDataException("The native embedding output has incompatible dimensions or pooling.");
        var result = new float[batch][];
        for (int row = 0; row < batch; row++)
        {
            int first = -1, last = -1, count = 0;
            for (int token = 0; token < sequence; token++)
            {
                long present = mask[row * sequence + token];
                if (present is not (0 or 1)) throw new InvalidDataException("Invalid embedding attention mask.");
                if (present == 0) continue;
                if (first < 0) first = token;
                last = token; count++;
            }
            if (count == 0) throw new InvalidDataException("An embedding input contains no unmasked tokens.");
            if (strategy == "pooled") result[row] = ModelContract.Normalize(values.Slice(row * dimensions, dimensions), dimensions);
            else if (strategy is "cls" or "last-token")
                result[row] = ModelContract.Normalize(values.Slice((row * sequence + (strategy == "cls" ? first : last)) * dimensions, dimensions), dimensions);
            else
            {
                float[] pooled = new float[dimensions];
                for (int column = 0; column < dimensions; column++)
                {
                    double sum = 0;
                    for (int token = 0; token < sequence; token++)
                        if (mask[row * sequence + token] != 0) sum += values[(row * sequence + token) * dimensions + column];
                    pooled[column] = (float)(sum / count);
                }
                result[row] = ModelContract.Normalize(pooled, dimensions);
            }
        }
        return result;
    }
}
