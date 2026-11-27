using System.Globalization;
using System.IO.Compression;
using System.Text;
using GranitDB.API.Exceptions;

namespace GranitDB.API.Connectors.H2;

/// <summary>
///     Lit, dans un fichier H2 (<c>.mv.db</c>, stockage « MVStore »), les instructions SQL qui définissent la base : H2 les
///     garde dans sa table système (carte <c>table.0</c>). Rien n'est exécuté et le fichier n'est ouvert qu'en lecture.
///     <para>
///         Structure suivie : des « chunks » alignés sur des blocs de 4 Ko ; le plus récent désigne la racine de la carte
///         <c>layout</c> (emplacement des chunks et racines des cartes), puis la carte <c>meta</c> (nom → numéro de carte),
///         puis les pages de <c>table.0</c>. Formats 2 et 3 (H2 2.x).
///     </para>
///     <para>Le fichier vient d'un utilisateur : tailles, profondeur et nombre de pages sont bornés.</para>
/// </summary>
internal sealed class H2Store : IDisposable
{
    private const int BlockSize = 4096;
    private const int MaxPageBytes = 32 * 1024 * 1024;
    private const int MaxPages = 100_000;
    private const int MaxDepth = 32;

    private readonly FileStream _file;
    private readonly Dictionary<long, long> _chunkOffsets = [];
    private const long MaxTotalBytes = 256L * 1024 * 1024;

    private int _pagesRead;
    private long _bytesRead;

    private H2Store(FileStream file) => _file = file;

    public void Dispose() => _file.Dispose();

    /// <summary>Les instructions SQL de la base, dans l'ordre de la table système.</summary>
    public static List<string> ReadStatements(string path, CancellationToken ct = default)
    {
        using var store = new H2Store(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
        store.CheckHeader();
        try
        {
            return store.Read(ct);
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or EndOfStreamException or
                                      FormatException or OverflowException or KeyNotFoundException or InvalidDataException)
        {
            throw Damaged(e);
        }
    }

    private static InvalidSourceException Damaged(Exception? inner = null) => new(
        "Fichier H2 illisible : il est incomplet, endommagé ou d'une structure non prise en charge. Fermez l'application qui l'utilise avant de le copier.",
        inner ?? new InvalidDataException());

    private void CheckHeader()
    {
        var header = Encoding.Latin1.GetString(ReadAt(0, (int)Math.Min(BlockSize, _file.Length)));
        if (header.StartsWith("H2encrypt", StringComparison.Ordinal))
            throw new InvalidSourceException("Base H2 chiffrée (CIPHER) : elle n'est pas prise en charge.");
        if (!header.StartsWith("H:2,", StringComparison.Ordinal))
            throw new InvalidSourceException("Le fichier n'est pas une base H2 (.mv.db) (en-tête invalide).");
        var values = Values(header.Split('\n')[0]);
        var format = values.TryGetValue("format", out var f) ? Hex(f) : 1;
        if (format is < 2 or > 3)
            throw new InvalidSourceException(format < 2
                ? "Ce fichier vient de H2 1.x, qui n'est pas pris en charge : convertissez-le avec H2 2.x (SCRIPT puis RUNSCRIPT)."
                : $"Format de fichier H2 {format} pas encore pris en charge (formats 2 et 3 : H2 2.x).");
    }

    private record Chunk(long Id, long Version, long Offset, Dictionary<string, string> Header);

    private List<string> Read(CancellationToken ct)
    {
        var chunks = ScanChunks(ct);
        // Le chunk le plus récent d'abord ; s'il est inachevé (arrêt brutal pendant une écriture), le précédent fait foi.
        Exception? failure = null;
        foreach (var latest in chunks.OrderByDescending(c => c.Version).Take(5))
            try
            {
                return ReadFrom(latest, chunks, ct);
            }
            catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or EndOfStreamException or
                                          FormatException or OverflowException or KeyNotFoundException or InvalidDataException)
            {
                failure = e;
            }

        throw Damaged(failure);
    }

