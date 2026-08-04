namespace WszSearcher.Core;

/// <summary>
/// 路径过滤公共逻辑——目录黑名单 + 用户 ExcludePaths 模式匹配。
/// 文件名搜索（USN/FSW）与内容索引（watcher/扫描）共用，保证屏蔽规则一致
/// </summary>
public static class PathFilter
{
    // 目录黑名单：命中即不索引（避免 C 盘全盘事件风暴/垃圾目录污染索引）
    private static readonly HashSet<string> SkipDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "bin", "obj", "packages",
        "vendor", "__pycache__", "target", "build", "dist",
        "bower_components", ".vs", ".vscode", ".idea"
    };

    /// <summary>
    /// 路径是否命中排除规则（黑名单目录段 / 点开头目录 / 用户 ExcludePaths 模式）
    /// 逐段匹配：路径中任意一段命中即排除
    /// </summary>
    public static bool IsExcluded(string path, IEnumerable<string> excludePatterns)
    {
        var segments = path.Split(Path.DirectorySeparatorChar);
        foreach (var seg in segments)
        {
            if (seg.Length > 0 && seg[0] == '.') return true; // .git/.vscode 等点开头目录
            if (SkipDirNames.Contains(seg)) return true;      // node_modules/bin/obj 等黑名单
            foreach (var pat in excludePatterns)
            {
                var name = pat.Trim('*', '\\', '/'); // "*\node_modules" → "node_modules"
                if (name.Length > 0 && name.Equals(seg, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }
}
