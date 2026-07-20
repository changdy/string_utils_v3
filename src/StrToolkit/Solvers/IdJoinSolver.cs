using System.Text.RegularExpressions;

namespace StrToolkit.Solvers;

public sealed class IdJoinSolver : ISolver
{
    public string Name => "id-join";
    public string Describe => "ID拼接";
    public string? NextStep => "id-join";

    public int Check(string logs, string[] arr, bool jsonFlag) => arr.Length > 0 && !jsonFlag ? 100 : 0;

    public string Transfer(string logs, string[] arr, bool jsonFlag)
    {
        if (arr.Length == 1)
        {
            if (logs.Contains('"'))
            {
                var replaced = Regex.Replace(logs, "\",\"", "$|^");
                int firstQuote = replaced.IndexOf('"');
                if (firstQuote >= 0)
                {
                    replaced = replaced.Remove(firstQuote, 1).Insert(firstQuote, "^");
                }
                return replaced[..^1] + "$";
            }
            if (logs.Contains('^'))
            {
                var replaced = Regex.Replace(logs, @"\$\|\^", ",");
                return replaced[1..^1];
            }
            return "\"" + logs.Replace(",", "\",\"") + "\"";
        }
        return "\"" + string.Join("\",\"", arr) + "\"";
    }
}
