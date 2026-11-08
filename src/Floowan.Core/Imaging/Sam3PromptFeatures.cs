using System.Text;

namespace Floowan.Core.Imaging;

/// <summary>
/// Precomputed SAM 3 text features for the fixed cutout prompts, so the app needs neither the
/// SAM 3 text encoder (~700 MB) nor a CLIP tokenizer. Embedded from
/// <c>Resources/sam3/prompt_features.bin</c>, built by <c>scripts/sam3/make_prompt_features.py</c>.
/// Derived from Meta's SAM 3 weights (SAM License, see <c>SAM_LICENSE.txt</c>).
/// </summary>
public sealed class Sam3PromptFeatures
{
    public const string ResourceName = "Floowan.Core.Resources.sam3.prompt_features.bin";

    private static readonly Lazy<Sam3PromptFeatures> EmbeddedInstance = new(LoadEmbedded);

    private readonly Dictionary<string, (float[] Features, long[] Mask)> _prompts;

    private Sam3PromptFeatures(int sequenceLength, int dimension, Dictionary<string, (float[], long[])> prompts)
    {
        SequenceLength = sequenceLength;
        Dimension = dimension;
        _prompts = prompts;
    }

    public static Sam3PromptFeatures Embedded => EmbeddedInstance.Value;

    public int SequenceLength { get; }
    public int Dimension { get; }
    public IReadOnlyCollection<string> Prompts => _prompts.Keys;

    public bool Contains(string prompt) => _prompts.ContainsKey(prompt);

    /// <summary>Text features <c>[SequenceLength × Dimension]</c> and attention mask <c>[SequenceLength]</c>.</summary>
    public (float[] Features, long[] Mask) Get(string prompt) =>
        _prompts.TryGetValue(prompt, out var value)
            ? value
            : throw new KeyNotFoundException($"SAM 3 prompt '{prompt}' is not in the precomputed feature set.");

    public static Sam3PromptFeatures Read(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var magic = reader.ReadBytes(4);
        if (magic.Length != 4 || magic[0] != (byte)'S' || magic[1] != (byte)'3' || magic[2] != (byte)'P' || magic[3] != (byte)'F')
            throw new InvalidDataException("Not a SAM 3 prompt feature file.");
        var version = reader.ReadInt32();
        if (version != 1)
            throw new InvalidDataException($"Unsupported SAM 3 prompt feature version {version}.");
        var count = reader.ReadInt32();
        var seqLen = reader.ReadInt32();
        var dim = reader.ReadInt32();
        if (count <= 0 || seqLen <= 0 || dim <= 0)
            throw new InvalidDataException("SAM 3 prompt feature header is invalid.");

        var prompts = new Dictionary<string, (float[], long[])>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var nameLength = reader.ReadInt32();
            var name = Encoding.UTF8.GetString(reader.ReadBytes(nameLength));
            var mask = new long[seqLen];
            for (var j = 0; j < seqLen; j++)
                mask[j] = reader.ReadInt64();
            var features = new float[seqLen * dim];
            for (var j = 0; j < features.Length; j++)
                features[j] = reader.ReadSingle();
            prompts[name] = (features, mask);
        }

        return new Sam3PromptFeatures(seqLen, dim, prompts);
    }

    private static Sam3PromptFeatures LoadEmbedded()
    {
        using var stream = typeof(Sam3PromptFeatures).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
        return Read(stream);
    }
}
