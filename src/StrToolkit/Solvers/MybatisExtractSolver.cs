using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using StrToolkit.Services;

namespace StrToolkit.Solvers;

/// <summary>MyBatis 注解提取 + 日志解析，逻辑与 Electron 版 mybatis-extract.js 对齐。</summary>
public sealed class MybatisExtractSolver : ISolver
{
    private static readonly Regex[] LogTypeArr =
    {
        new(@"\WDEBUG\W"), new(@"\WINFO\W"), new(@"\WTRACE\W"), new(@"\WWARN\W"), new(@"\WERROR\W")
    };

    private static readonly string[] NativeArr =
        "(Byte),(Float),(Long),(Short),(Double),(Integer),(Boolean),(BigDecimal)".Split(',');

    private static readonly string[] StringArr =
        "(String),(StringReader),(Timestamp),(LocalDate)".Split(',');

    private static readonly Regex MybatisReg = new("@Select|@Update|@Delete|@Insert");
    private static readonly Regex AnnotationOpenReg = new(@"@Select\(|@Update\(|@Delete\(|@Insert\(");
    private static readonly Regex ParametersReg = new("=> +Parameters: ");
    private static readonly Regex PreparingReg = new("=> +Preparing: ");

    public string Name => "mybatis-extract";
    public string Describe => "mybatis日志解析";
    public string? NextStep => null;

    public int Check(string logs, string[] arr, bool jsonFlag)
    {
        if (jsonFlag)
        {
            return 0;
        }
        if (MybatisReg.IsMatch(logs))
        {
            return 300;
        }
        return ParametersReg.IsMatch(logs) ? 200 : 0;
    }

    public string Transfer(string logs, string[] logArr, bool jsonFlag)
    {
        if (MybatisReg.IsMatch(logs))
        {
            logs = AnnotationOpenReg.Replace(logs, "", 1);
            int lastParen = logs.LastIndexOf(')');
            string s = (lastParen >= 0 ? logs[..lastParen] : logs).Trim();
            try
            {
                return ExtractMybatisAnnotationString(s);
            }
            catch (Exception e)
            {
                // 注解解析失败时回退原文，同时把原因打到 VS 调试输出
                AppLog.Error(
                    $"mybatis 注解解析失败，已回退原文: inputLen={s.Length}, preview={Preview(s)}",
                    e);
                return s;
            }
        }

        var lines = (string[])logArr.Clone();
        var resultArr = new List<MybatisLog>();
        for (int index = lines.Length - 1; index > 0; index--)
        {
            if (Regex.IsMatch(lines[index], @"\(.+?\)$") && LogTypeArr.All(x => !x.IsMatch(lines[index])))
            {
                lines[index - 1] = lines[index - 1] + "\n" + lines[index];
                lines[index] = "";
            }
        }

        foreach (var element in lines)
        {
            if (ParametersReg.IsMatch(element))
            {
                var arr = ParseParamLog(element);
                if (arr.Count > 0)
                {
                    resultArr.Add(new MybatisLog(true, arr));
                }
            }
            else if (PreparingReg.IsMatch(element))
            {
                var arr = Regex.Replace(element, ".+=> +Preparing: ", "").Split('?').ToList();
                resultArr.Add(new MybatisLog(false, arr));
            }
        }

        var sqlArr = new List<string>();
        for (int index = 0; index < resultArr.Count; index++)
        {
            var element = resultArr[index];
            if (!element.IsParam)
            {
                sqlArr.Add(element.Length == 1 ? element.Arr[0] : GetRealSql(resultArr, index));
            }
        }
        return string.Join("\n", sqlArr);
    }

