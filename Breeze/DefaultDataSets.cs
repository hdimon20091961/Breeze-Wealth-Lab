using System;
using System.Collections.Generic;
using System.Linq;
using WealthLab.Core;

namespace BreezeVL;

internal static class DefaultDataSets
{
    internal static DataSet? FindIndex(IEnumerable<DataSet> dataSets, string indexName)
    {
        string key = Normalize(indexName);
        // QualifiedName is a serialized identifier (Name;Provider), not a UI label.
        // Match the two fields separately so punctuation in the label is irrelevant.
        return dataSets.FirstOrDefault(item => Normalize(item.Name) == key &&
            string.Equals(item.PreferredDataProviderName, "WealthData", StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
