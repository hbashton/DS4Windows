using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DS4Windows;

internal sealed record LegacyJoyConSavedPair(string Left, string Right, bool PreferLeft);

/// <summary>Explicit original Joy-Con links, independent of Bluetooth bonds.</summary>
internal sealed class LegacyJoyConPairStore
{
    private readonly string path;
    private LegacyJoyConSavedPair[] pairs = Array.Empty<LegacyJoyConSavedPair>();
    private Exception readFailure;

    internal LegacyJoyConPairStore(string path)
    {
        this.path = path;
        try
        {
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length > 128 * 1024) throw new InvalidDataException("The saved Joy-Con links file is too large.");
            var loaded = JsonSerializer.Deserialize<LegacyJoyConSavedPair[]>(File.ReadAllText(path));
            if (loaded == null || loaded.Length > 128) throw new InvalidDataException("The saved Joy-Con links are invalid.");
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in loaded)
                if (pair == null || !ValidIdentity(pair.Left) || !ValidIdentity(pair.Right) ||
                    !identities.Add(pair.Left) || !identities.Add(pair.Right))
                    throw new InvalidDataException("The saved Joy-Con links contain duplicate or invalid controller identities.");
            pairs = loaded;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            // A damaged settings file must not prevent input/discovery. Do not
            // overwrite it with an empty list on the user's next Link click.
            readFailure = exception;
        }
    }

    internal IReadOnlyList<LegacyJoyConSavedPair> Pairs => pairs;
    internal void Remember(string left, string right, bool preferLeft)
    {
        if (!ValidIdentity(left) || !ValidIdentity(right) || left == right)
            throw new InvalidDataException("Both Joy-Cons need distinct stable identities to remember a link.");
        var next = pairs.Where(pair => pair.Left != left && pair.Right != left &&
            pair.Left != right && pair.Right != right).Append(new(left, right, preferLeft)).ToArray();
        Write(next);
    }

    internal void Forget(string left, string right)
    {
        // Session-only links have no persisted association to remove. They
        // must remain unlinkable if loading/saving the optional store failed.
        if (!pairs.Any(pair => pair.Left == left && pair.Right == right)) return;
        Write(pairs.Where(pair => pair.Left != left || pair.Right != right).ToArray());
    }

    private void Write(LegacyJoyConSavedPair[] next)
    {
        if (readFailure != null) throw new InvalidDataException("Saved Joy-Con links could not be read; the original file was kept.", readFailure);
        if (next.Length > 128) throw new InvalidDataException("Too many saved Joy-Con links.");
        string directory = Path.GetDirectoryName(path);
        Directory.CreateDirectory(directory);
        PortableLabContext.ValidateNoReparsePoints(directory);
        string temporary = Path.Combine(directory, ".joycon-links-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, next);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            pairs = next;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool ValidIdentity(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 &&
        value.All(character => !char.IsControl(character));
}
