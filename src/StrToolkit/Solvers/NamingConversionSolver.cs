using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace StrToolkit.Solvers;

public sealed class NamingConversionSolver : ISolver
{
    public string Name => "naming-conversion";
    public string Describe => "命名规则转换";
    public string? NextStep => null;

    public int Check(string logs, string[] arr, bool jsonFlag)
    {
        if (jsonFlag)
        {
            return 0;
        }
        return arr.All(x => Regex.IsMatch(x, @"^[a-zA-Z\-_]+$")) ? 150 : 0;
    }

    public string Transfer(string logs, string[] arr, bool jsonFlag)
    {
        int style = DetectStyle(arr);
        return style switch
        {
            // 下划线/中划线 -> 驼峰
            0 => string.Join("\n", arr.Select(x =>
            {
                var words = x.ToLowerInvariant().Split('-', '_');
                return string.Concat(words.Select((w, i) =>
                    i == 0 ? w.ToLowerInvariant() : Capitalize(w)));
            })),
            // 帕斯卡 -> 下划线
            1 => string.Join("\n", arr.Select(x =>
            {
                var separated = Regex.Replace(x, "([A-Z])", m => "_" + m.Value);
                var pascal = string.Concat(separated.Split('-', '_').Select(Capitalize));
                var snake = Regex.Replace(pascal, "([A-Z])", m => "_" + m.Value.ToLowerInvariant());
                return snake.Length > 0 ? snake[1..] : snake;
            })),
            // 驼峰/下划线 -> 帕斯卡
            _ => string.Join("\n", arr.Select(x =>
                string.Concat(x.Split('-', '_').Select(w =>
                    w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]))))
        };
    }

    private static string Capitalize(string word) =>
        word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant();

    private static int DetectStyle(string[] arr)
    {
        // 0: 下划线->驼峰  1: 帕斯卡->下划线  2: 驼峰->帕斯卡
        int[] countIndex = { 0, 0, 0 };
        foreach (var x in arr)
        {
            if (x.Contains('-') || x.Contains('_'))
            {
                countIndex[0]++;
            }
            else if (x.Length > 0 && char.IsUpper(x[0]))
            {
                countIndex[1]++;
            }
            else if (x.ToUpperInvariant() != x)
            {
                countIndex[2]++;
            }
            else
            {
                countIndex[2]++;
                countIndex[0]++;
            }
        }
        int max = countIndex[0], index = 0;
        for (int i = 0; i < countIndex.Length; i++)
        {
            if (max < countIndex[i])
            {
                max = countIndex[i];
                index = i;
            }
        }
        return index;
    }
}