    private List<Chunk> ScanChunks(CancellationToken ct)
    {
        var chunks = new List<Chunk>();
        var buffer = new byte[BlockSize * 256];
        // Les deux premiers blocs sont l'en-tête du fichier.
        long position = BlockSize * 2;
        _file.Position = position;
        int read;
        while ((read = _file.ReadAtLeast(buffer, buffer.Length, false)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            for (var o = 0; o + 6 <= read; o += BlockSize)
            {
                var block = buffer.AsSpan(o, Math.Min(1024, read - o));
                if (!block.StartsWith("chunk:"u8)) continue;
                var end = block.IndexOf((byte)'\n');
                if (end < 0) continue;
                var header = Values(Encoding.Latin1.GetString(block[..end]).Trim());
                if (header.ContainsKey("version") && header.ContainsKey("root"))
                    chunks.Add(new Chunk(Hex(header["chunk"]), Hex(header["version"]), position + o, header));
            }

            position += read;
        }

        return chunks;
    }

    private List<string> ReadFrom(Chunk latest, List<Chunk> chunks, CancellationToken ct)
    {
        _chunkOffsets.Clear();
        _pagesRead = 0;
        _bytesRead = 0;
        foreach (var chunk in chunks.Where(c => c.Version <= latest.Version).OrderBy(c => c.Version))
            _chunkOffsets[chunk.Id] = chunk.Offset;

        // La carte « layout » dit où se trouve chaque chunk encore utile : elle l'emporte sur le balayage.
        var layout = ReadStringMap(Position(latest.Header["root"]), ct);
        foreach (var (key, value) in layout)
        {
            if (!key.StartsWith("chunk.", StringComparison.Ordinal)) continue;
            var id = Hex(key[6..]);
            if (id != latest.Id && Values(value).TryGetValue("block", out var block))
                _chunkOffsets[id] = Hex(block) * BlockSize;
        }

        var meta = ReadStringMap(Position(layout["root." + layout["meta.id"]]), ct);
        var tableMap = meta["name.table.0"];
        var statements = new SortedDictionary<long, string>();
        // Une carte vide n'a pas de racine.
        if (layout.TryGetValue("root." + tableMap, out var root))
            Walk(Position(root), 0, ct, page => ReadSystemRows(page, statements));
        return statements.Values.ToList();
    }

    private sealed class Page(byte[] data, int keyCount)
    {
        public readonly H2Buffer Data = new(data);
        public readonly int KeyCount = keyCount;
    }

    private static ulong Position(string hex) => ulong.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    /// <summary>Parcourt un arbre de pages et appelle <paramref name="leaf" /> sur chaque feuille, dans l'ordre des clés.</summary>
    private void Walk(ulong position, int depth, CancellationToken ct, Action<Page> leaf)
    {
        ct.ThrowIfCancellationRequested();
        if (depth > MaxDepth || ++_pagesRead > MaxPages) throw new InvalidDataException("Trop de pages.");

        // Position d'une page : chunk (26 bits), décalage dans le chunk (32 bits), classe de taille (5 bits), type (1 bit).
        var start = _chunkOffsets[(long)(position >> 38)] + (long)((position >> 6) & 0xFFFF_FFFF);
        var length = new H2Buffer(ReadAt(start, 4)).Int32();
        if (length is < 8 or > MaxPageBytes) throw new InvalidDataException("Taille de page invalide.");
        var buffer = new H2Buffer(ReadAt(start, length));
        buffer.Skip(4 + 2); // longueur, somme de contrôle

        // Numéro de page, numéro de carte, nombre de clés : le numéro de page existe dans les formats 2 et 3.
        buffer.VarLong();
        buffer.VarLong();
        var keys = checked((int)buffer.VarLong());
        if (keys < 0 || keys > length) throw new InvalidDataException("Nombre de clés invalide.");
        int type = buffer.Byte();

        if ((type & 1) != 0)
        {
            var children = new ulong[keys + 1];
            for (var i = 0; i < children.Length; i++) children[i] = (ulong)buffer.Int64();
            foreach (var child in children) Walk(child, depth + 1, ct, leaf);
            return;
        }

        var content = (type & 2) != 0 ? Decompress(buffer, (type & 6) == 6) : buffer.Rest();
        // Borne la mémoire qu'un fichier forgé pourrait réclamer (pages compressées).
        if ((_bytesRead += content.Length) > MaxTotalBytes) throw new InvalidDataException("Contenu trop volumineux.");
        leaf(new Page(content, keys));
    }

    private static byte[] Decompress(H2Buffer buffer, bool deflate)
    {
        var added = buffer.VarLong();
        var compressed = buffer.Rest();
        var size = compressed.Length + added;
        if (size < 0 || size > MaxPageBytes) throw new InvalidDataException("Taille décompressée invalide.");
        var result = new byte[size];
        if (deflate)
        {
            using var stream = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress);
            stream.ReadExactly(result);
        }
        else Lzf(compressed, result);

        return result;
    }