    private static string ExtractMybatisAnnotationString(string expression)
    {
        string source = expression.Trim();
        var result = new StringBuilder();
        int index = 0;
        bool foundLiteral = false;
        while (index < source.Length)
        {
            char c = source[index];
            if (char.IsWhiteSpace(c) || c == '+' || c == ',' || c == '{' || c == '}')
            {
                index++;
                continue;
            }
            if (c != '"' && c != '\'')
            {
                throw new FormatException($"Unsupported MyBatis annotation token: {c}");
            }
            foundLiteral = true;
            char quote = c;
            index++;
            var value = new StringBuilder();
            bool closed = false;
            while (index < source.Length)
            {
                char current = source[index];
                if (current == '\\')
                {
                    if (index + 1 >= source.Length)
                    {
                        throw new FormatException("Invalid MyBatis annotation escape sequence");
                    }
                    char next = source[index + 1];
                    if (next == 'u')
                    {
                        if (index + 6 > source.Length ||
                            !int.TryParse(source.Substring(index + 2, 4), NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture, out int code))
                        {
                            throw new FormatException("Invalid MyBatis annotation unicode escape");
                        }
                        value.Append((char)code);
                        index += 6;
                        continue;
                    }
                    value.Append(DecodeJavaStringEscape(next));
                    index += 2;
                    continue;
                }
                if (current == quote)
                {
                    closed = true;
                    index++;
                    break;
                }
                value.Append(current);
                index++;
            }
            if (!closed)
            {
                throw new FormatException("Unterminated MyBatis annotation string literal");
            }
            result.Append(value);
        }
        return foundLiteral ? result.ToString() : source;
    }

    private static char DecodeJavaStringEscape(char c) => c switch
    {
        'n' => '\n',
        'r' => '\r',
        't' => '\t',
        'b' => '\b',
        'f' => '\f',
        _ => c
    };

    private static string Preview(string s, int max = 120)
    {
        string oneLine = s.Replace('\r', ' ').Replace('\n', ' ');
        return oneLine.Length <= max ? oneLine : oneLine[..max] + "…";
    }

    private static List<string> ParseParamLog(string paramLog)
    {
        paramLog = Regex.Replace(paramLog, ".+=> +Parameters: ", " ");
        if (paramLog.Length <= 3)
        {
            return new List<string>();
        }
        var paramValue = new List<string>();
        var splitArr = paramLog.Split(',');
        string formerValue = "";
        foreach (var raw in splitArr)
        {
            string x = formerValue + Regex.Replace(raw, "^ ", "");
            formerValue = "";
            if (x.EndsWith("(LocalDateTime)"))
            {
                x = "'" + Regex.Replace(x.Replace("T", " "), @"\.\d+$", "") + "'";
            }
            else if (StringArr.Any(y => x.EndsWith(y)))
            {
                x = "'" + x.Replace("\n", "") + "'";
            }
            else if (x != "null" && NativeArr.All(y => !x.EndsWith(y)))
            {
                formerValue = x;
                continue;
            }
            if (x.EndsWith("'"))
            {
                paramValue.Add(Regex.Replace(x, @"\(\w+\)'$", "'"));
            }
            else
            {
                paramValue.Add(Regex.Replace(x, @"\(\w+\)$", ""));
            }
        }
        return paramValue;
    }

    private sealed class MybatisLog
    {
        public MybatisLog(bool isParam, List<string> arr)
        {
            IsParam = isParam;
            Arr = arr;
        }

        public bool IsParam { get; }
        public bool IsUsed { get; private set; }
        public List<string> Arr { get; }
        public int Length => Arr.Count;

        public void SetParamUsed() => IsUsed = true;

        public bool EffectiveParam(int length) => IsParam && !IsUsed && Arr.Count == length;
    }

    private static string GetRealSql(List<MybatisLog> resultArr, int index)
    {
        var element = resultArr[index];
        for (int j = index + 1; j < resultArr.Count; j++)
        {
            var @params = resultArr[j];
            if (@params.EffectiveParam(element.Length - 1))
            {
                var paramsArr = @params.Arr;
                var sql = new StringBuilder();
                for (int l = 0; l < paramsArr.Count; l++)
                {
                    sql.Append(element.Arr[l]).Append(paramsArr[l]);
                }
                sql.Append(element.Arr[paramsArr.Count]);
                if (!element.Arr[paramsArr.Count].Contains(';'))
                {
                    sql.Append(';');
                }
                @params.SetParamUsed();
                return sql.ToString();
            }
        }
        return string.Concat(element.Arr);
    }
}
