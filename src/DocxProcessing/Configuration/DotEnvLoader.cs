namespace DocxProcessing.Configuration;

internal static class DotEnvLoader
{
    public static void Load()
    {
        var starts = new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory }
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var start in starts)
        {
            var isStart = true;
            for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
            {
                var isRepositoryRoot = File.Exists(Path.Combine(directory.FullName, "DocxProcessing.slnx"));
                var path = Path.Combine(directory.FullName, ".env");
                if ((isStart || isRepositoryRoot) && File.Exists(path)) LoadFile(path);
                if (isRepositoryRoot) return;
                isStart = false;
            }
        }
    }

    static void LoadFile(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            var entry = line.Trim();
            if (entry.Length == 0 || entry.StartsWith('#')) continue;
            if (entry.StartsWith("export ", StringComparison.Ordinal)) entry = entry[7..].TrimStart();

            var separator = entry.IndexOf('=');
            if (separator <= 0) continue;

            var name = entry[..separator].Trim();
            if (!IsValidName(name) || Environment.GetEnvironmentVariable(name) is not null) continue;

            var value = entry[(separator + 1)..].Trim();
            if (HasMatchingQuotes(value)) value = value[1..^1];
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    static bool IsValidName(string name) =>
        name.Length > 0 &&
        !char.IsDigit(name[0]) &&
        name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    static bool HasMatchingQuotes(string value) =>
        value.Length >= 2 &&
        ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''));
}
