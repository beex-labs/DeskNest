using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace BeeX.OCR;

/// <summary>
/// Decoder for the PP-FormulaNet (UniMERNet/Nougat family) tokenizer.
/// The vocabulary and special tokens are embedded in inference.yml in the model directory; tokens use GPT-2 style ByteLevel BPE representation.
/// </summary>
internal sealed partial class FormulaTokenizer
{
    private const long EosTokenId = 2;
    private static readonly Dictionary<char, byte> ByteDecoder = BuildByteDecoder();

    private readonly Dictionary<long, string> _idToToken;
    private readonly HashSet<long> _specialIds;

    private FormulaTokenizer(Dictionary<long, string> idToToken, HashSet<long> specialIds)
    {
        _idToToken = idToToken;
        _specialIds = specialIds;
    }

    public static FormulaTokenizer Load(string modelDirectory)
    {
        string ymlPath = Path.Combine(modelDirectory, "inference.yml");
        if (!File.Exists(ymlPath))
        {
            throw new InvalidOperationException("公式模型配置缺失：" + ymlPath);
        }

        using var reader = new StreamReader(ymlPath, Encoding.UTF8);
        var stream = new YamlStream();
        stream.Load(reader);

        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        YamlMappingNode fastTokenizer = GetMap(GetMap(GetMap(root, "PostProcess"), "character_dict"), "fast_tokenizer_file");
        YamlMappingNode vocab = GetMap(GetMap(fastTokenizer, "model"), "vocab");

        var idToToken = new Dictionary<long, string>(vocab.Children.Count + 16);
        foreach (KeyValuePair<YamlNode, YamlNode> pair in vocab.Children)
        {
            string token = ((YamlScalarNode)pair.Key).Value ?? string.Empty;
            long id = long.Parse(((YamlScalarNode)pair.Value).Value ?? "-1");
            idToToken[id] = token;
        }

        var specialIds = new HashSet<long>();
        if (fastTokenizer.Children.TryGetValue(new YamlScalarNode("added_tokens"), out YamlNode? added) &&
            added is YamlSequenceNode addedTokens)
        {
            foreach (YamlNode item in addedTokens)
            {
                if (item is not YamlMappingNode map)
                {
                    continue;
                }

                long id = long.Parse(GetScalar(map, "id") ?? "-1");
                string content = GetScalar(map, "content") ?? string.Empty;
                idToToken.TryAdd(id, content);

                if (string.Equals(GetScalar(map, "special"), "true", StringComparison.OrdinalIgnoreCase))
                {
                    specialIds.Add(id);
                }
            }
        }

        if (idToToken.Count == 0)
        {
            throw new InvalidOperationException("公式模型 tokenizer 词表为空：" + ymlPath);
        }

        return new FormulaTokenizer(idToToken, specialIds);
    }

    public string Decode(IReadOnlyList<long> tokenIds)
    {
        var bytes = new List<byte>(tokenIds.Count * 4);
        foreach (long id in tokenIds)
        {
            if (id == EosTokenId)
            {
                break;
            }

            if (_specialIds.Contains(id) || !_idToToken.TryGetValue(id, out string? token))
            {
                continue;
            }

            foreach (char c in token)
            {
                if (ByteDecoder.TryGetValue(c, out byte value))
                {
                    bytes.Add(value);
                }
                else
                {
                    bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
                }
            }
        }

        string text = Encoding.UTF8.GetString(bytes.ToArray());
        return PostProcess(text);
    }

    private static YamlMappingNode GetMap(YamlMappingNode parent, string key)
    {
        if (!parent.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? node) || node is not YamlMappingNode map)
        {
            throw new InvalidOperationException("公式模型配置缺少节点：" + key);
        }

        return map;
    }

    private static string? GetScalar(YamlMappingNode map, string key)
    {
        return map.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? node) && node is YamlScalarNode scalar
            ? scalar.Value
            : null;
    }

    /// <summary>Inverse of GPT-2 bytes_to_unicode: token char -> original byte.</summary>
    private static Dictionary<char, byte> BuildByteDecoder()
    {
        var byteToChar = new Dictionary<byte, char>(256);
        foreach ((int start, int end) in new[] { (0x21, 0x7e), (0xa1, 0xac), (0xae, 0xff) })
        {
            for (int b = start; b <= end; b++)
            {
                byteToChar[(byte)b] = (char)b;
            }
        }

        int offset = 0;
        for (int b = 0; b <= 255; b++)
        {
            if (!byteToChar.ContainsKey((byte)b))
            {
                byteToChar[(byte)b] = (char)(256 + offset);
                offset++;
            }
        }

        var decoder = new Dictionary<char, byte>(256);
        foreach (KeyValuePair<byte, char> pair in byteToChar)
        {
            decoder[pair.Value] = pair.Key;
        }

        return decoder;
    }

    /// <summary>Aligns the core behavior of PaddleX UniMERNetDecode.post_process: removes the Chinese text wrapper and compresses leftover BPE spaces;
    /// spaces inside text groups like \text/\operatorname must be preserved (otherwise \text{hello world} becomes helloworld).</summary>
    private static string PostProcess(string text)
    {
        text = ChineseTextWrapPattern().Replace(text, match => match.Groups[1].Value).Replace("\"", "");
        text = text.Trim();

        // First replace whole text groups with placeholders, protecting inner spaces from the space compression below
        var protectedGroups = new List<string>();
        text = TextGroupPattern().Replace(text, match =>
        {
            protectedGroups.Add(match.Value);
            return "\uE000" + (protectedGroups.Count - 1) + "\uE001";
        });

        string current = text;
        while (true)
        {
            string next = NoLetterGapPattern().Replace(current, "$1$2");
            next = NoLetterLetterGapPattern().Replace(next, "$1$2");
            next = LetterNoLetterGapPattern().Replace(next, "$1$2");
            if (next == current)
            {
                break;
            }

            current = next;
        }

        return PlaceholderPattern().Replace(current, match => protectedGroups[int.Parse(match.Groups[1].Value)]);
    }

    [GeneratedRegex(@"\\(?:text|operatorname|mathrm|mathbf)\s*\*?\s*\{[^{}]*\}")]
    private static partial Regex TextGroupPattern();

    [GeneratedRegex("\uE000(\\d+)\uE001")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"\\text\s*\{([^{}]*[\u4e00-\u9fff]+[^{}]*)\}")]
    private static partial Regex ChineseTextWrapPattern();

    [GeneratedRegex(@"(?!\\ )([\W_^\d])\s+?([\W_^\d])")]
    private static partial Regex NoLetterGapPattern();

    [GeneratedRegex(@"(?!\\ )([\W_^\d])\s+?([a-zA-Z])")]
    private static partial Regex NoLetterLetterGapPattern();

    [GeneratedRegex(@"([a-zA-Z])\s+?([\W_^\d])")]
    private static partial Regex LetterNoLetterGapPattern();
}
