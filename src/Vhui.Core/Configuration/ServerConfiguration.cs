using System.Globalization;

namespace Vhui.Core.Configuration;

/// <summary>
/// Перенос ReadConfigurationString (0x14070b8e0) и ReadConfigurationInteger
/// (0x14070ba90) из распакованной программы. Формат файла INI сюда не входит.
/// </summary>
public sealed class ServerConfiguration
{
    public static ServerConfiguration Load(string path)
    {
        var result = new ServerConfiguration();
        int lineNumber = 0;
        foreach (string source in File.ReadLines(path))
        {
            lineNumber++;
            string line = source.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#') || line.StartsWith('[') && line.EndsWith(']')) { continue; }
            int separator = line.IndexOf('=');
            if (separator <= 0) { throw new InvalidDataException($"Invalid configuration line {lineNumber}."); }
            result.Set(line[..separator].Trim(), line[(separator + 1)..].Trim());
        }
        return result;
    }
    public IReadOnlyDictionary<string, string> Snapshot() { lock (_sync) { return new Dictionary<string, string>(_values, StringComparer.OrdinalIgnoreCase); } }
    public void Remove(string key) { lock (_sync) { _values.Remove(key); } }
    public void Save(string path)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllLines(temporary, Snapshot().OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => pair.Key + "=" + pair.Value));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private readonly Func<string, string?> _readEnvironment;
    private readonly Action<string>? _warning;

    public ServerConfiguration(Func<string, string?>? readEnvironment = null, Action<string>? warning = null)
    {
        _readEnvironment = readEnvironment ?? Environment.GetEnvironmentVariable;
        _warning = warning;
    }

    public void Set(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        lock (_sync) { _values[key] = value; }
    }

    public string? GetString(string key, string? defaultValue = null, bool expandEnvironment = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        string? value;
        lock (_sync) { value = _values.TryGetValue(key, out string? storedValue) ? storedValue : defaultValue; }
        if (value is null || !expandEnvironment) { return value; }

        var previousValues = new HashSet<string>(StringComparer.Ordinal);
        for (int replacements = 0; replacements < 128; replacements++)
        {
            int start = value.IndexOf("${", StringComparison.Ordinal);
            int end = start < 0 ? -1 : value.IndexOf('}', start + 2);
            if (end < 0) { return value; }
            if (!previousValues.Add(value))
            {
                throw new InvalidDataException("Циклическая подстановка переменных окружения.");
            }
            string name = value[(start + 2)..end];
            string? replacement = _readEnvironment(name);
            if (replacement is null)
            {
                _warning?.Invoke($"Не найдена переменная окружения '{name}' для свойства '{key}'.");
                replacement = string.Empty;
            }
            value = value.Replace("${" + name + "}", replacement, StringComparison.Ordinal);
        }
        // В исходнике такие входные данные могли зациклить обработку.
        throw new InvalidDataException("Превышен предел вложенности переменных окружения.");
    }

    public int GetInteger(string key, int defaultValue)
        => ParseWindowsDecimalPrefix(GetString(key, defaultValue.ToString(CultureInfo.InvariantCulture))!);

    // Исходник использует Windows strtol(..., 10), а не строгий int.Parse:
    // начальные пробелы и знак разрешены, хвост после цифр игнорируется,
    // при переполнении возвращается LONG_MIN/LONG_MAX, при отсутствии цифр — 0.
    private static int ParseWindowsDecimalPrefix(string text)
    {
        int index = 0;
        while (index < text.Length && (text[index] == ' ' || text[index] is >= '\t' and <= '\r')) { index++; }
        bool negative = index < text.Length && text[index] == '-';
        if (index < text.Length && text[index] is '+' or '-') { index++; }
        long limit = negative ? 2147483648L : int.MaxValue;
        long value = 0;
        while (index < text.Length && text[index] is >= '0' and <= '9')
        {
            int digit = text[index++] - '0';
            if (value > (limit - digit) / 10) { return negative ? int.MinValue : int.MaxValue; }
            value = value * 10 + digit;
        }
        return (int)(negative ? -value : value);
    }
}
