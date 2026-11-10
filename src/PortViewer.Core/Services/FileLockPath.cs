namespace PortViewer.Core.Services;

public static class FileLockPath
{
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("请选择文件或文件夹。", nameof(path));
        path = path.Trim().Trim('"');
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("目前仅支持本机磁盘路径。", nameof(path));
        if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)) path = path[4..];
        if (path.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("目前仅支持本机磁盘路径。", nameof(path));
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("请输入本机磁盘的完整路径。", nameof(path));
        var full = Path.GetFullPath(path);
        if (!Path.IsPathFullyQualified(full) || full.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("请输入本机磁盘的完整路径。", nameof(path));
        return Path.TrimEndingDirectorySeparator(full);
    }

    public static bool Matches(string target, bool isDirectory, string candidate)
    {
        try
        {
            target = Normalize(target);
            candidate = Normalize(candidate);
            var prefix = Path.EndsInDirectorySeparator(target) ? target : target + Path.DirectorySeparatorChar;
            return string.Equals(target, candidate, StringComparison.OrdinalIgnoreCase) ||
                   (isDirectory && candidate.StartsWith(prefix,
                       StringComparison.OrdinalIgnoreCase));
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
    }
}
