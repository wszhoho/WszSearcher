using ToolGood.Words;

namespace WszSearcher.Core.Analysis;

/// <summary>拼音首字母转换工具</summary>
public static class PinyinHelper
{
    private static bool _initFailed;

    /// <summary>获取文本的拼音首字母（中文→首字母，英文/数字原样保留）</summary>
    public static string GetFirstLetters(string text)
    {
        if (string.IsNullOrEmpty(text) || _initFailed) return "";
        try
        {
            return WordsHelper.GetFirstPinyin(text) ?? "";
        }
        catch
        {
            _initFailed = true;
            return "";
        }
    }

    public static string GetPinyin(string text)
    {
        if (string.IsNullOrEmpty(text) || _initFailed) return "";
        try
        {
            var result = WordsHelper.GetPinyin(text) ?? "";
            return result;
        }
        catch { return ""; }
    }
}