    private static void Lzf(byte[] source, byte[] target)
    {
        int i = 0, o = 0;
        while (o < target.Length)
        {
            int control = source[i++];
            if (control < 32)
            {
                // Suite d'octets recopiés tels quels.
                Array.Copy(source, i, target, o, control + 1);
                i += control + 1;
                o += control + 1;
                continue;
            }

            var length = control >> 5;
            var reference = o - ((control & 31) << 8) - 1;
            if (length == 7) length += source[i++];
            reference -= source[i++];
            // Les deux zones peuvent se chevaucher : copie octet par octet.
            for (var k = 0; k < length + 2; k++) target[o++] = target[reference++];
        }
    }

    /// <summary>Feuille d'une carte texte → texte : toutes les clés, puis toutes les valeurs.</summary>
    private Dictionary<string, string> ReadStringMap(ulong root, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        Walk(root, 0, ct, page =>
        {
            var keys = new string[page.KeyCount];
            for (var i = 0; i < keys.Length; i++) keys[i] = page.Data.String();
            foreach (var key in keys) map[key] = page.Data.String();
        });
        return map;
    }

    /// <summary>
    ///     Feuille de la table système : les clés (identifiants), puis les lignes <c>ID, HEAD, TYPE, SQL</c>. Un octet initial dit
    ///     si toutes sont validées ; sinon chacune porte son état de transaction.
    /// </summary>
    private static void ReadSystemRows(Page page, SortedDictionary<long, string> statements)
    {
        var keys = new long[page.KeyCount];
        for (var i = 0; i < keys.Length; i++) keys[i] = page.Data.VarLong();
        var allCommitted = page.Data.Byte() == 0;
        foreach (var key in keys)
        {
            string? sql;
            if (allCommitted || page.Data.VarLong() == 0) sql = ReadRow(page.Data);
            else
            {
                // Ligne d'une transaction en cours : la valeur validée l'emporte sur la valeur en cours.
                int flags = page.Data.Byte();
                var current = (flags & 1) != 0 ? ReadRow(page.Data) : null;
                var committed = (flags & 2) != 0 ? ReadRow(page.Data) : null;
                sql = committed ?? current;
            }

            if (sql != null) statements[key] = sql;
        }
    }

    private static string? ReadRow(H2Buffer data)
    {
        string? sql = null;
        for (var column = 0; column < 4; column++) sql = data.Value();
        return sql;
    }

    private byte[] ReadAt(long position, int length)
    {
        if (position < 0 || position + length > _file.Length) throw new EndOfStreamException();
        var bytes = new byte[length];
        _file.Position = position;
        _file.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>Les en-têtes de H2 : <c>clé:valeur</c> séparés par des virgules, nombres en hexadécimal.</summary>
    private static Dictionary<string, string> Values(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in text.Split(','))
        {
            var colon = part.IndexOf(':');
            if (colon > 0) values[part[..colon]] = part[(colon + 1)..].Trim();
        }

        return values;
    }

    private static long Hex(string value) => long.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
