using System.Linq;
using System.Text.RegularExpressions;

namespace StrToolkit.Solvers;

public sealed class SqlExtractSolver : ISolver
{
    public string Name => "sql-extract";
    public string Describe => "搭配datagrip,从sql中提取数据";
    public string? NextStep => "sort-distinct";

    public int Check(string logs, string[] arr, bool jsonFlag)
    {
        if (jsonFlag)
        {
            return 0;
        }
        return arr.All(x => x.StartsWith("UPDATE") || x.StartsWith("INSERT ")) ? 300 : 0;
    }

    public string Transfer(string logs, string[] arr, bool jsonFlag)
    {
        string[] result;
        if (arr.All(x => Regex.IsMatch(x, " SET  WHERE ")))
        {
            result = arr.Select(x =>
                Regex.Replace(
                    Regex.Replace(
                        Regex.Replace(x, "^UPDATE.+?= ", ""),
                        ";$", ""),
                    "^'|^`|'$|`$", "")).ToArray();
        }
        else if (arr.All(x => x.StartsWith("INSERT ")))
        {
            result = arr.Select(x =>
                Regex.Replace(
                    Regex.Replace(
                        Regex.Replace(x, @"^.+\) VALUES \(", ""),
                        @"\);$", ""),
                    "^'|'$", "")).ToArray();
        }
        else
        {
            result = arr.Select(x =>
                Regex.Replace(
                    Regex.Replace(
                        Regex.Replace(
                            Regex.Replace(x, "^UPDATE .+?= ", ""),
                            ";$", ""),
                        " where.+", "", RegexOptions.IgnoreCase),
                    "^'|^`|'$|`$", "")).ToArray();
        }
        return string.Join("\n", result);
    }
}
