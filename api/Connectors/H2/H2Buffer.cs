using System.Buffers.Binary;
using System.Text;

namespace GranitDB.API.Connectors.H2;

/// <summary>Lecture séquentielle des encodages de H2 ; tout dépassement lève une exception.</summary>
internal sealed class H2Buffer(byte[] data)
{
    // Codes de type des valeurs sérialisées par H2 (ValueDataType) utiles à la table système (entiers et texte).
    private const int Null = 0;
    private const int Integer = 4;
    private const int Varchar = 13;
    private const int IntegerNegative = 22;
    private const int SmallInteger = 32; // 32 à 47 : entiers de 0 à 15
    private const int ShortString = 68; // 68 à 99 : textes de 0 à 31 caractères

    private int _position;

    public byte Byte() => data[_position++];

    public void Skip(int count) => _position += count;

    public int Int32()
    {
        var value = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(_position, 4));
        _position += 4;
        return value;
    }

    public long Int64()
    {
        var value = BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(_position, 8));
        _position += 8;
        return value;
    }

    /// <summary>Entier de longueur variable : 7 bits par octet, les poids faibles d'abord.</summary>
    public long VarLong()
    {
        long result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            long next = Byte();
            result |= (next & 0x7F) << shift;
            if ((next & 0x80) == 0) return result;
        }

        throw new InvalidDataException("Entier trop long.");
    }

    public byte[] Rest() => data[_position..];

    /// <summary>Texte précédé de son nombre de caractères.</summary>
    public string String() => Characters(VarLong());

    /// <summary>Caractères en « UTF-8 modifié » : un à trois octets par caractère UTF-16.</summary>
    private string Characters(long count)
    {
        if (count < 0 || count > data.Length - _position) throw new InvalidDataException("Texte trop long.");
        var text = new StringBuilder((int)count);
        for (var i = 0; i < count; i++)
        {
            int first = Byte();
            if (first < 0x80) text.Append((char)first);
            else if (first >= 0xE0)
            {
                int second = Byte();
                int third = Byte();
                text.Append((char)(((first & 0x0F) << 12) | ((second & 0x3F) << 6) | (third & 0x3F)));
            }
            else text.Append((char)(((first & 0x1F) << 6) | (Byte() & 0x3F)));
        }

        return text.ToString();
    }

    /// <summary>Une valeur d'une ligne de la table système : le texte, ou null pour un entier ou NULL (sans utilité ici).</summary>
    public string? Value()
    {
        int type = Byte();
        switch (type)
        {
            case Null:
            case >= SmallInteger and < SmallInteger + 16:
                return null;
            case Integer:
            case IntegerNegative:
                VarLong();
                return null;
            case Varchar:
                return String();
            case >= ShortString and < ShortString + 32:
                return Characters(type - ShortString);
            default:
                throw new InvalidDataException($"Type de valeur inattendu : {type}.");
        }
    }
}
