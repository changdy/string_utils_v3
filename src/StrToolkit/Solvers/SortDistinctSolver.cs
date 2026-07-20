using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace StrToolkit.Solvers;

public sealed class SortDistinctSolver : ISolver
{
    public string Name => "sort-distinct";
    public string Describe => "排序&去重";
    public string? NextStep => "id-join";

    public int Check(string logs, string[] arr, bool jsonFlag)
    {
        if (jsonFlag)
        {
            return 0;
        }
        int setSize = new HashSet<string>(arr).Count;
        return (arr.Length == setSize || arr.Length == 1) ? 80 : 120;
    }

    public string Transfer(string logs, string[] arr, bool jsonFlag)
    {
        var tempArr = arr.Distinct().ToList();
        var numbers = new List<decimal>(tempArr.Count);
        bool allNumeric = true;
        foreach (var item in tempArr)
        {
            if (decimal.TryParse(item.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                numbers.Add(value);
            }
            else
            {
                allNumeric = false;
                break;
            }
        }

        if (allNumeric && tempArr.Count > 0)
        {
            var pairs = tempArr.Zip(numbers, (text, num) => (text, num)).ToList();
            pairs.Sort((a, b) => a.num.CompareTo(b.num));
            return string.Join("\n", pairs.Select(p => p.num.ToString(CultureInfo.InvariantCulture)));
        }

        tempArr.Sort((a, b) => string.CompareOrdinal(a.ToLowerInvariant(), b.ToLowerInvariant()));
        return string.Join("\n", tempArr);
    }
}
